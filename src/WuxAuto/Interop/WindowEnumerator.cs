using System.Diagnostics;
using System.Text;

namespace WuxAuto.Interop;

public sealed record WindowInfo(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string Title,
    int ClientWidth,
    int ClientHeight)
{
    public string Display
    {
        get
        {
            string title = Title.Length > 60 ? Title[..57] + "..." : Title;
            string size = ClientWidth > 0 && ClientHeight > 0 ? $"  [{ClientWidth}x{ClientHeight}]" : "";
            return $"{ProcessName}.exe  (pid {ProcessId}){size}  -  {title}";
        }
    }

    public string ShortDisplay => $"{ProcessName}.exe (pid {ProcessId})";

    public bool LooksLikeGame => ProcessName.Contains("wuxen", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Enumerates visible top-level windows the pilot can bind to.</summary>
public static class WindowEnumerator
{
    public static IReadOnlyList<WindowInfo> Enumerate(bool includeOwnWindow = false)
    {
        var results = new List<WindowInfo>();
        int ownPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            // Tool windows / owned popups are noise for this app.
            if (NativeMethods.GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero)
                return true;

            long exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            int cloaked = 0;
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int titleLength = NativeMethods.GetWindowTextLength(hWnd);
            if (titleLength <= 0)
                return true;

            var sb = new StringBuilder(titleLength + 2);
            if (NativeMethods.GetWindowText(hWnd, sb, sb.Capacity) <= 0)
                return true;

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || (!includeOwnWindow && pid == ownPid))
                return true;

            if (!NativeMethods.GetClientRect(hWnd, out var client) || client.Width < 200 || client.Height < 120)
                return true;

            string processName;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch
            {
                return true;
            }

            results.Add(new WindowInfo(hWnd, (int)pid, processName, sb.ToString(), client.Width, client.Height));
            return true;
        }, IntPtr.Zero);

        return results
            .OrderByDescending(w => w.LooksLikeGame)
            .ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TryGetClientRect(IntPtr hwnd, out int width, out int height, out int screenLeft, out int screenTop)
    {
        width = height = screenLeft = screenTop = 0;
        if (!NativeMethods.IsWindow(hwnd))
            return false;
        if (!NativeMethods.GetClientRect(hwnd, out var rect))
            return false;
        var origin = new NativeMethods.POINT(0, 0);
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
            return false;

        width = rect.Width;
        height = rect.Height;
        screenLeft = origin.X;
        screenTop = origin.Y;
        return width > 0 && height > 0;
    }

    public static bool IsForeground(IntPtr hwnd) => NativeMethods.GetForegroundWindow() == hwnd;

    /// <summary>Handle of the current foreground window (zero when the desktop is locked).</summary>
    public static IntPtr ForegroundWindow => NativeMethods.GetForegroundWindow();

    /// <summary>Title of the current foreground window, for log/diagnostic output.</summary>
    public static string ForegroundTitle()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return "(none)";

        var text = new System.Text.StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static bool IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd);

    /// <summary>Thread that owns the window (the thread keyboard focus belongs to).</summary>
    public static uint ThreadIdOf(IntPtr hwnd) => NativeMethods.GetWindowThreadProcessId(hwnd, out _);

    /// <summary>Direct child windows, used to spot input-carrying children of a game window.</summary>
    public static IReadOnlyList<IntPtr> ChildWindows(IntPtr hwnd, int limit = 8)
    {
        var children = new List<IntPtr>();
        IntPtr child = NativeMethods.GetWindow(hwnd, 5); // GW_CHILD

        while (child != IntPtr.Zero && children.Count < limit)
        {
            children.Add(child);
            child = NativeMethods.GetWindow(child, 2); // GW_HWNDNEXT
        }

        return children;
    }

    /// <summary>Win32 window class name, for diagnostics.</summary>
    public static string ClassName(IntPtr hwnd)
    {
        var text = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static bool ProcessIsAlive(IntPtr hwnd, int processId)
    {
        if (!NativeMethods.IsWindow(hwnd))
            return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)processId;
    }

    /// <summary>
    /// Tries hard to make the bound window the foreground window. Windows only lets the
    /// current foreground process do this, so a hotkey-triggered call can legitimately fail.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd, int timeoutMs = 1200)
    {
        if (!NativeMethods.IsWindow(hwnd))
            return false;

        if (NativeMethods.GetForegroundWindow() == hwnd)
            return true;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        if (!NativeMethods.IsWindowVisible(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);

        TryActivate(hwnd);
        if (WaitForeground(hwnd, Math.Min(timeoutMs, 450)))
            return true;

        // A process may only set the foreground window while it owns the foreground or has been
        // granted the right. A synthetic ALT tap lifts that lock for the following call - this is
        // the difference between "focus works when clicked" and "focus works from a hotkey".
        NativeMethods.PressAlt();
        TryActivate(hwnd);
        if (WaitForeground(hwnd, Math.Min(timeoutMs, 450)))
            return true;

        // Last resort: a temporary topmost toggle is allowed to pull the window forward. It is
        // dropped again immediately so the game does not stay above everything else.
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
        TryActivate(hwnd);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);

        return WaitForeground(hwnd, Math.Max(250, timeoutMs));
    }

    private static void TryActivate(IntPtr hwnd)
    {
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();
        bool attached = false;

        try
        {
            if (targetThread != 0 && targetThread != currentThread)
                attached = NativeMethods.AttachThreadInput(currentThread, targetThread, true);

            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.SetActiveWindow(hwnd);
            NativeMethods.SetFocus(hwnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private static bool WaitForeground(IntPtr hwnd, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            if (NativeMethods.GetForegroundWindow() == hwnd)
                return true;

            Thread.Sleep(20);
        }
        while (Environment.TickCount64 < deadline);

        return NativeMethods.GetForegroundWindow() == hwnd;
    }
}
