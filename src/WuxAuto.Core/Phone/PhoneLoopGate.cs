using WuxAuto.Automation;

namespace WuxAuto.Phone;

/// <summary>Why the phone engine does not tick the pilot on a pass.</summary>
public enum LoopWait
{
    None,
    CaptureOff,
    FramesRejected,
    NoRecentFrame,
    NoFrameSinceTouch,
    NotInFront,
    PanelOpen,
    NoTitle,
}

/// <summary>Whether the phone engine ticks the pilot on this pass, and why not when it does not.</summary>
public readonly record struct LoopDecision(bool Tick, string Reason, LoopWait Kind = LoopWait.None)
{
    public static LoopDecision Go { get; } = new(true, string.Empty);

    public static LoopDecision Wait(LoopWait kind, string reason) => new(false, reason, kind);
}

/// <summary>What the capture reports for one pass (all times <see cref="FrameGate.NowTicks"/> ticks, 0 = never).</summary>
/// <param name="Alive">The capture runs (a projection exists).</param>
/// <param name="FrameTicks">When the newest accepted frame came in.</param>
/// <param name="LastRejectedTicks">When the capture last turned a frame down (black, unexpected size, a failed copy).</param>
/// <param name="RejectReason">Why, for the log.</param>
public readonly record struct CaptureState(bool Alive, long FrameTicks, long LastRejectedTicks = 0, string? RejectReason = null);

/// <summary>
/// The phone engine's per-pass check before <see cref="AutomationPilot.TickAsync"/>: only a current picture of the game
/// counts. In order: the capture runs; frames are not all being turned down (<see cref="RejectedForMs"/>); the newest
/// frame is not older than <see cref="StaleMs"/>; a touch sent <see cref="TouchFrameMs"/> ago has been followed by a
/// frame; the game is the app in front; the floating control's panel is shut (it covers the game); and the minimap's
/// zone title read within <see cref="TitleGraceMs"/> (the M0 rule: a launcher frame is not black, so "not black" alone
/// proves nothing) - except in the phases where the game's own screen hides the minimap (<see cref="RunsWithoutTitle"/>).
/// Times are <see cref="FrameGate.NowTicks"/> ticks. Loop thread only.
/// </summary>
public sealed class PhoneLoopGate
{
    /// <summary>
    /// The newest frame may be this old when nothing was touched since. MediaProjection hands over a frame only when the
    /// screen changes, so a still screen is not stale - its last frame is the screen. In town and in the dungeons the
    /// HUD's ping / FPS counter and the world change all the time, but the game's own dialogs can stand still: MuMu's
    /// "Update available" screen sent no frame for over 30 s (2026-10-28; a first 30 s limit paused the pilot there).
    /// 5 minutes without a single frame while the capture claims to run = stuck or the screen is off.
    /// </summary>
    public const int StaleMs = 300_000;

    /// <summary>
    /// A touch changes the screen (or at least the HUD keeps changing): no accepted frame this long after a touch began
    /// means the picture the pilot would act on is from before its own touch - wait (review ruling I-3).
    /// </summary>
    public const int TouchFrameMs = 3_000;

    /// <summary>Frames still arrive but every one has been turned down for this long: the capture is not usable (I-3).</summary>
    public const int RejectedForMs = 10_000;

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

    /// <inheritdoc cref="Decide(long, CaptureState, bool, bool, PilotPhase, long)"/>
    public LoopDecision Decide(long nowTicks, bool captureAlive, long frameTicks, bool gameInFront, bool panelOpen, PilotPhase phase) =>
        Decide(nowTicks, new CaptureState(captureAlive, frameTicks), gameInFront, panelOpen, phase);

    /// <param name="lastTouchTicks">When the newest touch began (0 = none since the start).</param>
    public LoopDecision Decide(long nowTicks, CaptureState capture, bool gameInFront, bool panelOpen, PilotPhase phase, long lastTouchTicks = 0)
    {
        if (!capture.Alive)
            return LoopDecision.Wait(LoopWait.CaptureOff, "screen capture off");

        long frame = capture.FrameTicks;
        if (capture.LastRejectedTicks > frame
            && nowTicks - Math.Max(frame, 0) >= Ms(RejectedForMs)
            && nowTicks - capture.LastRejectedTicks <= Ms(RejectedForMs))
            return LoopDecision.Wait(LoopWait.FramesRejected,
                $"the screen capture turns every frame down ({capture.RejectReason ?? "unknown reason"})");

        if (!_frames.Fresh(nowTicks, frame))
            return LoopDecision.Wait(LoopWait.NoRecentFrame, "no recent frame from the screen capture");
        if (lastTouchTicks > 0 && frame < lastTouchTicks && nowTicks - lastTouchTicks >= Ms(TouchFrameMs))
            return LoopDecision.Wait(LoopWait.NoFrameSinceTouch, "no new frame since the last touch");
        if (!gameInFront)
            return LoopDecision.Wait(LoopWait.NotInFront, "the game is not in front");
        if (panelOpen)
            return LoopDecision.Wait(LoopWait.PanelOpen, "the control panel is open");
        if (_titleReadAt > 0 && nowTicks - _titleReadAt <= Ms(TitleGraceMs))
            return LoopDecision.Go;
        if (RunsWithoutTitle(phase))
            return LoopDecision.Go;
        return LoopDecision.Wait(LoopWait.NoTitle, "the minimap's zone title does not read");
    }

    /// <summary>
    /// Whether the engine must lift every touch on this pass: on entering a wait in which a held finger would press on
    /// something that is not the game (another app in front, the panel over the game). Once per entry.
    /// </summary>
    public static bool MustRelease(LoopDecision previous, LoopDecision current) =>
        current.Kind is LoopWait.NotInFront or LoopWait.PanelOpen && previous.Kind != current.Kind;

    /// <summary>
    /// Whether the engine hands this pass's frame to <see cref="AutomationPilot.TickWithoutTitleAsync"/> instead of the
    /// full tick: only while the zone title is all that is missing (capture, frame, front and panel are fine). The login
    /// screens have no minimap, so this is how the pilot still sees a disconnect - and how a pause without a title ends
    /// after 2 minutes (the pilot's own limit) instead of lasting for ever.
    /// </summary>
    public static bool TicksWithoutTitle(LoopDecision decision) => !decision.Tick && decision.Kind == LoopWait.NoTitle;

    /// <summary>The pilot was running before its tick and is not after it (Done, Fail, Stop): lift every touch.</summary>
    public static bool StoppedDuringTick(bool runningBefore, bool runningAfter) => runningBefore && !runningAfter;

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

    private static long Ms(int ms) => ms * TimeSpan.TicksPerMillisecond;
}
