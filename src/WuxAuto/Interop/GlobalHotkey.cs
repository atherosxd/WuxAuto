using System.Windows.Input;
using System.Windows.Interop;
using WuxAuto.Interop;

namespace WuxAuto.Interop;

/// <summary>System-wide hotkeys (work while the game owns the foreground).</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int ToggleId = 0xA001;
    private const int PanicId = 0xA002;
    private const int SnapshotId = 0xA003;

    private readonly HwndSource _source;
    private readonly Action _onToggle;
    private readonly Action _onPanic;
    private readonly Action? _onSnapshot;

    public GlobalHotkey(
        IntPtr hwnd,
        Action onToggle,
        Action onPanic,
        Action? onSnapshot = null,
        Key toggleKey = Key.F9,
        Key panicKey = Key.F10,
        Key snapshotKey = Key.F11)
    {
        _onToggle = onToggle;
        _onPanic = onPanic;
        _onSnapshot = onSnapshot;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Window has no HwndSource.");
        _source.AddHook(WndProc);

        uint toggleVk = (uint)KeyInterop.VirtualKeyFromKey(toggleKey);
        uint panicVk = (uint)KeyInterop.VirtualKeyFromKey(panicKey);
        uint snapshotVk = (uint)KeyInterop.VirtualKeyFromKey(snapshotKey);

        ToggleRegistered = NativeMethods.RegisterHotKey(hwnd, ToggleId, NativeMethods.MOD_NOREPEAT, toggleVk);
        PanicRegistered = NativeMethods.RegisterHotKey(hwnd, PanicId, NativeMethods.MOD_NOREPEAT, panicVk);
        SnapshotRegistered = NativeMethods.RegisterHotKey(hwnd, SnapshotId, NativeMethods.MOD_NOREPEAT, snapshotVk);
    }

    public bool ToggleRegistered { get; }
    public bool PanicRegistered { get; }
    public bool SnapshotRegistered { get; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
            return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case ToggleId:
                _onToggle();
                handled = true;
                break;
            case PanicId:
                _onPanic();
                handled = true;
                break;
            case SnapshotId:
                _onSnapshot?.Invoke();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeMethods.UnregisterHotKey(_source.Handle, ToggleId);
        NativeMethods.UnregisterHotKey(_source.Handle, PanicId);
        NativeMethods.UnregisterHotKey(_source.Handle, SnapshotId);
        _source.RemoveHook(WndProc);
    }
}
