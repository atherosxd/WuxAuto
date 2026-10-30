namespace WuxAuto.Core;

/// <summary>Touch timings and gesture sizes of the phone app. Not positional: saved like any other setting.</summary>
public sealed class PhoneSettings
{
    public int TapHoldMs { get; set; } = 60;

    /// <summary>
    /// <see cref="AttackMode"/> Tap: the phone pilot taps an attack skill once per this many ms, start to start - LOD
    /// skill spam and the manual attack taps (user, MuMu 2026-10-29: "actually make it 100ms taps"). Replaces the
    /// desktop's skill intervals on the phone; clamped to 50-2000 ms. A tick slower than this (the LOD fight's
    /// whole-screen OCR) stretches it - the pilot logs the real rate.
    /// </summary>
    public int AttackTapEveryMs { get; set; } = 100;

    /// <summary>
    /// How the phone presses the attack skill icon (user, MuMu 2026-10-29: "maybe add it as a setting if hold the attack
    /// or tap"; default Hold: "the straight hold is better until the boss dies"): <see cref="PhoneAttackMode.Tap"/> = one
    /// plain tap per <see cref="AttackTapEveryMs"/>, never held;
    /// <see cref="PhoneAttackMode.Hold"/> = the finger stays on the icon (LOD: the whole fight; Laby: the held press).
    /// </summary>
    public PhoneAttackMode AttackMode { get; set; } = PhoneAttackMode.Hold;

    /// <summary>
    /// Hold mode in the LOD fight: lift and press the attack again this often (user, Xiaomi 2026-10-30: "it also
    /// sometimes stop attacking, whenever I touch something on the screen. maybe every 5s hold, then retap", then "5s is
    /// too long maybe try 2s"). The player's own touch can end the pilot's held finger without the system telling it.
    /// 0 = never; clamped 0-60000.
    /// </summary>
    public int HoldRepressMs { get; set; } = 2000;

    /// <summary>
    /// LOD on arrival (and after dying, back at the start): push the joystick up-left this long before tabbing (user,
    /// Xiaomi 2026-10-30: "walk/run using joystick for 2s going northwest, ... the Boss Arena is just left of the
    /// character"). 0 = stand where the teleport put the character; clamped 0-10000.
    /// </summary>
    public int LodArrivalWalkMs { get; set; } = 2000;

    /// <summary>
    /// LOD: after this many sword (target) taps without the boss targeted, tap the boss itself - its pink ring, else just
    /// below its belt (user, Xiaomi 2026-10-30: "if the blade doesn't select the boss, try tapping the boss"). 0 = never.
    /// </summary>
    public int LodBossTapAfterTabs { get; set; } = 3;

    /// <summary>
    /// The game's Settings > VISUAL "Font size" the phone sets once per Start in town (user, 2026-10-30: "similar to the
    /// desktop counter part. increase the font size as well? to 120%"). Exact value, not the desktop's range; 0 = leave it.
    /// </summary>
    public int FontSizePercent { get; set; } = 120;

    /// <summary>
    /// Settings > VISUAL "Interface scale" the phone sets with its - / + buttons in the same visit (user, 2026-10-30:
    /// "change the interface scale to 1.10 using the + sign"). Steps of 0.01; "(AUTO)" counts as 1.00 (MuMu 2560x1440: one
    /// + from AUTO read 1.01). 0 = leave it.
    /// </summary>
    public double InterfaceScale { get; set; } = 1.10;

    // Values below marked "measured" come from MuMu on 2026-10-28, see docs/superpowers/notes/2026-10-28-phone-checks.md.

    /// <summary>Pause after a joystick step before the next input; a tap right after the release was lost up to 38 ms, delivered from 41 ms (measured; 2x the threshold).</summary>
    public int TapSettleMs { get; set; } = 100;

    public int JoystickStepMs { get; set; } = 900;

    /// <summary>
    /// Turn-drag pixels per millisecond of desktop key turn (measured: 180 ms = 15 degrees = 20 + 4.8 x 15 = 92 px,
    /// so 0.5, the 20 px dead zone folded in).
    /// </summary>
    public double SwipePxPerTurnMs { get; set; } = 0.5;

    /// <summary>The shortest turn drag: under it the move falls inside the game's dead zone (20 px turned 1-2 degrees at most; 40 px about 4).</summary>
    public int TurnDragMinPx { get; set; } = 40;

    /// <summary>How long the finger stays at the end of a turn or tilt drag before lifting (measured: without it part of the move is lost).</summary>
    public int TurnDragEndHoldMs { get; set; } = 100;

    /// <summary>Turn drag speed in pixels per millisecond (measured recommendation 0.3); the duration is length / speed, at least <see cref="TurnDragMinMs"/>.</summary>
    public double TurnDragPxPerMs { get; set; } = 0.3;

    public int TurnDragMinMs { get; set; } = 150;

    /// <summary>Pinch start / end distance between the two fingers (full gap, px). Measured in MuMu as each finger 360 → 96 px from the centre.</summary>
    public int PinchFromGap { get; set; } = 720;
    public int PinchToGap { get; set; } = 192;
    public int PinchMs { get; set; } = 400;

    /// <summary>Length of one 3-notch list-scroll swipe (measured on the Settings list: content follows the finger minus ~40 px; 170 px = 3 rows). Not for the map list, which does not scroll by swipe.</summary>
    public int ListSwipePx { get; set; } = 170;

    /// <summary>
    /// The floating button's centre as fractions of the screen (saved, so it stays where the player dragged it). Default:
    /// the top strip (0.40, 0.03) = 768,32 at 1920x1080, where nothing the pilot reads or taps sits (measured).
    /// </summary>
    public double FloatingX { get; set; } = 0.40;
    public double FloatingY { get; set; } = 0.03;
}

/// <summary>How the phone presses the attack skill icon (<see cref="PhoneSettings.AttackMode"/>).</summary>
public enum PhoneAttackMode
{
    Tap,
    Hold,
}
