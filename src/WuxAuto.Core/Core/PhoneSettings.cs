namespace WuxAuto.Core;

/// <summary>Touch timings and gesture sizes of the phone app. Not positional: saved like any other setting.</summary>
public sealed class PhoneSettings
{
    public int TapHoldMs { get; set; } = 60;

    /// <summary>Pause after a tap before the next input. Provisional - Task 1 measures this.</summary>
    public int TapSettleMs { get; set; } = 150;

    public int JoystickStepMs { get; set; } = 900;

    /// <summary>Swipe pixels per millisecond of desktop key turn. Provisional - Task 1 measures this.</summary>
    public double SwipePxPerTurnMs { get; set; } = 1.0;

    public int PinchFromGap { get; set; } = 360;
    public int PinchToGap { get; set; } = 96;
    public int PinchMs { get; set; } = 400;

    /// <summary>Length of one list-scroll swipe. Provisional - Task 1 measures this.</summary>
    public int ListSwipePx { get; set; } = 300;

    /// <summary>The floating button's position as fractions of the screen (saved, so it stays where the player dragged it).</summary>
    public double FloatingX { get; set; } = 0.02;
    public double FloatingY { get; set; } = 0.4;
}
