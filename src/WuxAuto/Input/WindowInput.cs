using System.Runtime.InteropServices;
using System.Windows.Input;
using WuxAuto.Core;
using WuxAuto.Interop;
using InputMode = WuxAuto.Core.InputMode;

namespace WuxAuto.Input;

public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// Sends keyboard/mouse input to exactly one window. In the default mode it refuses to
/// fire unless the bound HWND is the foreground window, which is what keeps stray
/// keystrokes from landing in Discord/Chrome/whatever else may be open.
/// </summary>
public sealed class WindowInput
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, (int VirtualKey, int ScanCode)> _keyCache = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSuppressionLog;

    public WindowInput(AppSettings settings)
    {
        _settings = settings;
    }

    public event Action<LogLevel, string>? Log;

    public IntPtr TargetWindow { get; private set; }
    public int TargetProcessId { get; private set; }
    public bool IsBound => TargetWindow != IntPtr.Zero && WindowEnumerator.ProcessIsAlive(TargetWindow, TargetProcessId);

    public bool IsTargetForeground => IsBound && NativeMethods.GetForegroundWindow() == TargetWindow;

    public bool IsDryRun => _settings.Sequence.DryRun;

    public void Bind(IntPtr hwnd, int processId)
    {
        TargetWindow = hwnd;
        TargetProcessId = processId;
        _keyCache.Clear();
    }

    public void Unbind()
    {
        TargetWindow = IntPtr.Zero;
        TargetProcessId = 0;
        _keyCache.Clear();
    }

    public bool TryFocus(int timeoutMs = 1200)
    {
        if (!IsBound)
            return false;

        bool ok = WindowEnumerator.ForceForeground(TargetWindow, timeoutMs);
        if (!ok)
            Log?.Invoke(LogLevel.Warning, "Windows refused to bring the game to the foreground. Click the game once, then retry.");
        return ok;
    }

    public bool KeyTap(string keyName)
    {
        if (!KeyDown(keyName))
            return false;
        Thread.Sleep(_settings.Input.KeyHoldMs);
        return KeyUp(keyName);
    }

    public bool KeyDown(string keyName)
    {
        if (!TryPrepare($"press {keyName}"))
            return false;

        if (IsDryRun)
        {
            Log?.Invoke(LogLevel.Trace, $"[dry run] key down {keyName}");
            return true;
        }

        var key = ResolveKey(keyName);
        if (key == null)
            return false;

        if (_settings.Input.Mode == InputMode.BackgroundMessages)
        {
            PostKey(NativeMethods.WM_KEYDOWN, key.Value.VirtualKey, key.Value.ScanCode);
            return true;
        }

        SendKeyboard(key.Value.VirtualKey, key.Value.ScanCode, down: true);
        return true;
    }

    public bool KeyUp(string keyName)
    {
        if (!IsBound)
            return false;

        if (IsDryRun)
            return true;

        var key = ResolveKey(keyName);
        if (key == null)
            return false;

        if (_settings.Input.Mode == InputMode.BackgroundMessages)
        {
            PostKey(NativeMethods.WM_KEYUP, key.Value.VirtualKey, key.Value.ScanCode);
            return true;
        }

        SendKeyboard(key.Value.VirtualKey, key.Value.ScanCode, down: false);
        return true;
    }

    public bool Click(int clientX, int clientY, MouseButton button = MouseButton.Left)
    {
        if (!TryPrepare($"click at {clientX},{clientY}"))
            return false;

        if (IsDryRun)
        {
            Log?.Invoke(LogLevel.Trace, $"[dry run] click {button} at client {clientX},{clientY}");
            return true;
        }

        if (_settings.Input.Mode == InputMode.BackgroundMessages)
        {
            PostClick(clientX, clientY, button);
            return true;
        }

        return SendClick(clientX, clientY, button);
    }

    /// <summary>
    /// Left click with Ctrl held. In this client it walks to the clicked spot instead of attacking
    /// what is under the cursor (LOD: walk up to the boss, user 2026-10-24). Verified with the default
    /// foreground input; posted messages carry no Ctrl state, so background mode may just click.
    /// </summary>
    public bool CtrlClick(int clientX, int clientY)
    {
        if (!KeyDown("LeftCtrl"))
            return false;

        try
        {
            Thread.Sleep(40);
            return Click(clientX, clientY);
        }
        finally
        {
            Thread.Sleep(40);
            KeyUp("LeftCtrl");
        }
    }

    /// <summary>
    /// Two quick left clicks. Most MMOs only run a consumable (teleport scroll) on a double click;
    /// a single click just picks the item up and leaves it hanging on the cursor.
    /// </summary>
    public bool DoubleClick(int clientX, int clientY, MouseButton button = MouseButton.Left)
    {
        if (!TryPrepare($"double click at {clientX},{clientY}"))
            return false;

        if (IsDryRun)
        {
            Log?.Invoke(LogLevel.Trace, $"[dry run] double click {button} at client {clientX},{clientY}");
            return true;
        }

        if (_settings.Input.Mode == InputMode.BackgroundMessages)
        {
            PostClick(clientX, clientY, button);
            Thread.Sleep(70);
            PostClick(clientX, clientY, button);
            return true;
        }

        return SendClick(clientX, clientY, button, doubleClick: true);
    }

    /// <summary>Right-button drag, used to turn the camera when key rotation is disabled.</summary>
    public bool Drag(int fromX, int fromY, int toX, int toY, int durationMs = 260)
    {
        if (!TryPrepare($"drag {fromX},{fromY} -> {toX},{toY}"))
            return false;

        if (IsDryRun)
        {
            Log?.Invoke(LogLevel.Trace, $"[dry run] right drag to {toX},{toY}");
            return true;
        }

        if (!TryClientToScreen(fromX, fromY, out int sx, out int sy) ||
            !TryClientToScreen(toX, toY, out int ex, out int ey))
            return false;

        if (_settings.Input.Mode == InputMode.BackgroundMessages)
        {
            PostClick(fromX, fromY, MouseButton.Right, downOnly: true);
            PostClick(toX, toY, MouseButton.Right, upOnly: true);
            return true;
        }

        var saved = NativeMethods.GetCursorPos(out var before) ? before : (NativeMethods.POINT?)null;

        MoveAbsolute(sx, sy);
        Thread.Sleep(_settings.Input.MoveSettleMs);
        SendMouseButton(MouseButton.Right, down: true, sx, sy);

        int steps = Math.Clamp(durationMs / 16, 2, 40);
        for (int i = 1; i <= steps; i++)
        {
            int mx = sx + (ex - sx) * i / steps;
            int my = sy + (ey - sy) * i / steps;
            MoveAbsolute(mx, my);
            Thread.Sleep(Math.Max(1, durationMs / steps));
        }

        SendMouseButton(MouseButton.Right, down: false, ex, ey);

        if (_settings.Input.RestoreCursor && saved.HasValue)
            NativeMethods.SetCursorPos(saved.Value.X, saved.Value.Y);

        return true;
    }

    /// <summary>
    /// Scrolls the mouse wheel over a client point. Used to page the map's monster list, which is
    /// far more reliable than clicking the tiny up/down arrows next to the "Spawns" header.
    /// </summary>
    public bool Wheel(int clientX, int clientY, int notches)
    {
        if (IsDryRun)
            return true;

        if (!TryPrepare("scroll"))
            return false;

        if (!TryClientToScreen(clientX, clientY, out int screenX, out int screenY))
            return false;

        var saved = NativeMethods.GetCursorPos(out var before) ? before : (NativeMethods.POINT?)null;

        MoveAbsolute(screenX, screenY);
        Thread.Sleep(_settings.Input.MoveSettleMs);

        for (int i = 0; i < Math.Abs(notches); i++)
        {
            SendWheel(notches > 0 ? 120 : -120);
            Thread.Sleep(60);
        }

        if (_settings.Input.RestoreCursor && saved.HasValue)
            NativeMethods.SetCursorPos(saved.Value.X, saved.Value.Y);

        return true;
    }

    private static void SendWheel(int delta)
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)delta),
                        dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
                    },
                },
            },
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private bool SendClick(int clientX, int clientY, MouseButton button, bool doubleClick = false)
    {
        if (!TryClientToScreen(clientX, clientY, out int screenX, out int screenY))
            return false;

        var saved = NativeMethods.GetCursorPos(out var before) ? before : (NativeMethods.POINT?)null;

        MoveAbsolute(screenX, screenY);
        Thread.Sleep(_settings.Input.MoveSettleMs);

        int clicks = doubleClick ? 2 : 1;
        for (int i = 0; i < clicks; i++)
        {
            if (i > 0)
                Thread.Sleep(70);

            SendMouseButton(button, down: true, screenX, screenY);
            Thread.Sleep(_settings.Input.ClickHoldMs);
            SendMouseButton(button, down: false, screenX, screenY);
        }

        if (_settings.Input.RestoreCursor && saved.HasValue)
            NativeMethods.SetCursorPos(saved.Value.X, saved.Value.Y);

        return true;
    }

    private bool TryPrepare(string action)
    {
        if (!IsBound)
        {
            Log?.Invoke(LogLevel.Warning, $"Not bound - cannot {action}.");
            return false;
        }

        if (IsDryRun)
            return true;

        if (_settings.Input.Mode == InputMode.BackgroundMessages)
            return true;

        if (!_settings.Input.RequireForeground)
            return true;

        if (IsTargetForeground)
            return true;

        long now = Environment.TickCount64;
        if (now - _lastSuppressionLog > 2500)
        {
            _lastSuppressionLog = now;
            Log?.Invoke(LogLevel.Warning, $"Input blocked: '{action}' was suppressed because the bound window is not in front.");
        }

        return false;
    }

    private (int VirtualKey, int ScanCode)? ResolveKey(string name)
    {
        if (_keyCache.TryGetValue(name, out var cached))
            return cached;

        try
        {
            // "1" means the top-row 1 key (Key.D1), never the enum value 1.
            string keyName = name.Length == 1 && char.IsDigit(name[0]) ? "D" + name
                : name is "`" or "~" ? "Oem3"
                : name;
            var converter = new KeyConverter();
            if (converter.ConvertFromString(keyName) is not Key key || key == Key.None)
            {
                Log?.Invoke(LogLevel.Error, $"Unknown key name '{name}'.");
                return null;
            }

            int vk = KeyInterop.VirtualKeyFromKey(key);
            int scan = (int)NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
            var result = (vk, scan);
            _keyCache[name] = result;
            return result;
        }
        catch
        {
            Log?.Invoke(LogLevel.Error, $"Unknown key name '{name}'.");
            return null;
        }
    }

    private bool TryClientToScreen(int clientX, int clientY, out int screenX, out int screenY)
    {
        var point = new NativeMethods.POINT(clientX, clientY);
        if (!NativeMethods.ClientToScreen(TargetWindow, ref point))
        {
            screenX = screenY = 0;
            Log?.Invoke(LogLevel.Warning, "Could not translate client coordinates to screen coordinates.");
            return false;
        }

        screenX = point.X;
        screenY = point.Y;
        return true;
    }

    private static (int X, int Y, int W, int H) VirtualScreen()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return (x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static bool IsExtendedKey(int vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or // nav cluster
        0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5; // insert/del/win/app/num-div/rctrl/ralt

    private static void SendKeyboard(int vk, int scan, bool down)
    {
        uint flags = NativeMethods.KEYEVENTF_SCANCODE;
        if (!down)
            flags |= NativeMethods.KEYEVENTF_KEYUP;
        if (IsExtendedKey(vk))
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    Keyboard = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)scan,
                        dwFlags = flags,
                    },
                },
            },
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendMouseButton(MouseButton button, bool down, int screenX, int screenY)
    {
        uint flag = button switch
        {
            MouseButton.Left => down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP,
            MouseButton.Right => down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP,
            _ => down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP,
        };

        var (absoluteX, absoluteY) = ToAbsolute(screenX, screenY);
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MOUSEINPUT
                    {
                        dx = absoluteX,
                        dy = absoluteY,
                        dwFlags = flag | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    },
                },
            },
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void MoveAbsolute(int screenX, int screenY)
    {
        var (absoluteX, absoluteY) = ToAbsolute(screenX, screenY);
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                Type = NativeMethods.INPUT_MOUSE,
                U = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MOUSEINPUT
                    {
                        dx = absoluteX,
                        dy = absoluteY,
                        dwFlags = NativeMethods.MOUSEEVENTF_MOVE |
                                  NativeMethods.MOUSEEVENTF_ABSOLUTE |
                                  NativeMethods.MOUSEEVENTF_VIRTUALDESK |
                                  NativeMethods.MOUSEEVENTF_MOVE_NOCOALESCE,
                    },
                },
            },
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static (int X, int Y) ToAbsolute(int screenX, int screenY)
    {
        var (vx, vy, vw, vh) = VirtualScreen();
        int absoluteX = (int)Math.Round((screenX - vx) * 65535.0 / Math.Max(1, vw - 1));
        int absoluteY = (int)Math.Round((screenY - vy) * 65535.0 / Math.Max(1, vh - 1));
        return (absoluteX, absoluteY);
    }

    private void PostKey(int message, int vk, int scan)
    {
        int lParam = 1 | ((scan & 0xFF) << 16);
        if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            lParam |= unchecked((int)0xC0000000);

        NativeMethods.PostMessage(TargetWindow, message, (IntPtr)vk, (IntPtr)lParam);
    }

    private void PostClick(int clientX, int clientY, MouseButton button, bool downOnly = false, bool upOnly = false)
    {
        int lParam = NativeMethods.MakeLParam(clientX, clientY);
        NativeMethods.PostMessage(TargetWindow, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)lParam);

        int down = button switch
        {
            MouseButton.Left => NativeMethods.WM_LBUTTONDOWN,
            MouseButton.Right => NativeMethods.WM_RBUTTONDOWN,
            _ => NativeMethods.WM_MBUTTONDOWN,
        };
        int up = button switch
        {
            MouseButton.Left => NativeMethods.WM_LBUTTONUP,
            MouseButton.Right => NativeMethods.WM_RBUTTONUP,
            _ => NativeMethods.WM_MBUTTONUP,
        };
        int mk = button switch
        {
            MouseButton.Left => NativeMethods.MK_LBUTTON,
            MouseButton.Right => NativeMethods.MK_RBUTTON,
            _ => 0x10,
        };

        if (!upOnly)
        {
            NativeMethods.PostMessage(TargetWindow, down, (IntPtr)mk, (IntPtr)lParam);
            Thread.Sleep(_settings.Input.ClickHoldMs);
        }

        if (!downOnly)
            NativeMethods.PostMessage(TargetWindow, up, IntPtr.Zero, (IntPtr)lParam);
    }
}
