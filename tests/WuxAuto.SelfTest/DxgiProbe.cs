using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WuxAuto.SelfTest;

/// <summary>
/// Prints what the machine actually exposes to DXGI/D3D11 and tries every device configuration the
/// capture service may pick, including DuplicateOutput. Run with: ... --dxgi
/// </summary>
internal static class DxgiProbe
{
    private static readonly FeatureLevel[] AllLevels =
    {
        FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0, FeatureLevel.Level_9_3, FeatureLevel.Level_9_2, FeatureLevel.Level_9_1,
    };

    private static readonly FeatureLevel[] SafeLevels =
    {
        FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        FeatureLevel.Level_9_3, FeatureLevel.Level_9_2, FeatureLevel.Level_9_1,
    };

    private const int D3D11SdkVersion = 7;
    private const uint D3DDriverTypeHardware = 1;
    private const uint D3DDriverTypeWarp = 5;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDevice_(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        int[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr context);

    public static void Run()
    {
        Console.WriteLine($"== D3D11 / DXGI probe ({(Environment.Is64BitProcess ? "64" : "32")}-bit process, {RuntimeInformation.ProcessArchitecture}, OS {RuntimeInformation.OSArchitecture}) ==");

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint a = 0; ; a++)
        {
            var adapterResult = factory.EnumAdapters1(a, out var adapter);
            if (adapterResult.Failure)
                break;

            var description = adapter.Description1;
            Console.WriteLine();
            Console.WriteLine($"adapter {a}: \"{description.Description}\" vendor=0x{description.VendorId:X4} device=0x{description.DeviceId:X4} flags={description.Flags}");

            for (uint o = 0; ; o++)
            {
                var outputResult = adapter.EnumOutputs(o, out var output);
                if (outputResult.Failure)
                    break;

                var outputDescription = output.Description;
                var rect = outputDescription.DesktopCoordinates;
                Console.WriteLine($"  output {o}: \"{outputDescription.DeviceName}\" {rect.Left},{rect.Top}-{rect.Right},{rect.Bottom} attached={outputDescription.AttachedToDesktop}");

                // With an explicit adapter D3D11 expects DriverType.Unknown (what the app uses on hybrid GPUs).
                TryDevice(adapter, output, "unknown/all-levels", DriverType.Unknown, AllLevels);
                TryDevice(adapter, output, "hw/all-levels", DriverType.Hardware, AllLevels);
                TryDevice(adapter, output, "hw/11.0-levels", DriverType.Hardware, SafeLevels);
                TryDevice(adapter, output, "hw/default-levels", DriverType.Hardware, null!);
                TryDevice(adapter, output, "warp", DriverType.Warp, SafeLevels);

                TryRaw(adapter, output, "raw hw/adapter", D3DDriverTypeHardware, new[] { 0xB100, 0xB000, 0xA100, 0xA000, 0x9300, 0x9200, 0x9100 });
                TryRaw(adapter, output, "raw hw/1-level", D3DDriverTypeHardware, new[] { 0xB000 });
                TryRaw(adapter, output, "raw hw/no-levels", D3DDriverTypeHardware, null);
                TryRaw(adapter, output, "raw warp/adapter", D3DDriverTypeWarp, null);

                output.Dispose();
            }

            adapter.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine("adapter=null (system default) device creation:");
        TryDefault("hw/null-adapter", DriverType.Hardware, SafeLevels);
        TryDefault("warp/null-adapter", DriverType.Warp, SafeLevels);

        // The fix candidate: a default-adapter device should be able to duplicate an output that is
        // owned by the same adapter the system picked.
        Console.WriteLine();
        Console.WriteLine("default-adapter device + DuplicateOutput:");
        TryDefaultDuplication(factory, "hw/null-adapter", DriverType.Hardware, SafeLevels);
    }

    private static void TryDefaultDuplication(IDXGIFactory1 factory, string label, DriverType driverType, FeatureLevel[] levels)
    {
        var result = D3D11.D3D11CreateDevice(null, driverType, DeviceCreationFlags.BgraSupport, levels,
            out var device, out var featureLevel, out var context);
        if (result.Failure)
        {
            Console.WriteLine($"    {label,-18} -> create FAILED {Format(result.Code)} {result.Description}");
            return;
        }

        using (device)
        using (context)
        {
            Console.WriteLine($"    {label,-18} -> create OK ({featureLevel})");
            for (uint a = 0; ; a++)
            {
                var adapterResult = factory.EnumAdapters1(a, out var adapter);
                if (adapterResult.Failure)
                    break;

                for (uint o = 0; ; o++)
                {
                    var outputResult = adapter.EnumOutputs(o, out var output);
                    if (outputResult.Failure)
                        break;

                    Console.WriteLine($"      {adapter.Description1.Description} / {output.Description.DeviceName}: {TryDuplicate(output, device)}");
                    output.Dispose();
                }

                adapter.Dispose();
            }
        }
    }

    /// <summary>Default adapter device + duplication of every output (this is the likely fallback path).</summary>
    private static void TryDefault(string label, DriverType driverType, FeatureLevel[] levels)
    {
        try
        {
            var result = D3D11.D3D11CreateDevice(null, driverType, DeviceCreationFlags.BgraSupport, levels,
                out var device, out var featureLevel, out var context);
            if (result.Failure)
            {
                Console.WriteLine($"    {label,-18} -> create FAILED {Format(result.Code)} {result.Description}");
                return;
            }

            using (device)
            using (context)
                Console.WriteLine($"    {label,-18} -> create OK ({featureLevel})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {label,-18} -> threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryDevice(IDXGIAdapter1 adapter, IDXGIOutput output, string label, DriverType driverType, FeatureLevel[] levels)
    {
        try
        {
            var result = D3D11.D3D11CreateDevice(adapter, driverType, DeviceCreationFlags.BgraSupport, levels,
                out var device, out var featureLevel, out var context);

            if (result.Failure)
            {
                Console.WriteLine($"    {label,-18} -> create FAILED {Format(result.Code)} {result.Description}");
                return;
            }

            using (device)
            using (context)
                Console.WriteLine($"    {label,-18} -> create OK ({featureLevel}), {TryDuplicate(output, device)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {label,-18} -> threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryRaw(IDXGIAdapter1 adapter, IDXGIOutput output, string label, uint driverType, int[]? levels)
    {
        try
        {
            int hr = D3D11CreateDevice_(
                adapter.NativePointer,
                driverType,
                IntPtr.Zero,
                D3D11CreateDeviceBgraSupport,
                levels,
                (uint)(levels?.Length ?? 0),
                D3D11SdkVersion,
                out var devicePointer,
                out int featureLevel,
                out var contextPointer);

            if (hr < 0)
            {
                Console.WriteLine($"    {label,-18} -> raw FAILED 0x{(uint)hr:X8}");
                return;
            }

            using var device = new ID3D11Device(devicePointer);
            using var context = new ID3D11DeviceContext(contextPointer);
            Console.WriteLine($"    {label,-18} -> raw OK (0x{featureLevel:X}), {TryDuplicate(output, device)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {label,-18} -> raw threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string TryDuplicate(IDXGIOutput output, ID3D11Device device)
    {
        try
        {
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            using var duplication = output1.DuplicateOutput(device);
            return "duplicate OK";
        }
        catch (Exception ex)
        {
            return $"duplicate FAILED: {ex.Message}";
        }
    }

    private static string Format(int code) => $"0x{(uint)code:X8}";
}
