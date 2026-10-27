using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Emgu.CV;
using WuxAuto.Capture;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Interop;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>
/// End-to-end input check against the real game: focus the window, tap the inventory key, then
/// compare the frame before and after. Tells you whether the key reached the game at all.
/// Run with: ... --input [key]
/// </summary>
internal static class InputProbe
{
    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int max);

    public static async Task<int> RunAsync(string[] keys)
    {
        var windows = WindowEnumerator.Enumerate(includeOwnWindow: true);
        var target = windows.FirstOrDefault(w => w.LooksLikeGame);
        if (target == null)
        {
            Console.WriteLine("no Wuxen2 window found; open the game first");
            return 1;
        }

        var settings = SettingsStore.Load();
        Console.WriteLine($"target     : 0x{target.Handle:X} {target.ProcessName} pid {target.ProcessId} client {target.ClientWidth}x{target.ClientHeight} class {ClassName(target.Handle)}");
        Console.WriteLine($"input mode : {settings.Input.Mode}, require foreground {settings.Input.RequireForeground}, focus on start {settings.Input.FocusOnStart}, dry run {settings.Sequence.DryRun}");
        Console.WriteLine($"children   : {DescribeChildren(target.Handle)}");

        bool selfElevated = ProcessInfo.IsElevated;
        bool targetElevated = ProcessInfo.IsProcessElevated(target.ProcessId);
        Console.WriteLine($"elevation  : WuxAuto={(selfElevated ? "admin" : "user")}, {target.ProcessName}={(targetElevated ? "admin" : "user")}");
        if (targetElevated && !selfElevated)
            Console.WriteLine("  !! the game is elevated and this process is not - Windows drops injected input (UIPI). Run WuxAuto as administrator.");

        Console.WriteLine($"foreground : 0x{WindowEnumerator.ForegroundWindow:X} \"{WindowEnumerator.ForegroundTitle()}\"");

        using var capture = new DesktopCaptureService { MaxFps = 15 };
        capture.Faulted += message => Console.WriteLine($"[capture] {message}");
        capture.Start(target.Handle, target.ProcessId);

        using var before = await GrabAsync(capture);
        if (before == null)
        {
            Console.WriteLine("no frame captured");
            return 1;
        }

        Save(before, "input-before.png");

        var clock = Stopwatch.StartNew();
        bool focused = WindowEnumerator.ForceForeground(target.Handle, 1500);
        Console.WriteLine($"focus      : {focused} after {clock.ElapsedMilliseconds}ms, foreground 0x{WindowEnumerator.ForegroundWindow:X} \"{WindowEnumerator.ForegroundTitle()}\"");
        Console.WriteLine($"keyboard   : {DescribeFocus(target.ProcessId, target.Handle)}");

        var input = new WindowInput(settings);
        input.Log += (level, message) => Console.WriteLine($"  [{level}] {message}");
        input.Bind(target.Handle, target.ProcessId);

        using var previous = before.Clone();
        foreach (string key in keys)
        {
            bool sent = input.KeyTap(key);
            await Task.Delay(1200);

            using var after = await GrabAsync(capture) ?? await GrabAsync(capture);
            if (after == null)
            {
                Console.WriteLine($"key '{key}': no frame captured");
                continue;
            }

            double changed = ChangedFraction(previous, after);
            string name = $"input-after-{key}.png";
            Save(after, name);
            Console.WriteLine($"key '{key}'{(sent ? "" : " (SUPPRESSED)")}: {changed:P1} of pixels changed -> %TEMP%\\wuxauto-{name}");
            after.CopyTo(previous);
        }

        Console.WriteLine();
        Console.WriteLine("if nothing changed at all, the game ignores synthetic input in this mode:");
        Console.WriteLine("  - run both as administrator, or");
        Console.WriteLine("  - tick 'Loop'? no - try mode 'Background messages' in settings, or");
        Console.WriteLine("  - confirm the hotkey is actually bound to that action inside the game.");

        return 0;
    }

    private static string ClassName(IntPtr hwnd) => WindowEnumerator.ClassName(hwnd);

    private static string DescribeChildren(IntPtr hwnd)
    {
        var children = WindowEnumerator.ChildWindows(hwnd);
        return children.Count == 0
            ? "(none)"
            : string.Join(", ", children.Select(c => $"0x{c:X} {ClassName(c)}"));
    }

    /// <summary>Which window inside the game thread currently owns the keyboard focus.</summary>
    private static string DescribeFocus(int processId, IntPtr topLevel)
    {
        uint thread = WindowEnumerator.ThreadIdOf(topLevel);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info))
            return "GetGUIThreadInfo failed";

        return $"thread {thread}: focus=0x{info.hwndFocus:X} ({(info.hwndFocus == IntPtr.Zero ? "NOTHING has keyboard focus - injected keys are dropped" : ClassName(info.hwndFocus))}), " +
               $"active=0x{info.hwndActive:X}{(info.hwndActive == topLevel ? " (the game)" : "")}";
    }

    private static string ForegroundLabel()
    {
        var hwnd = WindowEnumerator.ForegroundWindow;
        if (hwnd == IntPtr.Zero)
            return "(none)";

        return $"\"{WindowEnumerator.ForegroundTitle()}\"";
    }

    private static async Task<Mat?> GrabAsync(DesktopCaptureService capture)
    {
        using var frame = await capture.WaitForFrameAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        if (frame == null)
            return null;

        return CaptureMats.Copy(frame);
    }

    private static void Save(Mat mat, string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "wuxauto-" + name);
        ImageFile.Write(path, mat);
        Console.WriteLine($"frame      : {path}");
    }

    private static double ChangedFraction(Mat a, Mat b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return 1;

        int changed = 0;
        int width = a.Width, height = a.Height;
        long total = width * (long)height;
        byte[] pa = CvHelpers.Bytes(a), pb = CvHelpers.Bytes(b);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]) > 30)
                    changed++;
            }
        }

        return changed / (double)total;
    }
}
