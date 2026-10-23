using System.Buffers;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using WuxAuto.Core;
using WuxAuto.Interop;

namespace WuxAuto.Capture;

/// <summary>
/// Windows Graphics Capture of one window - the fallback when Desktop Duplication cannot start.
/// On NVIDIA Optimus laptops (an app running on the dGPU, the panel wired to the iGPU) every
/// DuplicateOutput fails with DXGI_ERROR_UNSUPPORTED (a friend's RTX 3060 laptop, 2026-10-21);
/// window capture works across adapters. The frame is the window incl. its frame, cropped to the
/// client area here so the rest of the app sees exactly what Desktop Duplication delivers.
/// </summary>
internal sealed class WindowCaptureSession : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;

    private WindowCaptureSession(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winrtDevice,
        GraphicsCaptureItem item, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session)
    {
        _device = device;
        _context = context;
        _winrtDevice = winrtDevice;
        _item = item;
        _pool = pool;
        _session = session;
        _poolSize = item.Size;
    }

    public string Backend => $"Windows Graphics Capture (window), {_item.Size.Width}x{_item.Size.Height}";

    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    public static WindowCaptureSession Create(IntPtr hwnd)
    {
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out var device, out _, out var context);
        if (result.Failure || device == null || context == null)
        {
            device?.Dispose();
            context?.Dispose();
            throw new CaptureException($"Window capture: D3D11CreateDevice failed 0x{(uint)result.Code:X8} ({result.Description}).");
        }

        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr rawDevice));
            var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(rawDevice);
            Marshal.Release(rawDevice);

            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
            var iid = GraphicsCaptureItemIid;
            IntPtr rawItem = interop.CreateForWindow(hwnd, ref iid);
            var item = GraphicsCaptureItem.FromAbi(rawItem);
            Marshal.Release(rawItem);

            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            var session = pool.CreateCaptureSession(item);
            try
            {
                session.IsCursorCaptureEnabled = false;
            }
            catch
            {
                // Older Windows 10 builds: the cursor is part of the image, harmless for detection.
            }

            // IsBorderRequired (no yellow frame) is not in the 19041 projection this app targets; the
            // frame is drawn around the window on screen, not into the captured image.

            session.StartCapture();
            return new WindowCaptureSession(device, context, winrtDevice, item, pool, session);
        }
        catch (Exception ex) when (ex is not CaptureException)
        {
            context.Dispose();
            device.Dispose();
            throw new CaptureException($"Window capture could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>The newest frame cropped to the window's client area, or null when none arrived.</summary>
    public CaptureFrame? TryRead(IntPtr hwnd, long index, out bool cropChanged)
    {
        cropChanged = false;
        using var frame = _pool.TryGetNextFrame();
        if (frame == null)
            return null;

        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            // The window was resized: the pool must match, this frame is skipped.
            _poolSize = frame.ContentSize;
            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            return null;
        }

        if (!WindowEnumerator.TryGetClientRect(hwnd, out int clientWidth, out int clientHeight, out int clientLeft, out int clientTop))
            return null;

        // The captured image starts at the window's visible frame (DWM extended frame bounds).
        int frameLeft = clientLeft, frameTop = clientTop;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var bounds, Marshal.SizeOf<NativeRect>()) == 0)
        {
            frameLeft = bounds.Left;
            frameTop = bounds.Top;
        }

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = Texture2DIid;
        IntPtr rawTexture = access.GetInterface(ref iid);
        using var texture = new ID3D11Texture2D(rawTexture);
        var desc = texture.Description;

        int x = Math.Clamp(clientLeft - frameLeft, 0, (int)desc.Width - 1);
        int y = Math.Clamp(clientTop - frameTop, 0, (int)desc.Height - 1);
        int width = Math.Min(clientWidth, (int)desc.Width - x);
        int height = Math.Min(clientHeight, (int)desc.Height - y);
        if (width < 8 || height < 8)
            return null;

        if (_staging == null || _stagingWidth != width || _stagingHeight != height)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
            _stagingWidth = width;
            _stagingHeight = height;
            cropChanged = true;
        }

        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(x, y, 0, x + width, y + height, 1));

        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = width * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(rowBytes * height);
            unsafe
            {
                byte* source = (byte*)mapped.DataPointer;
                fixed (byte* destination = buffer)
                {
                    for (int row = 0; row < height; row++)
                        Buffer.MemoryCopy(source + (long)row * mapped.RowPitch, destination + (long)row * rowBytes, rowBytes, rowBytes);
                }
            }

            return new CaptureFrame(buffer, width, height, frameLeft + x, frameTop + y, index);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        try
        {
            _session.Dispose();
            _pool.Dispose();
        }
        catch
        {
            // shutting down
        }

        _staging?.Dispose();
        _staging = null;
        (_winrtDevice as IDisposable)?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
