using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Interop;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto-confirm (user, 2026-10-25, "to make it faster to sell"): while the game is bound and the
/// pilot is not running, a Ctrl + right click on the game (the player selling or using an item)
/// starts watching every frame for the game's confirm dialog ("Fist Scroll Box Confirm to use?"
/// over CANCEL / OK) and clicks its OK the moment it shows. A plain right click does nothing, so
/// the player still gets the dialog when Ctrl is not held. Ticked by the engine's vision loop
/// (same thread as the pilot); all input goes through the foreground-guarded <see cref="WindowInput"/>.
/// </summary>
public sealed class AutoConfirm
{
    /// <summary>How long one Ctrl + right click keeps the watch going (the dialog is up long before that).</summary>
    private const int WatchMs = 3000;

    /// <summary>At most one click per this interval: the dialog needs a moment to close after OK.</summary>
    private const int ClickGapMs = 400;

    /// <summary>The same dialog still up after this many clicks: the clicks do not take, leave it to the player.</summary>
    private const int MaxClicksPerDialog = 3;

    private const int MaxSnapshots = 3;

    private readonly AppSettings _settings;
    private readonly ScreenAnalyzer _analyzer;
    private readonly WindowInput _input;
    private readonly List<OverlayItem> _overlays = new();

    private bool _rightWasDown;
    private long _watchUntil;
    private long _lastAttemptAt;
    private PixelRect? _lastButton;
    private int _clicksOnDialog;
    private bool _stuckWarned;
    private int _snapshots;

    public AutoConfirm(AppSettings settings, ScreenAnalyzer analyzer, WindowInput input)
    {
        _settings = settings;
        _analyzer = analyzer;
        _input = input;
    }

    public event Action<LogLevel, string>? Log;

    public IReadOnlyList<OverlayItem> Overlays => _overlays;

    public (int X, int Y, long Ticks)? LastClick { get; private set; }

    /// <summary>
    /// Forgets a pending watch (unbind, UI thread). The overlays are left to the loop thread, which
    /// clears them on its next tick.
    /// </summary>
    public void Reset()
    {
        _watchUntil = 0;
        _rightWasDown = false;
        _lastButton = null;
        _clicksOnDialog = 0;
        _stuckWarned = false;
    }

    public async Task TickAsync(Mat frame)
    {
        var sequence = _settings.Sequence;
        if (!sequence.AutoConfirmDialogs || !_input.IsBound)
        {
            _overlays.Clear();
            _watchUntil = 0;
            return;
        }

        long now = Environment.TickCount64;

        // The player's gesture: Ctrl held, right button pressed, over the game while it is in front.
        // Polled once per frame (30 fps); the "pressed since the last call" bit catches a click
        // shorter than a frame.
        bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        short right = NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON);
        bool rightDown = (right & 0x8000) != 0;
        bool pressed = (rightDown && !_rightWasDown) || (right & 0x0001) != 0;
        _rightWasDown = rightDown;

        if (ctrl && pressed && _input.IsTargetForeground && CursorOverGame())
        {
            if (now >= _watchUntil)
                Log?.Invoke(LogLevel.Trace, "Ctrl + right click on the game - watching for a confirm dialog.");
            _watchUntil = now + WatchMs;
            _stuckWarned = false;
        }

        if (now >= _watchUntil)
        {
            if (_overlays.Count > 0)
                _overlays.Clear();
            _lastButton = null;
            _clicksOnDialog = 0;
            return;
        }

        ConfirmDialog? dialog;
        try
        {
            dialog = await _analyzer.FindConfirmDialogAsync(frame, sequence.AutoConfirmText, sequence.AutoConfirmButton);
        }
        catch (Exception ex)
        {
            Log?.Invoke(LogLevel.Trace, $"Auto-confirm read failed: {ex.Message}");
            return;
        }

        _overlays.Clear();
        if (dialog == null)
        {
            // Gone (confirmed, or cancelled by the player): the next dialog of a selling spree pops up
            // at the same spot and must count as a new one.
            _lastButton = null;
            _clicksOnDialog = 0;
            return;
        }

        _overlays.Add(new OverlayItem(dialog.MessageBounds, dialog.Message, OverlayColor.Blue));
        _overlays.Add(new OverlayItem(dialog.Button, sequence.AutoConfirmButton, OverlayColor.Yellow));

        if (now - _lastAttemptAt < ClickGapMs)
            return;

        bool sameDialog = _lastButton is { } last
                          && Math.Abs(last.CenterX - dialog.Button.CenterX) <= dialog.Button.Width
                          && Math.Abs(last.CenterY - dialog.Button.CenterY) <= dialog.Button.Height;
        if (!sameDialog)
            _clicksOnDialog = 0;
        else if (_clicksOnDialog >= MaxClicksPerDialog)
        {
            if (!_stuckWarned)
            {
                _stuckWarned = true;
                Log?.Invoke(LogLevel.Warning,
                    $"'{dialog.Message}' is still up after {_clicksOnDialog} clicks on '{sequence.AutoConfirmButton}' at {dialog.Button.CenterX},{dialog.Button.CenterY} - leaving it to you.");
                SaveSnapshot(frame, "autoconfirm_stuck", dialog.Button);
            }

            return;
        }

        // Blocked = the game is not in front (WindowInput logs it); try again after the gap.
        _lastAttemptAt = now;
        if (!_input.Click(dialog.Button.CenterX, dialog.Button.CenterY))
            return;

        _lastButton = dialog.Button;
        _clicksOnDialog++;
        LastClick = (dialog.Button.CenterX, dialog.Button.CenterY, now);
        // The next dialog of a selling spree may come without another Ctrl + right click reaching
        // the poll (held Ctrl, quick clicks): keep watching a little after a confirmed one.
        _watchUntil = Math.Max(_watchUntil, now + 1500);

        if (_input.IsDryRun)
        {
            Log?.Invoke(LogLevel.Info, $"[dry run] '{dialog.Message}' - would click '{sequence.AutoConfirmButton}' at {dialog.Button.CenterX},{dialog.Button.CenterY} ({dialog.How}).");
            return;
        }

        Log?.Invoke(LogLevel.Success, $"Auto-confirmed '{dialog.Message}' - clicked '{sequence.AutoConfirmButton}' at {dialog.Button.CenterX},{dialog.Button.CenterY} ({dialog.How}).");
        SaveSnapshot(frame, "autoconfirm", dialog.Button);
    }

    private bool CursorOverGame()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
            return false;
        if (!WindowEnumerator.TryGetClientRect(_input.TargetWindow, out int width, out int height, out int left, out int top))
            return false;
        return cursor.X >= left && cursor.X < left + width && cursor.Y >= top && cursor.Y < top + height;
    }

    /// <summary>A few frames per app start, so the first live dialogs can be checked with the probe.</summary>
    private void SaveSnapshot(Mat frame, string tag, PixelRect button)
    {
        if (_snapshots >= MaxSnapshots)
            return;
        _snapshots++;
        try
        {
            DebugArtifacts.SaveFrame(frame, _overlays, tag, (button.CenterX, button.CenterY));
        }
        catch (Exception ex)
        {
            Log?.Invoke(LogLevel.Trace, $"Auto-confirm snapshot failed: {ex.Message}");
        }
    }
}
