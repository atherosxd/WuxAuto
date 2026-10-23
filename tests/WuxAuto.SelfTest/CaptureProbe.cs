using System.Diagnostics;
using WuxAuto.Capture;
using WuxAuto.Interop;

namespace WuxAuto.SelfTest;

/// <summary>
/// Starts the real capture service against the first available window and reports the frames that
/// come out. Verifies device creation, duplication and the staging read-back without the UI.
/// Run with: ... --capture
/// </summary>
internal static class CaptureProbe
{
    public static async Task RunAsync()
    {
        var windows = WindowEnumerator.Enumerate(includeOwnWindow: true);
        Console.WriteLine($"{windows.Count} window(s):");
        foreach (var window in windows)
            Console.WriteLine($"  hwnd=0x{window.Handle:X} {window.ProcessName} [{window.ClientWidth}x{window.ClientHeight}] \"{window.Title}\"");

        var target = windows
            .FirstOrDefault(w => w.LooksLikeGame)
            ?? windows.OrderByDescending(w => w.ClientWidth * w.ClientHeight).FirstOrDefault();

        if (target == null)
        {
            Console.WriteLine("no window to capture");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"capturing hwnd=0x{target.Handle:X} ({target.ProcessName}, client {target.ClientWidth}x{target.ClientHeight})");

        using var capture = new DesktopCaptureService();
        capture.StatusChanged += message => Console.WriteLine($"[status] {message}");
        capture.Faulted += message => Console.WriteLine($"[fault]  {message}");
        capture.MaxFps = 10;

        capture.Start(target.Handle, target.ProcessId);

        var clock = Stopwatch.StartNew();
        int frames = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(8))
        {
            using var frame = await capture.WaitForFrameAsync(TimeSpan.FromMilliseconds(1200), CancellationToken.None);
            if (frame == null)
            {
                Console.WriteLine("  ... no frame (a locked desktop or a static screen produces none)");
                continue;
            }

            frames++;
            double sum = 0;
            for (int i = 0; i < frame.Buffer.Length; i += 4)
                sum += frame.Buffer[i];

            double mean = sum / (frame.Buffer.Length / 4.0);
            Console.WriteLine($"  frame {frame.Index}: {frame.Width}x{frame.Height} at screen {frame.ScreenLeft},{frame.ScreenTop}, mean blue {mean:0.0}");

            string output = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"wuxauto-capture-{frames}.png");
            using (var mat = OpenCvSharp.Mat.FromPixelData(frame.Height, frame.Width, OpenCvSharp.MatType.CV_8UC4, frame.Buffer))
                OpenCvSharp.Cv2.ImWrite(output, mat);
            Console.WriteLine($"    saved {output}");

            if (frames >= 2)
                break;
        }

        Console.WriteLine();
        Console.WriteLine($"captured {frames} frame(s); running={capture.IsRunning}, error={capture.LastError ?? "none"}");
    }
}
