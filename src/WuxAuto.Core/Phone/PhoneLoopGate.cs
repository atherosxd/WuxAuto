using WuxAuto.Automation;

namespace WuxAuto.Phone;

/// <summary>Whether the phone engine ticks the pilot on this pass, and why not when it does not.</summary>
public readonly record struct LoopDecision(bool Tick, string Reason)
{
    public static LoopDecision Go { get; } = new(true, string.Empty);

    public static LoopDecision Wait(string reason) => new(false, reason);
}

/// <summary>
/// The phone engine's per-pass check before <see cref="AutomationPilot.TickAsync"/>: only a current picture of the game
/// counts. In order: the capture runs; its newest frame is not older than <see cref="StaleMs"/>; the game is the app
/// in front; the floating control's panel is shut (it covers the game); and the minimap's zone title read within
/// <see cref="TitleGraceMs"/> (the M0 rule: a launcher frame is not black, so "not black" alone proves nothing) - except
/// in the phases where the game's own screen hides the minimap (<see cref="RunsWithoutTitle"/>). Times are
/// <see cref="FrameGate.NowTicks"/> ticks. Loop thread only.
/// </summary>
public sealed class PhoneLoopGate
{
    /// <summary>
    /// The newest frame may be this old. MediaProjection hands over a frame only when the screen changes, so a still
    /// screen is not stale - its last frame is the screen. The game's own HUD never stands still for long (the ping /
    /// FPS counter at the top refreshes every second or so, the world animates at 15 fps), so 30 s without any frame
    /// while the capture claims to run means the pipeline is stuck (or the screen is off): wait rather than act on it.
    /// </summary>
    public const int StaleMs = 30_000;

    /// <summary>How often the engine reads the minimap's zone title (an OCR of the small minimap crop).</summary>
    public const int TitleCheckEveryMs = 1_000;

    /// <summary>
    /// A title read this recently still vouches for the screen: the pilot's own windows (the ☰ panel, Settings) can
    /// cover the minimap for a few seconds. Another app in front is caught by the game-in-front check, not by this.
    /// </summary>
    public const int TitleGraceMs = 10_000;

    private readonly FrameGate _frames = new(StaleMs);
    private long _titleReadAt;
    private long _titleCheckedAt;

    /// <summary>True when the minimap's title should be read on this pass.</summary>
    public bool TitleCheckDue(long nowTicks) =>
        _titleCheckedAt <= 0 || nowTicks - _titleCheckedAt >= TitleCheckEveryMs * TimeSpan.TicksPerMillisecond;

    /// <summary>Records a title check at <paramref name="nowTicks"/>; <paramref name="read"/> = a zone title read.</summary>
    public void TitleChecked(long nowTicks, bool read)
    {
        _titleCheckedAt = nowTicks;
        if (read)
            _titleReadAt = nowTicks;
    }

    /// <summary>Forgets the title (a new Start, the capture restarted).</summary>
    public void Reset()
    {
        _titleReadAt = 0;
        _titleCheckedAt = 0;
    }

    public LoopDecision Decide(long nowTicks, bool captureAlive, long frameTicks, bool gameInFront, bool panelOpen, PilotPhase phase)
    {
        if (!captureAlive)
            return LoopDecision.Wait("screen capture off");
        if (!_frames.Fresh(nowTicks, frameTicks))
            return LoopDecision.Wait("no recent frame from the screen capture");
        if (!gameInFront)
            return LoopDecision.Wait("the game is not in front");
        if (panelOpen)
            return LoopDecision.Wait("the control panel is open");
        if (_titleReadAt > 0 && nowTicks - _titleReadAt <= TitleGraceMs * TimeSpan.TicksPerMillisecond)
            return LoopDecision.Go;
        if (RunsWithoutTitle(phase))
            return LoopDecision.Go;
        return LoopDecision.Wait("the minimap's zone title does not read");
    }

    /// <summary>
    /// Phases in which the game's own screen may hide the minimap and the pilot must still tick: before and between
    /// runs, the loading screen and the zone-name wait after it, the login screens, and the town windows the pilot
    /// opens itself (☰ panel, Settings, the teleport list).
    /// </summary>
    public static bool RunsWithoutTitle(PilotPhase phase) => phase is
        PilotPhase.Idle or PilotPhase.Focus or PilotPhase.WaitForLoad or PilotPhase.ZoomOut or PilotPhase.Reconnect
        or PilotPhase.FontSize or PilotPhase.OpenInventory or PilotPhase.FindScroll or PilotPhase.FindChevron
        or PilotPhase.FindDestination or PilotPhase.RegularWarOn or PilotPhase.Done or PilotPhase.Failed;

    /// <summary>True when one of the OCR lines of the minimap crop is a zone title ("[Piercing City]").</summary>
    public static bool TitleReads(IEnumerable<string> lines) => lines.Any(WarText.IsZoneTitleLine);
}
