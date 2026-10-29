using WuxAuto.Core;

namespace WuxAuto.Phone;

// Measured in MuMu 2026-10-28, see docs/superpowers/notes/2026-10-28-phone-checks.md (each one is pinned by a TouchControlsTests test):
// * Turn(Left): the finger moves LEFT (the camera turns with the finger; the provisional rule was the opposite)  - Turn_Left_FingerMovesLeft
// * Turns are Drags (move, then hold ~100 ms before lifting) on the right-side band, centre 0.74 w, 0.33 h  - Turn_* tests
//   length = max(TurnDragMinPx 40, ms x SwipePxPerTurnMs) (note: px = 20 + 4.8 x degrees, 180 ms = 15 degrees); 20 px is inside the
//   game's dead zone, so the 40 ms fine-tune tap is raised to 40 px (about 4 degrees) rather than skipped
// * TiltCamera: vertical Drag at 0.65 w (at 0.74 w vertical drags were ignored); negative pixels = finger up = towards the horizon  - TiltCamera_*
// * ScrollList(n < 0), the list moves down: the finger swipes UP (Settings list; the map list pages by its arrows, never by swipe)  - ScrollList_Down_FingerSwipesUp
// * Has(RunSkillKey) = the shortcut icon was found; Has(OpenInventory | ToggleSettings) = the menu was found (both confirmed)
// * ZoomOut: one pinch per 5 notches, at least one (fingers 720 -> 192 px apart, 400 ms is about x0.5, 3 pinches = full range)  - ZoomOut_OnePinchPerFiveNotches
// * TapSettleMs 100 (a tap right after the joystick is lost up to 38 ms)
// * The attack skill held down (SkillDown, AttackMode Hold) hit the LOD boss for a whole fight (MuMu 2026-10-29, Task 8)
// Still unverified: Shura = ActionDown(ShuraKey) / StopAttack icon (no stop-attack shortcut exists in the picker),
//   Has(OpenAutohuntSettings) = menu found (which entry opens the autohunt settings is for the user to say)

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

    /// <summary>Where turn drags run: the clear band on the right side, away from the pet panel, minimap and buttons (fractions of the frame; measured at 1920x1080).</summary>
    public const double TurnBandCentreX = 0.74;
    public const double TurnBandCentreY = 0.33;
    public const double TurnBandMinX = 0.60;
    public const double TurnBandMaxX = 0.88;

    /// <summary>Vertical (tilt) drags run at this x: at 0.74 w they were ignored.</summary>
    public const double TiltX = 0.65;

    /// <summary>
    /// A tap while a finger is held (a Hold fight: the sword, Pick up, AUTO BUFF) is let through like this: lift the held
    /// finger, wait <see cref="LiftBeforeTapMs"/>, tap, wait <see cref="TapBeforeRepressMs"/>, press the held button again.
    /// The game ignores a button tap while another finger is down (M0), and AUTO BUFF tapped during a held skill never cast
    /// (MuMu LOD run 2026-10-30: 15 buff presses in the log, no buff on screen; one tap in town casts it).
    /// </summary>
    public const int LiftBeforeTapMs = 150;

    /// <inheritdoc cref="LiftBeforeTapMs"/>
    public const int TapBeforeRepressMs = 400;

    private readonly Action<int> _sleep = sleep ?? Thread.Sleep;

    // What the one held finger presses ("action:ShuraKey" / "skill:1"), null = nothing. Locked: ReleaseAll comes
    // from the UI thread while the loop thread presses.
    private readonly Lock _heldLock = new();
    private string? _held;

    // Where the held finger is down (for the press again after a tap in between). Under _heldLock.
    private (int X, int Y) _heldAt;

    // Bumped by every ReleaseAll and every new held press (under _heldLock): a tap in between presses the old hold
    // again only if nothing else let go of it or took the finger during its waits.
    private int _holdGeneration;

    // The skill "held" by SkillDown ("skill:1"): one press went out, nothing stays down. Under _heldLock.
    private string? _pressedSkill;

    // Stop / Panic: nothing goes down until Resume (the next Start). Volatile: set from the UI side, read by the loop.
    private volatile bool _halted;

    /// <summary>True between <see cref="Halt"/> and <see cref="Resume"/>: every sending call refuses (false, nothing sent).</summary>
    public bool IsHalted => _halted;

    private PhoneSettings Phone => settings.Phone;

    public bool IsDryRun => touch.IsDryRun;
    public bool IsGameInFront => touch.IsGameInFront;
    public bool TryBringGameToFront() => touch.BringGameToFront();

    public bool Has(GameAction action) => IsMenuAction(action) ? shortcuts.Menu != null : shortcuts.Find(action) != null;

    /// <summary>Any key name will do: the phone has no keys, the setting only names the action.</summary>
    public bool IsKnownKey(string keyName) => true;

    /// <summary>Taps the action's shortcut icon; the inventory, settings and autohunt settings live behind ☰, so only the menu is tapped (the entry is the pilot's job).</summary>
    public bool Press(GameAction action) =>
        Send(() => IsMenuAction(action)
            ? TapCentre(shortcuts.Menu, shortcuts.InvalidateMenu)
            : TapCentre(shortcuts.Find(action), () => shortcuts.Missed(action)));

    /// <summary>Holds the action's shortcut icon; actions behind ☰ cannot be held.</summary>
    public bool ActionDown(GameAction action) =>
        Send(() => !IsMenuAction(action) && Hold(ActionHold(action), shortcuts.Find(action), () => shortcuts.Missed(action)));

    public void ActionUp(GameAction action) => LetGo(ActionHold(action));

    public bool Tap(int x, int y) => Send(() => TapPoint(x, y));
    public bool UseItem(int x, int y) => Send(() => TapPoint(x, y));

    /// <summary>
    /// One plain tap of the attack skill button (<see cref="IShortcutSource.AttackSkill"/>: on the phone the first attack
    /// icon found, whatever the name). How often the pilot taps is its job (<see cref="PhoneSettings.AttackTapEveryMs"/>).
    /// </summary>
    public bool SkillTap(string skill) => Send(() => TapSkill(skill));

    /// <summary>No Ctrl on the phone: the same tap as <see cref="SkillTap"/>.</summary>
    public bool SkillCtrlTap(string skill) => SkillTap(skill);

    /// <summary>
    /// <see cref="PhoneSettings.AttackMode"/> Hold (the default; user, MuMu 2026-10-29: "I just tried holding the attack
    /// skills, it worked"): the finger stays on the attack icon until <see cref="SkillUp"/> / <see cref="ReleaseAll"/> /
    /// <see cref="Halt"/>, like <see cref="ActionDown"/>. Tap: the "held" skill is one plain <see cref="SkillTap"/>,
    /// nothing stays down; it counts as held until <see cref="SkillUp"/>, and a press held by <see cref="ActionDown"/>
    /// is let go first (one finger).
    /// </summary>
    public bool SkillDown(string skill) => Send(() =>
    {
        if (Phone.AttackMode == PhoneAttackMode.Hold)
            return Hold(SkillHold(skill), shortcuts.AttackSkill(skill), () => shortcuts.MissedAttackSkill(skill));

        lock (_heldLock)
        {
            if (_held != null)
            {
                _held = null;
                touch.Release();
            }
        }

        if (!TapSkill(skill))
            return false;
        lock (_heldLock)
        {
            if (_halted)
                return false;
            _pressedSkill = SkillHold(skill);
        }

        return true;
    });

    /// <summary>Lets go of the skill (Hold mode) or ends its "hold" (Tap mode, nothing is down); true if it was held. A dry run reports true like <c>WindowInput.KeyUp</c>.</summary>
    public bool SkillUp(string skill)
    {
        if (LetGo(SkillHold(skill)))
            return true;

        lock (_heldLock)
        {
            if (_pressedSkill == SkillHold(skill))
            {
                _pressedSkill = null;
                return true;
            }
        }

        return touch.IsDryRun;
    }

    /// <summary>
    /// Whether the skill's press is still on: true in a dry run (nothing was pressed) and for Tap mode's one-tap "hold"
    /// until <see cref="SkillUp"/>; in Hold mode true while this finger is down (<see cref="ITouchInput.HoldAlive"/> not
    /// false - the next gesture lifts a held press, and the system can cancel it). False (the skill counts as let go,
    /// press it again) when the finger is gone, and when nothing holds this skill any more - <see cref="ReleaseAll"/>
    /// (the floating panel, the game behind, a wait in the loop) or another held press took the finger (M4 review #1).
    /// </summary>
    public bool SkillStillHeld(string skill)
    {
        if (touch.IsDryRun)
            return true;

        string what = SkillHold(skill);
        lock (_heldLock)
        {
            if (_pressedSkill == what)
                return true;
            if (_held != what)
                return false;
            if (touch.HoldAlive != false)
                return true;
            _held = null;
            return false;
        }
    }

    private bool TapSkill(string skill) =>
        TapCentre(shortcuts.AttackSkill(skill), () => shortcuts.MissedAttackSkill(skill));

    /// <summary>
    /// One joystick step towards the point (there is no attack-instead-of-walk on the stick, so
    /// <paramref name="forceWalk"/> changes nothing), then <see cref="PhoneSettings.TapSettleMs"/>: the touches are
    /// serialised and the next one must not cut this one short.
    /// </summary>
    public bool WalkTo(int x, int y, bool forceWalk) => Send(() =>
    {
        if (shortcuts.Joystick is not { } stick)
        {
            shortcuts.InvalidateJoystick();
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

    /// <summary>A sideways drag of <paramref name="ms"/> × <see cref="PhoneSettings.SwipePxPerTurnMs"/> pixels (see <see cref="TurnDrag"/>).</summary>
    public bool Turn(TurnDirection direction, int ms) => Send(() => TurnDrag(direction, ms));

    /// <summary>The whole sweep (<see cref="DungeonSettings.RotateLeftSweepMs"/>) as one drag; <see cref="EndTurn"/> has nothing left to do.</summary>
    public bool BeginTurn(TurnDirection direction) => Send(() => TurnDrag(direction, settings.Dungeon.RotateLeftSweepMs));

    public void EndTurn(TurnDirection direction)
    {
    }

    public bool TurnByDrag(int x, int y, int pixels, int durationMs) => Send(() => touch.Swipe(x, y, x + pixels, y, durationMs));

    /// <summary>A vertical drag at <see cref="TiltX"/> (the x argument is the desktop's and is ignored), starting at <paramref name="y"/>; negative pixels = the finger moves up.</summary>
    public bool TiltCamera(int x, int y, int pixels, int durationMs) => Send(() =>
    {
        var (w, h) = shortcuts.FrameSize;
        if (w <= 0 || h <= 0)
            return false;

        int tx = Math.Clamp((int)Math.Round(w * TiltX), 0, w - 1);
        int y1 = Math.Clamp(y, 0, h - 1);
        int y2 = Math.Clamp(y + pixels, 0, h - 1);
        return touch.Drag(tx, y1, tx, y2, durationMs, Phone.TurnDragEndHoldMs);
    });

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
            _pressedSkill = null;
            _holdGeneration++;
            touch.Release();
        }
    }

    /// <summary>
    /// Stop / Panic: refuse every touch from now on (a dry run included) and lift whatever is held. A tick still running
    /// on the loop thread can send nothing new; a held press it starts after this is refused under the hold lock.
    /// </summary>
    public void Halt()
    {
        _halted = true;
        ReleaseAll();
    }

    /// <summary>The next Start: touches go out again.</summary>
    public void Resume() => _halted = false;

    /// <summary>The gate every sending call goes through: halted = nothing sent, false; dry run = nothing sent, true; the game behind = nothing sent, false.</summary>
    private bool Send(Func<bool> send)
    {
        if (_halted)
            return false;
        if (touch.IsDryRun)
            return true;
        if (!touch.IsGameInFront)
            return false;
        return send();
    }

    /// <summary>Taps the button's centre; no button = nothing sent and <paramref name="missed"/> reports just that one (<see cref="IShortcutSource.Missed(GameAction)"/>).</summary>
    private bool TapCentre(PixelRect? button, Action missed)
    {
        if (button is not { } r)
        {
            missed();
            return false;
        }

        return TapPoint(r.CenterX, r.CenterY);
    }

    /// <summary>
    /// One tap; with a finger held, the held press is lifted around it and pressed again (<see cref="LiftBeforeTapMs"/>,
    /// <see cref="TapBeforeRepressMs"/>). The press again is skipped when a Halt / ReleaseAll / another hold came in between.
    /// </summary>
    private bool TapPoint(int x, int y)
    {
        string? resume;
        (int X, int Y) at;
        int generation;
        lock (_heldLock)
        {
            resume = _held;
            at = _heldAt;
            generation = _holdGeneration;
            if (resume != null)
            {
                _held = null;
                touch.Release();
            }
        }

        if (resume == null)
            return touch.Tap(x, y, Phone.TapHoldMs);

        _sleep(LiftBeforeTapMs);
        if (_halted)
            return false; // Stop / F10 during the lift: no tap, no press again
        bool tapped = touch.Tap(x, y, Phone.TapHoldMs);
        _sleep(TapBeforeRepressMs);
        lock (_heldLock)
        {
            // Not after a Halt, a ReleaseAll (a pause, Stop -> Start) or another held press during the waits.
            if (!_halted && _held == null && _holdGeneration == generation && touch.IsGameInFront && touch.PressDown(at.X, at.Y))
                _held = resume;
        }

        return tapped;
    }

    /// <summary>Presses the button down and remembers it; one finger only, so a press already held is let go first.</summary>
    private bool Hold(string what, PixelRect? button, Action missed)
    {
        if (button is not { } r)
        {
            missed();
            return false;
        }

        lock (_heldLock)
        {
            if (_held != null)
            {
                _held = null;
                touch.Release();
            }

            // Halted while this call was on its way (Halt's release ran first): the finger must not go down after it.
            if (_halted)
                return false;

            if (!touch.PressDown(r.CenterX, r.CenterY))
                return false;

            _held = what;
            _heldAt = (r.CenterX, r.CenterY);
            _holdGeneration++;
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
    /// A turn drag centred on the right-side band, the finger moving the way the camera turns (left = finger left),
    /// with an end hold. ms &lt;= 0 sends nothing and reports true: a zero-length drag is a tap on the ground, which could
    /// walk or attack. A short turn is raised to <see cref="PhoneSettings.TurnDragMinPx"/>: 20 px (the desktop's 40 ms
    /// fine-tune tap) is inside the game's dead zone and does nothing, 40 px is the least that turned (measured in
    /// MuMu 2026-10-28), so a fine-tune tap becomes the smallest real turn rather than being skipped. A long one is
    /// cut to the band's width.
    /// </summary>
    private bool TurnDrag(TurnDirection direction, int ms)
    {
        if (ms <= 0)
            return true;

        var (w, h) = shortcuts.FrameSize;
        if (w <= 0 || h <= 0)
            return false;

        int minX = (int)Math.Round(w * TurnBandMinX);
        int maxX = (int)Math.Round(w * TurnBandMaxX);
        int cx = (int)Math.Round(w * TurnBandCentreX);
        int cy = Math.Clamp((int)Math.Round(h * TurnBandCentreY), 0, h - 1);
        double length = Math.Clamp(Math.Max(ms * Phone.SwipePxPerTurnMs, Phone.TurnDragMinPx), 1, maxX - minX);
        int half = (int)Math.Round(length / 2);
        int sign = direction == TurnDirection.Left ? -1 : 1; // the finger moves the way the camera turns
        int x1 = Math.Clamp(cx - sign * half, minX, maxX);
        int x2 = Math.Clamp(cx + sign * half, minX, maxX);
        if (x1 == x2)
            return true;
        int durationMs = Math.Max(Phone.TurnDragMinMs, (int)Math.Round(length / Phone.TurnDragPxPerMs));
        return touch.Drag(x1, cy, x2, cy, durationMs, Phone.TurnDragEndHoldMs);
    }

    private static bool IsMenuAction(GameAction action) =>
        action is GameAction.OpenInventory or GameAction.ToggleSettings or GameAction.OpenAutohuntSettings;

    private static string ActionHold(GameAction action) => $"action:{action}";
    private static string SkillHold(string skill) => $"skill:{skill}";
}
