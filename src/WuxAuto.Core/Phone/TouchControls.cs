using WuxAuto.Core;

namespace WuxAuto.Phone;

// Task 1 confirms (provisional until the MuMu measurements run; each one is pinned by a TouchControlsTests test):
// * Turn(Left): the finger swipes RIGHT (the camera turns left)                    - Turn_Left_FingerSwipesRight
// * ScrollList(n < 0), the list moves down: the finger swipes UP                   - ScrollList_Down_FingerSwipesUp
// * Has(RunSkillKey | ShuraKey | StopAttack) = the shortcut icon was found          - Has_ShortcutAction_IsIconFound
// * Has(OpenInventory | ToggleSettings | OpenAutohuntSettings) = the ☰ menu was found - Has_MenuAction_IsMenuFound
// * ZoomOut: one pinch per 5 notches, at least one                                  - ZoomOut_OnePinchPerFiveNotches
// * TiltCamera keeps the desktop's sign (negative pixels = the finger moves up)     - TiltCamera_SwipesVertically

/// <summary>
/// The phone's <see cref="IGameControls"/>: every action is a touch on the game's own on-screen buttons, found by
/// <see cref="IShortcutSource"/>. Like the desktop's <c>WindowInput</c>, a dry run sends nothing and reports true
/// (before anything is looked up); with the game behind, nothing is sent and every call reports false. Letting go of a
/// held press is the exception: it always lifts the finger, so Stop / F10 cannot leave a skill held.
/// Not thread-safe except for the held press: <see cref="ReleaseAll"/> may come from the UI thread.
/// </summary>
public sealed class TouchControls(AppSettings settings, ITouchInput touch, IShortcutSource shortcuts, Action<int>? sleep = null) : IGameControls
{
    /// <summary>How long one list-scroll swipe takes.</summary>
    public const int ListSwipeMs = 300;

    /// <summary>Where turn swipes run: the ground above the character, clear of the buttons (fractions of the frame).</summary>
    public const double TurnSwipeX = 0.5;
    public const double TurnSwipeY = 0.35;

    private readonly Action<int> _sleep = sleep ?? Thread.Sleep;

    // What the one held finger presses ("action:ShuraKey" / "skill:1"), null = nothing. Locked: ReleaseAll comes
    // from the UI thread while the loop thread presses.
    private readonly Lock _heldLock = new();
    private string? _held;

    private PhoneSettings Phone => settings.Phone;

    public bool IsDryRun => touch.IsDryRun;
    public bool IsGameInFront => touch.IsGameInFront;
    public bool TryBringGameToFront() => touch.BringGameToFront();

    public bool Has(GameAction action) => IsMenuAction(action) ? shortcuts.Menu != null : shortcuts.Find(action) != null;

    /// <summary>Any key name will do: the phone has no keys, the setting only names the action.</summary>
    public bool IsKnownKey(string keyName) => true;

    /// <summary>Taps the action's shortcut icon; the inventory, settings and autohunt settings live behind ☰, so only the menu is tapped (the entry is the pilot's job).</summary>
    public bool Press(GameAction action) =>
        Send(() => TapCentre(IsMenuAction(action) ? shortcuts.Menu : shortcuts.Find(action)));

    /// <summary>Holds the action's shortcut icon; actions behind ☰ cannot be held.</summary>
    public bool ActionDown(GameAction action) =>
        Send(() => !IsMenuAction(action) && Hold(ActionHold(action), shortcuts.Find(action)));

    public void ActionUp(GameAction action) => LetGo(ActionHold(action));

    public bool Tap(int x, int y) => Send(() => touch.Tap(x, y, Phone.TapHoldMs));
    public bool UseItem(int x, int y) => Send(() => touch.Tap(x, y, Phone.TapHoldMs));

    public bool SkillTap(string skill) => Send(() => TapCentre(shortcuts.Skill(skill)));

    /// <summary>No Ctrl on the phone: the same tap as <see cref="SkillTap"/>.</summary>
    public bool SkillCtrlTap(string skill) => SkillTap(skill);

    public bool SkillDown(string skill) => Send(() => Hold(SkillHold(skill), shortcuts.Skill(skill)));
    /// <summary>Lets go of the skill if it is held; a dry run reports true like <c>WindowInput.KeyUp</c> (nothing went down).</summary>
    public bool SkillUp(string skill) => LetGo(SkillHold(skill)) || touch.IsDryRun;

    /// <summary>
    /// One joystick step towards the point (there is no attack-instead-of-walk on the stick, so
    /// <paramref name="forceWalk"/> changes nothing), then <see cref="PhoneSettings.TapSettleMs"/>: the touches are
    /// serialised and the next one must not cut this one short.
    /// </summary>
    public bool WalkTo(int x, int y, bool forceWalk) => Send(() =>
    {
        if (shortcuts.Joystick is not { } stick)
        {
            shortcuts.Invalidate();
            return false;
        }

        var (w, h) = shortcuts.FrameSize;
        if (w <= 0 || h <= 0)
            return false;

        var (dx, dy) = JoystickMath.ToStick(w, h, x, y, stick.Radius);
        if (!touch.Joystick(stick.Cx, stick.Cy, dx, dy, Phone.JoystickStepMs))
            return false;

        _sleep(Phone.TapSettleMs);
        return true;
    });

    /// <summary>A sideways swipe of <paramref name="ms"/> × <see cref="PhoneSettings.SwipePxPerTurnMs"/> pixels, taking <paramref name="ms"/>.</summary>
    public bool Turn(TurnDirection direction, int ms) => Send(() => TurnSwipe(direction, ms));

    /// <summary>The whole sweep (<see cref="DungeonSettings.RotateLeftSweepMs"/>) as one swipe; <see cref="EndTurn"/> has nothing left to do.</summary>
    public bool BeginTurn(TurnDirection direction) => Send(() => TurnSwipe(direction, settings.Dungeon.RotateLeftSweepMs));

    public void EndTurn(TurnDirection direction)
    {
    }

    public bool TurnByDrag(int x, int y, int pixels, int durationMs) => Send(() => touch.Swipe(x, y, x + pixels, y, durationMs));
    public bool TiltCamera(int x, int y, int pixels, int durationMs) => Send(() => touch.Swipe(x, y, x, y + pixels, durationMs));

    /// <summary>One pinch per 5 wheel notches (at least one), <see cref="PhoneSettings.TapSettleMs"/> apart.</summary>
    public bool ZoomOut(int x, int y, int notches) => Send(() =>
    {
        int pinches = Math.Max(1, notches / 5);
        for (int i = 0; i < pinches; i++)
        {
            if (i > 0)
                _sleep(Phone.TapSettleMs);
            if (!touch.Pinch(x, y, Phone.PinchFromGap, Phone.PinchToGap, Phone.PinchMs))
                return false;
        }

        return true;
    });

    /// <summary>A vertical swipe of <paramref name="notches"/> × <see cref="PhoneSettings.ListSwipePx"/> / 3; negative notches (the list moves down) = the finger moves up.</summary>
    public bool ScrollList(int x, int y, int notches) =>
        Send(() => touch.Swipe(x, y, x, y + notches * Phone.ListSwipePx / 3, ListSwipeMs));

    /// <summary>Lets go of whatever is held, joystick step included (Stop / F10 / the end of a run); safe to call any time.</summary>
    public void ReleaseAll()
    {
        // Inside the lock (as in Hold): a Hold on the loop thread must not slip in between and be lifted with _held set.
        lock (_heldLock)
        {
            _held = null;
            touch.Release();
        }
    }

    /// <summary>The gate every sending call goes through: dry run = nothing sent, true; the game behind = nothing sent, false.</summary>
    private bool Send(Func<bool> send)
    {
        if (touch.IsDryRun)
            return true;
        if (!touch.IsGameInFront)
            return false;
        return send();
    }

    private bool TapCentre(PixelRect? button)
    {
        if (button is not { } r)
        {
            shortcuts.Invalidate();
            return false;
        }

        return touch.Tap(r.CenterX, r.CenterY, Phone.TapHoldMs);
    }

    /// <summary>Presses the button down and remembers it; one finger only, so a press already held is let go first.</summary>
    private bool Hold(string what, PixelRect? button)
    {
        if (button is not { } r)
        {
            shortcuts.Invalidate();
            return false;
        }

        lock (_heldLock)
        {
            if (_held != null)
            {
                _held = null;
                touch.Release();
            }

            if (!touch.PressDown(r.CenterX, r.CenterY))
                return false;

            _held = what;
            return true;
        }
    }

    /// <summary>Lifts the finger if <paramref name="what"/> is what it holds (even with the game behind); otherwise nothing.</summary>
    private bool LetGo(string what)
    {
        lock (_heldLock)
        {
            if (_held != what)
                return false;
            _held = null;
            touch.Release();
            return true;
        }
    }

    /// <summary>
    /// A turn swipe centred on the ground above the character. Nothing to turn (ms &lt;= 0 or under a pixel) sends
    /// nothing and reports true: a zero-length swipe is a tap on the ground, which could walk or attack. The end points
    /// are kept on the frame (Task 1 tunes the length).
    /// </summary>
    private bool TurnSwipe(TurnDirection direction, int ms)
    {
        double length = ms * Phone.SwipePxPerTurnMs;
        if (ms <= 0 || length < 1)
            return true;

        var (w, h) = shortcuts.FrameSize;
        if (w <= 0 || h <= 0)
            return false;

        int cx = (int)Math.Round(w * TurnSwipeX);
        int cy = (int)Math.Round(h * TurnSwipeY);
        int half = (int)Math.Round(length / 2);
        int sign = direction == TurnDirection.Left ? 1 : -1; // left = the finger moves right (Task 1 confirms)
        int x1 = Math.Clamp(cx - sign * half, 0, w - 1);
        int x2 = Math.Clamp(cx + sign * half, 0, w - 1);
        if (x1 == x2)
            return true;
        return touch.Swipe(x1, cy, x2, cy, ms);
    }

    private static bool IsMenuAction(GameAction action) =>
        action is GameAction.OpenInventory or GameAction.ToggleSettings or GameAction.OpenAutohuntSettings;

    private static string ActionHold(GameAction action) => $"action:{action}";
    private static string SkillHold(string skill) => $"skill:{skill}";
}
