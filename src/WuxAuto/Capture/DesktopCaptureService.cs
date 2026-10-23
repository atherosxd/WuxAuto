using System.Buffers;
using System.Diagnostics;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WuxAuto.Core;
using WuxAuto.Interop;

namespace WuxAuto.Capture;

public sealed class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A single BGRA frame of the bound window's client area, rented from the shared pool.
/// Call <see cref="Dispose"/> when done - the vision loop owns the lifetime.
/// </summary>
public sealed class CaptureFrame : IDisposable
{
    private bool _disposed;

    internal CaptureFrame(byte[] buffer, int width, int height, int screenLeft, int screenTop, long index)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = width * 4;
        ScreenLeft = screenLeft;
        ScreenTop = screenTop;
        Index = index;
        TimestampUtc = DateTime.UtcNow;
        Timestamp = Stopwatch.GetTimestamp();
    }

    public byte[] Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int ScreenLeft { get; }
    public int ScreenTop { get; }
    public long Index { get; }
    public DateTime TimestampUtc { get; }
    public long Timestamp { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>
/// Desktop Duplication (DXGI) capture for one window. The duplication API only hands out
/// frames when the desktop actually changes, which keeps idle CPU close to zero.
/// Runs on its own MTA thread; frames are exposed as an always-newest slot.
/// </summary>
public sealed class DesktopCaptureService : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private CaptureFrame? _latest;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private IntPtr _hwnd;
    private int _processId;
    private volatile bool _running;

    public event Action<string>? StatusChanged;
    public event Action<string>? Faulted;

    public bool IsRunning => _running;
    public int TargetProcessId => _processId;
    public IntPtr TargetWindow => _hwnd;
    public int FramesCaptured => _framesCaptured;
    public int FramesCoalesced => _framesCoalesced;
    public DateTime? LastFrameUtc => _lastFrameUtc;
    public int LastWidth => _lastWidth;
    public int LastHeight => _lastHeight;
    public string? LastError => _lastError;

    private int _framesCaptured;
    private int _framesCoalesced;
    private DateTime? _lastFrameUtc;
    private int _lastWidth;
    private int _lastHeight;
    private string? _lastError;

    /// <summary>Upper bound for frame processing; duplication can deliver at monitor refresh rate.</summary>
    public int MaxFps { get; set; } = 30;

    public void Start(IntPtr hwnd, int processId)
    {
        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("Capture already running.");

            _hwnd = hwnd;
            _processId = processId;
            _cts = new CancellationTokenSource();
            _running = true;

            var token = _cts.Token;
            _thread = new Thread(() => CaptureThread(token))
            {
                IsBackground = true,
                Name = "WuxAuto capture",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public async Task<CaptureFrame?> WaitForFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await _frameSignal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return Interlocked.Exchange(ref _latest, null);
    }

    private void Publish(CaptureFrame frame)
    {
        var replaced = Interlocked.Exchange(ref _latest, frame);
        if (replaced != null)
        {
            replaced.Dispose();
            Interlocked.Increment(ref _framesCoalesced);
        }

        Interlocked.Increment(ref _framesCaptured);
        _lastFrameUtc = frame.TimestampUtc;
        _lastWidth = frame.Width;
        _lastHeight = frame.Height;

        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A frame is already pending; the slot itself holds the newest image.
        }
    }

    /// <summary>
    /// After AccessLost: Windows refuses duplication (E_ACCESSDENIED) while a secure desktop is up -
    /// lock screen, UAC prompt, Ctrl+Alt+Del. That once killed a run at stage 3; keep retrying every
    /// 2 s until capture is allowed again. Null only when the capture is being stopped.
    /// </summary>
    private DuplicationSession? RecreateSession(CancellationToken token)
    {
        Thread.Sleep(400);
        int tries = 0;
        while (!token.IsCancellationRequested)
        {
            if (!NativeMethods.IsWindow(_hwnd))
                throw new CaptureException("The bound window no longer exists.");

            try
            {
                var session = DuplicationSession.Create(_hwnd);
                if (tries > 0)
                    StatusChanged?.Invoke($"Desktop Duplication ready again after {tries} retr{(tries == 1 ? "y" : "ies")}: {session.Backend}.");
                return session;
            }
            catch (Exception ex)
            {
                if (tries++ % 15 == 0)
                    StatusChanged?.Invoke($"Capture not allowed right now (locked screen or a Windows prompt?) - retrying every 2 s. {FirstLine(ex.Message)}");
                Thread.Sleep(2000);
            }
        }

        return null;
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(new[] { '\r', '\n' });
        return end < 0 ? text : text[..end];
    }

    private void CaptureThread(CancellationToken token)
    {
        DuplicationSession? session = null;
        var lastPublish = 0L;
        long frameIndex = 0;

        try
        {
            // Window capture instead when duplication cannot start (Optimus laptops) or when asked
            // to (WUXAUTO_CAPTURE=window, for testing that path).
            bool forceWindow = string.Equals(Environment.GetEnvironmentVariable("WUXAUTO_CAPTURE"), "window", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (forceWindow)
                    throw new CaptureException("WUXAUTO_CAPTURE=window");
                session = DuplicationSession.Create(_hwnd);
            }
            catch (CaptureException ex) when (WindowCaptureSession.IsSupported)
            {
                StatusChanged?.Invoke($"Desktop Duplication unavailable ({FirstLine(ex.Message)}) - using window capture instead.");
                WindowCaptureLoop(token);
                return;
            }

            StatusChanged?.Invoke($"Desktop Duplication ready: {session.Backend}.");

            while (!token.IsCancellationRequested)
            {
                if (!NativeMethods.IsWindow(_hwnd) ||
                    !WindowEnumerator.TryGetClientRect(_hwnd, out int clientWidth, out int clientHeight, out int screenLeft, out int screenTop))
                {
                    if (!NativeMethods.IsWindow(_hwnd))
                        throw new CaptureException("The bound window no longer exists.");

                    Thread.Sleep(150);
                    continue;
                }

                if (clientWidth < 8 || clientHeight < 8)
                {
                    // Minimized; nothing useful to capture.
                    Thread.Sleep(150);
                    continue;
                }

                int minFrameMs = Math.Max(1, 1000 / Math.Clamp(MaxFps, 1, 240));
                if (Stopwatch.GetTimestamp() - lastPublish < minFrameMs * (Stopwatch.Frequency / 1000))
                {
                    Thread.Sleep(2);
                    continue;
                }

                var crop = session.IntersectWithOutput(screenLeft, screenTop, clientWidth, clientHeight);
                if (crop.Width < 8 || crop.Height < 8)
                {
                    Thread.Sleep(100);
                    continue;
                }

                var result = session.Acquire(120, out var frameInfo, out var resource);

                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                    continue;

                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    StatusChanged?.Invoke("Desktop changed (resolution/mode, lock screen or UAC); reinitializing capture…");
                    session.Dispose();
                    session = RecreateSession(token);
                    if (session == null)
                        return;
                    continue;
                }

                if (result.Failure)
                    throw new CaptureException($"AcquireNextFrame failed: {result.Description}");

                try
                {
                    // Zero accumulated frames means only the cursor moved.
                    if (frameInfo.AccumulatedFrames == 0 || frameInfo.LastPresentTime == 0)
                        continue;

                    if (frameInfo.ProtectedContentMaskedOut)
                    {
                        StatusChanged?.Invoke("Frame is protected content and cannot be captured.");
                        continue;
                    }

                    var frame = session.ReadCrop(resource, crop, ++frameIndex, out bool cropChanged);
                    if (cropChanged)
                        StatusChanged?.Invoke($"Capture area changed to {crop.Width}x{crop.Height}.");

                    Publish(frame);
                    lastPublish = Stopwatch.GetTimestamp();
                }
                finally
                {
                    session.ReleaseFrame();
                    resource.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            if (!token.IsCancellationRequested)
                Faulted?.Invoke(ex is CaptureException ? ex.Message : $"Capture failed: {ex.Message}");
        }
        finally
        {
            session?.Dispose();
            _running = false;
        }
    }

    /// <summary>The capture loop for <see cref="WindowCaptureSession"/>: same throttle, same frames.</summary>
    private void WindowCaptureLoop(CancellationToken token)
    {
        using var window = WindowCaptureSession.Create(_hwnd);
        StatusChanged?.Invoke($"Window capture ready: {window.Backend}.");
        var lastPublish = 0L;
        long frameIndex = 0;

        while (!token.IsCancellationRequested)
        {
            if (!NativeMethods.IsWindow(_hwnd))
                throw new CaptureException("The bound window no longer exists.");

            int minFrameMs = Math.Max(1, 1000 / Math.Clamp(MaxFps, 1, 240));
            if (Stopwatch.GetTimestamp() - lastPublish < minFrameMs * (Stopwatch.Frequency / 1000))
            {
                Thread.Sleep(2);
                continue;
            }

            var frame = window.TryRead(_hwnd, frameIndex + 1, out bool cropChanged);
            if (frame == null)
            {
                Thread.Sleep(5);
                continue;
            }

            frameIndex++;
            if (cropChanged)
                StatusChanged?.Invoke($"Capture area changed to {frame.Width}x{frame.Height}.");

            Publish(frame);
            lastPublish = Stopwatch.GetTimestamp();
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Thread? thread;
        lock (_gate)
        {
            cts = _cts;
            thread = _thread;
            _cts = null;
            _thread = null;
            _running = false;
        }

        try
        {
            cts?.Cancel();
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
                thread.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore shutdown races
        }
        finally
        {
            cts?.Dispose();
            var pending = Interlocked.Exchange(ref _latest, null);
            pending?.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _frameSignal.Dispose();
    }

    /// <summary>One D3D11 device + duplication object tied to the monitor that hosts the window.</summary>
    private sealed class DuplicationSession : IDisposable
    {
        private static readonly FeatureLevel[] FeatureLevels =
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0, FeatureLevel.Level_9_3, FeatureLevel.Level_9_2, FeatureLevel.Level_9_1,
        };

        private static readonly FeatureLevel[] FeatureLevelsNo11 =
        {
            FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
            FeatureLevel.Level_9_3, FeatureLevel.Level_9_2, FeatureLevel.Level_9_1,
        };

        private ID3D11Device _device = null!;
        private ID3D11DeviceContext _context = null!;
        private IDXGIOutputDuplication _duplication = null!;
        private ID3D11Texture2D? _staging;
        private RawRect _monitorBounds;
        private int _stagingWidth;
        private int _stagingHeight;

        /// <summary>Which device configuration worked, e.g. "default adapter (hardware), feature level Level_11_0".</summary>
        public string Backend { get; private set; } = "unknown";

        public static DuplicationSession Create(IntPtr hwnd)
        {
            var session = new DuplicationSession();
            try
            {
                session.Initialize(hwnd);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private void Initialize(IntPtr hwnd)
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
                throw new CaptureException("Could not resolve the monitor that hosts the game window.");

            _monitorBounds = new RawRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);

            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var (adapter, output) = FindOutput(factory)
                ?? throw new CaptureException("No DXGI output matches the monitor hosting the game window.");

            try
            {
                var failures = new List<string>();

                // The *default* adapter is tried first on purpose: Windows on ARM (x64 emulation) and
                // a few hybrid-GPU drivers reject D3D11CreateDevice when an adapter is passed
                // explicitly (E_INVALIDARG) although the very same device is created fine when the
                // adapter is left to the OS. DuplicateOutput then fails if the output genuinely
                // belongs to another adapter, in which case the explicit adapter is tried next.
                // With an explicit adapter D3D11 wants DriverType.Unknown - Hardware plus an adapter is
                // E_INVALIDARG by design, so that fallback never worked: on a hybrid-GPU laptop
                // (default adapter = the dGPU, monitor on the iGPU: DXGI_ERROR_UNSUPPORTED) capture
                // could not start at all (a friend's PC, 2026-10-21).
                if (!TryCreateDevice(null, DriverType.Hardware, "default adapter (hardware)", output, failures) &&
                    !TryCreateDevice(adapter, DriverType.Unknown, "output adapter", output, failures) &&
                    !TryCreateDevice(adapter, DriverType.Hardware, "output adapter (hardware)", output, failures) &&
                    !TryCreateDevice(null, DriverType.Warp, "software (WARP)", output, failures))
                {
                    throw new CaptureException(
                        "Desktop Duplication could not be started." + Environment.NewLine +
                        string.Join(Environment.NewLine, failures.Select(f => "  - " + f)) + Environment.NewLine +
                        "Make sure the game runs windowed or borderless windowed. Full diagnostics: WuxAuto.SelfTest --dxgi");
                }
            }
            finally
            {
                output.Dispose();
                adapter.Dispose();
            }

            var dupDesc = _duplication.Description;
            if (dupDesc.Rotation != ModeRotation.Identity && dupDesc.Rotation != ModeRotation.Unspecified)
            {
                // Rare on desktop monitors; rotating would need an extra pass.
                throw new CaptureException($"Monitor rotation ({dupDesc.Rotation}) is not supported. Use a landscape monitor.");
            }
        }

        /// <summary>The DXGI adapter/output pair that covers the monitor hosting the window.</summary>
        private (IDXGIAdapter1 Adapter, IDXGIOutput Output)? FindOutput(IDXGIFactory1 factory)
        {
            for (uint a = 0; ; a++)
            {
                var adapterResult = factory.EnumAdapters1(a, out var adapter);
                if (adapterResult.Failure)
                    return null;

                for (uint o = 0; ; o++)
                {
                    var outputResult = adapter.EnumOutputs(o, out var output);
                    if (outputResult.Failure)
                        break;

                    var bounds = output.Description.DesktopCoordinates;
                    if (bounds.Left == _monitorBounds.Left && bounds.Top == _monitorBounds.Top &&
                        bounds.Right == _monitorBounds.Right && bounds.Bottom == _monitorBounds.Bottom)
                    {
                        return (adapter, output);
                    }

                    output.Dispose();
                }

                adapter.Dispose();
            }
        }

        /// <summary>
        /// Creates the device and the duplication for one candidate configuration, keeping the
        /// objects only on success. Failures are collected so the caller can report all of them.
        /// </summary>
        private bool TryCreateDevice(
            IDXGIAdapter1? adapter,
            DriverType driverType,
            string label,
            IDXGIOutput output,
            List<string> failures)
        {
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            IDXGIOutput1? output1 = null;
            IDXGIOutputDuplication? duplication = null;

            try
            {
                var result = D3D11.D3D11CreateDevice(adapter, driverType, DeviceCreationFlags.BgraSupport, FeatureLevels,
                    out device, out var featureLevel, out context);

                // Feature level 11.1 needs the D3D11.1 runtime; retry without it when the OS
                // rejects the whole parameter block.
                if ((uint)result.Code == 0x80070057u)
                {
                    device?.Dispose();
                    context?.Dispose();
                    device = null;
                    context = null;
                    result = D3D11.D3D11CreateDevice(adapter, driverType, DeviceCreationFlags.BgraSupport, FeatureLevelsNo11,
                        out device, out featureLevel, out context);
                }

                if (result.Failure || device == null || context == null)
                {
                    failures.Add($"{label}: D3D11CreateDevice 0x{(uint)result.Code:X8} ({result.Description})");
                    device?.Dispose();
                    context?.Dispose();
                    return false;
                }

                output1 = output.QueryInterface<IDXGIOutput1>();
                duplication = output1.DuplicateOutput(device);

                _device = device;
                _context = context;
                _duplication = duplication;
                Backend = $"{label}, feature level {featureLevel}, {output.Description.DeviceName}";
                return true;
            }
            catch (Exception ex)
            {
                failures.Add($"{label}: {ex.Message}");
                duplication?.Dispose();
                device?.Dispose();
                context?.Dispose();
                return false;
            }
            finally
            {
                output1?.Dispose();
            }
        }

        public PixelRect IntersectWithOutput(int left, int top, int width, int height)
        {
            int l = Math.Max(left, _monitorBounds.Left);
            int t = Math.Max(top, _monitorBounds.Top);
            int r = Math.Min(left + width, _monitorBounds.Right);
            int b = Math.Min(top + height, _monitorBounds.Bottom);
            return new PixelRect(l - _monitorBounds.Left, t - _monitorBounds.Top, Math.Max(0, r - l), Math.Max(0, b - t));
        }

        public Result Acquire(int timeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource resource)
            => _duplication.AcquireNextFrame((uint)timeoutMs, out frameInfo, out resource);

        public void ReleaseFrame()
        {
            _duplication.ReleaseFrame();
        }

        public CaptureFrame ReadCrop(IDXGIResource resource, PixelRect crop, long index, out bool cropChanged)
        {
            cropChanged = false;
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var desc = texture.Description;

            // Guard against the desktop being resized between Acquire and copy.
            if (crop.Right > desc.Width || crop.Bottom > desc.Height)
                throw new CaptureException("Frame bounds are outside the duplicated output.");

            if (_staging == null || _stagingWidth != crop.Width || _stagingHeight != crop.Height)
            {
                _staging?.Dispose();
                _staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)crop.Width,
                    Height = (uint)crop.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                });
                _stagingWidth = crop.Width;
                _stagingHeight = crop.Height;
                cropChanged = true;
            }

            _context.CopySubresourceRegion(
                _staging,
                0,
                0, 0, 0,
                texture,
                0,
                new Box(crop.X, crop.Y, 0, crop.Right, crop.Bottom, 1));

            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = crop.Width * 4;
                var buffer = ArrayPool<byte>.Shared.Rent(rowBytes * crop.Height);
                unsafe
                {
                    byte* source = (byte*)mapped.DataPointer;
                    fixed (byte* destination = buffer)
                    {
                        for (int y = 0; y < crop.Height; y++)
                        {
                            Buffer.MemoryCopy(
                                source + (long)y * mapped.RowPitch,
                                destination + (long)y * rowBytes,
                                rowBytes,
                                rowBytes);
                        }
                    }
                }

                int screenLeft = _monitorBounds.Left + crop.X;
                int screenTop = _monitorBounds.Top + crop.Y;
                return new CaptureFrame(buffer, crop.Width, crop.Height, screenLeft, screenTop, index);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        public void Dispose()
        {
            _staging?.Dispose();
            _staging = null;
            _duplication?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
