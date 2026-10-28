namespace WuxAuto.Core;

/// <summary>Touch timings and gesture sizes of the phone app. Not positional: saved like any other setting.</summary>
public sealed class PhoneSettings
{
    public int TapHoldMs { get; set; } = 60;

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

    public int PinchFromGap { get; set; } = 360;
    public int PinchToGap { get; set; } = 96;
    public int PinchMs { get; set; } = 400;

    /// <summary>Length of one 3-notch list-scroll swipe (measured on the Settings list: content follows the finger minus ~40 px; 170 px = 3 rows). Not for the map list, which does not scroll by swipe.</summary>
    public int ListSwipePx { get; set; } = 170;

    /// <summary>The floating button's position as fractions of the screen (saved, so it stays where the player dragged it).</summary>
    public double FloatingX { get; set; } = 0.02;
    public double FloatingY { get; set; } = 0.4;
}
