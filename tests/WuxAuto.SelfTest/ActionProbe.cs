using System.Diagnostics;
using System.IO;
using OpenCvSharp;
using WuxAuto.Capture;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Interop;

namespace WuxAuto.SelfTest;

/// <summary>
/// Scripted input exploration: sends keys/clicks to the game and saves a frame after every step,
/// so the UI flow can be mapped without the pilot. Steps are given as --do key:I, --do ctrlkey:1 (Ctrl+1),
/// --do click:661,285, --do rclick:800,450, --do wheel:960,600,-5 (negative = down), --do wait:500,
/// --do ctrlclick:960,400 (Ctrl+left click), --do ctrlring / --do ctrlbelt (LOD: Ctrl+click the boss ring /
/// belt on the last frame).
/// </summary>
internal static class ActionProbe
{
    public static async Task<int> RunAsync(IReadOnlyList<string> steps)
    {
        var windows = WindowEnumerator.Enumerate(includeOwnWindow: true);
        var target = windows.FirstOrDefault(w => w.LooksLikeGame);
        if (target == null)
        {
            Console.WriteLine("no Wuxen2 window found");
            return 1;
        }

        var settings = SettingsStore.Load();
        Console.WriteLine($"target: 0x{target.Handle:X} {target.ProcessName} client {target.ClientWidth}x{target.ClientHeight}, dry run {settings.Sequence.DryRun}");
        if (settings.Sequence.DryRun)
        {
            Console.WriteLine("dry run is on - the saved settings would suppress input; forcing it off for this probe");
            settings.Sequence.DryRun = false;
        }

        using var capture = new DesktopCaptureService { MaxFps = 15 };
        capture.Faulted += message => Console.WriteLine($"[capture] {message}");
        capture.Start(target.Handle, target.ProcessId);

        WindowEnumerator.ForceForeground(target.Handle, 1500);
        Console.WriteLine($"foreground: 0x{WindowEnumerator.ForegroundWindow:X} \"{WindowEnumerator.ForegroundTitle()}\"");

        var input = new WindowInput(settings);
        input.Log += (level, message) => Console.WriteLine($"  [{level}] {message}");
        input.Bind(target.Handle, target.ProcessId);

        using var previous = await GrabAsync(capture);
        if (previous == null)
            return 1;

        using var analyzer = new WuxAuto.Vision.ScreenAnalyzer(settings, new WuxAuto.Vision.OcrService());

        Save(previous, "step0-start.png");

        for (int i = 0; i < steps.Count; i++)
        {
            string step = steps[i];
            int colon = step.IndexOf(':');
            string verb = (colon < 0 ? step : step[..colon]).ToLowerInvariant();
            string argument = colon < 0 ? string.Empty : step[(colon + 1)..];
            var parts = argument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            try
            {
                switch (verb)
                {
                    case "key":
                        input.KeyTap(argument);
                        break;
                    case "ctrlkey": // Ctrl + key tap, e.g. ctrlkey:1 (the manual attacks' Ctrl + skill key)
                        input.CtrlKeyTap(argument);
                        break;
                    case "click":
                        input.Click(int.Parse(parts[0]), int.Parse(parts[1]));
                        break;
                    case "dclick":
                        input.DoubleClick(int.Parse(parts[0]), int.Parse(parts[1]));
                        break;
                    case "rclick":
                        input.Click(int.Parse(parts[0]), int.Parse(parts[1]), MouseButton.Right);
                        break;
                    case "ctrlclick":
                        input.CtrlClick(int.Parse(parts[0]), int.Parse(parts[1]));
                        break;
                    case "ctrlring": // LOD: Ctrl+click the boss ring's centre (the feet) found on the last frame
                    case "ctrlbelt": // LOD: Ctrl+click the boss's cyan belt found on the last frame
                    {
                        var spot = verb == "ctrlring" ? analyzer.FindBossRing(previous) : analyzer.FindBossBelt(previous);
                        Console.WriteLine($"  {verb[4..]}: {(spot == null ? "not found" : spot.Value.ToString())}");
                        if (spot is { } s)
                            input.CtrlClick(s.CenterX, s.CenterY);
                        break;
                    }
                    case "drag":
                        input.Drag(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                        break;
                    case "wheel": // wheel:x,y,notches - positive scrolls up, negative down
                        input.Wheel(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
                        break;
                    case "move":
                        // mouse move only: reuse click with a zero-length hold is not possible, log instead
                        Console.WriteLine("  (move is not supported; use click)");
                        break;
                    case "wait":
                        break;
                    default:
                        Console.WriteLine($"  unknown step '{step}'");
                        continue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  step '{step}' failed: {ex.Message}");
                continue;
            }

            int delay = verb == "wait" && parts.Length > 0 && int.TryParse(parts[0], out int waitMs) ? waitMs : 900;
            await Task.Delay(delay);

            using var frame = await GrabAsync(capture);
            if (frame == null)
            {
                Console.WriteLine($"  step {i + 1} '{step}': no frame");
                continue;
            }

            double changed = ChangedFraction(previous, frame);
            string name = $"step{i + 1}-{verb}.png";
            Save(frame, name);
            frame.CopyTo(previous);
            Console.WriteLine($"step {i + 1}: {step,-24} {changed,6:P1} changed -> {name}");
        }

        return 0;
    }

    private static async Task<Mat?> GrabAsync(DesktopCaptureService capture)
    {
        using var frame = await capture.WaitForFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        return frame == null
            ? null
            : Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Buffer).Clone();
    }

    private static void Save(Mat mat, string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "wuxauto-" + name);
        Cv2.ImWrite(path, mat);
    }

    private static double ChangedFraction(Mat a, Mat b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return 1;

        int width = a.Width, height = a.Height;
        int changed = 0;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                var p = a.At<Vec4b>(y, x);
                var q = b.At<Vec4b>(y, x);
                if (Math.Abs(p.Item0 - q.Item0) + Math.Abs(p.Item1 - q.Item1) + Math.Abs(p.Item2 - q.Item2) > 30)
                    changed++;
            }
        }

        return changed / (width * (double)height / 4);
    }
}
