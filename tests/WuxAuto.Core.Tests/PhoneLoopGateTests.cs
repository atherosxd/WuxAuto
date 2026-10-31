using WuxAuto.Automation;
using WuxAuto.Phone;

namespace WuxAuto.Core.Tests;

public class PhoneLoopGateTests
{
    static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

    static readonly long T0 = Ms(1_000_000);

    static PhoneLoopGate GateWithTitleAt(long at)
    {
        var gate = new PhoneLoopGate();
        gate.TitleChecked(at, read: true);
        return gate;
    }

    [Fact]
    public void GameFrameWithTheTitle_Ticks()
    {
        var gate = GateWithTitleAt(T0);

        var d = gate.Decide(T0 + Ms(100), captureAlive: true, frameTicks: T0, gameInFront: true, panelOpen: false, PilotPhase.LodAttack);

        Assert.True(d.Tick);
    }

    [Fact]
    public void CaptureOff_Waits()
    {
        var gate = GateWithTitleAt(T0);

        var d = gate.Decide(T0 + Ms(100), captureAlive: false, frameTicks: T0, gameInFront: true, panelOpen: false, PilotPhase.LodAttack);

        Assert.False(d.Tick);
        Assert.Contains("capture", d.Reason);
    }

    [Fact]
    public void StaticScreen_IsNotStale()
    {
        // MediaProjection only delivers a frame when the screen changes: a still screen keeps the last one current.
        // A game dialog that stood still for over 30 s (MuMu, 2026-10-28) still counts.
        var gate = GateWithTitleAt(T0 + Ms(119_000));

        var d = gate.Decide(T0 + Ms(120_000), captureAlive: true, frameTicks: T0, gameInFront: true, panelOpen: false, PilotPhase.LodAttack);

        Assert.True(d.Tick);
    }

    [Fact]
    public void NoFrameForLong_Waits()
    {
        var gate = GateWithTitleAt(T0 + Ms(PhoneLoopGate.StaleMs));

        var d = gate.Decide(T0 + Ms(PhoneLoopGate.StaleMs + 1), captureAlive: true, frameTicks: T0, gameInFront: true, panelOpen: false, PilotPhase.LodAttack);

        Assert.False(d.Tick);
        Assert.Contains("frame", d.Reason);
    }

    [Fact]
    public void NoFrameYet_Waits()
    {
        var gate = new PhoneLoopGate();

        Assert.False(gate.Decide(T0, captureAlive: true, frameTicks: 0, gameInFront: true, panelOpen: false, PilotPhase.Focus).Tick);
    }

    [Fact]
    public void LauncherOrOtherApp_Waits()
    {
        var gate = GateWithTitleAt(T0);

        // Even in a phase that may run without the title, and with a title read a moment ago.
        foreach (var phase in new[] { PilotPhase.LodAttack, PilotPhase.Focus, PilotPhase.Reconnect })
        {
            var d = gate.Decide(T0 + Ms(100), captureAlive: true, frameTicks: T0, gameInFront: false, panelOpen: false, phase);
            Assert.False(d.Tick);
            Assert.Contains("not in front", d.Reason);
        }
    }

    [Fact]
    public void PanelOpen_Waits()
    {
        var gate = GateWithTitleAt(T0);

        var d = gate.Decide(T0 + Ms(100), captureAlive: true, frameTicks: T0, gameInFront: true, panelOpen: true, PilotPhase.LodAttack);

        Assert.False(d.Tick);
        Assert.Contains("panel", d.Reason);
    }

    [Fact]
    public void TitleNotRead_WaitsInAWorldPhase()
    {
        var gate = GateWithTitleAt(T0);
        gate.TitleChecked(T0 + Ms(1_000), read: false);
        long now = T0 + Ms(PhoneLoopGate.TitleGraceMs + 1);

        var d = gate.Decide(now, captureAlive: true, frameTicks: now, gameInFront: true, panelOpen: false, PilotPhase.LodAttack);

        Assert.False(d.Tick);
        Assert.Contains("zone title", d.Reason);
    }

    [Fact]
    public void TitleNeverRead_Waits()
    {
        var gate = new PhoneLoopGate();

        var d = gate.Decide(T0, captureAlive: true, frameTicks: T0, gameInFront: true, panelOpen: false, PilotPhase.LodRun);

        Assert.False(d.Tick);
    }

    [Fact]
    public void TitleReadRecently_StillTicks_WhileAWindowCoversIt()
    {
        var gate = GateWithTitleAt(T0);
        gate.TitleChecked(T0 + Ms(2_000), read: false);   // the ☰ panel covers the minimap
        long now = T0 + Ms(PhoneLoopGate.TitleGraceMs);

        Assert.True(gate.Decide(now, captureAlive: true, frameTicks: now, gameInFront: true, panelOpen: false, PilotPhase.LodAttack).Tick);
    }

    [Theory]
    [InlineData(PilotPhase.Focus)]
    [InlineData(PilotPhase.WaitForLoad)]
    [InlineData(PilotPhase.ZoomOut)]
    [InlineData(PilotPhase.Reconnect)]
    [InlineData(PilotPhase.FontSize)]
    [InlineData(PilotPhase.FindDestination)]
    [InlineData(PilotPhase.ClickMap)]     // the Labyrinth's map window covers the whole phone screen
    [InlineData(PilotPhase.ReadStage)]
    [InlineData(PilotPhase.AdvanceStage)]
    public void PhasesWithoutTheMinimap_TickWithoutTheTitle(PilotPhase phase)
    {
        var gate = new PhoneLoopGate();

        Assert.True(PhoneLoopGate.RunsWithoutTitle(phase));
        Assert.True(gate.Decide(T0, captureAlive: true, frameTicks: T0, gameInFront: true, panelOpen: false, phase).Tick);
    }

    [Theory]
    [InlineData(PilotPhase.LodRun)]
    [InlineData(PilotPhase.LodAttack)]
    [InlineData(PilotPhase.LodLoot)]
    [InlineData(PilotPhase.WatchBoss)]
    [InlineData(PilotPhase.RunToTarget)]
    [InlineData(PilotPhase.CheckZone)]
    public void WorldPhases_NeedTheTitle(PilotPhase phase) =>
        Assert.False(PhoneLoopGate.RunsWithoutTitle(phase));

    [Theory]
    [InlineData(PilotPhase.ClickMap, true)]
    [InlineData(PilotPhase.ReadStage, true)]
    [InlineData(PilotPhase.AdvanceStage, true)]
    [InlineData(PilotPhase.WaitBoss, false)]
    [InlineData(PilotPhase.WatchBoss, false)]
    [InlineData(PilotPhase.FindDestination, false)]
    public void MapPick_IsTheMapsThreePhases(PilotPhase phase, bool mapPick) =>
        Assert.Equal(mapPick, PhoneLoopGate.IsMapPick(phase));

    [Fact]
    public void TitleCheck_DueOncePerInterval()
    {
        var gate = new PhoneLoopGate();

        Assert.True(gate.TitleCheckDue(T0));
        gate.TitleChecked(T0, read: true);
        Assert.False(gate.TitleCheckDue(T0 + Ms(PhoneLoopGate.TitleCheckEveryMs - 1)));
        Assert.True(gate.TitleCheckDue(T0 + Ms(PhoneLoopGate.TitleCheckEveryMs)));
    }

    [Fact]
    public void Reset_ForgetsTheTitle()
    {
        var gate = GateWithTitleAt(T0);

        gate.Reset();

        Assert.True(gate.TitleCheckDue(T0 + Ms(1)));
        Assert.False(gate.Decide(T0 + Ms(1), captureAlive: true, frameTicks: T0, gameInFront: true, panelOpen: false, PilotPhase.LodAttack).Tick);
    }

    // --- I-3: a touch must be followed by a frame; frames all turned down = not alive -------------------------------

    static LoopDecision DecideAt(PhoneLoopGate gate, long now, CaptureState capture, long lastTouch = 0) =>
        gate.Decide(now, capture, gameInFront: true, panelOpen: false, PilotPhase.LodAttack, lastTouch);

    [Fact]
    public void TouchWithoutANewFrame_WaitsAfterThreeSeconds()
    {
        long touch = T0 + Ms(1_000);
        var gate = GateWithTitleAt(touch + Ms(2_900));
        var capture = new CaptureState(true, T0);   // the newest frame is from before the touch

        Assert.True(DecideAt(gate, touch + Ms(PhoneLoopGate.TouchFrameMs - 1), capture, touch).Tick);
        var d = DecideAt(gate, touch + Ms(PhoneLoopGate.TouchFrameMs), capture, touch);
        Assert.False(d.Tick);
        Assert.Equal(LoopWait.NoFrameSinceTouch, d.Kind);
    }

    [Fact]
    public void FrameAfterTheTouch_Ticks()
    {
        long touch = T0 + Ms(1_000);
        var gate = GateWithTitleAt(touch + Ms(5_000));

        Assert.True(DecideAt(gate, touch + Ms(6_000), new CaptureState(true, touch + Ms(200)), touch).Tick);
        Assert.True(DecideAt(gate, touch + Ms(6_000), new CaptureState(true, touch), touch).Tick);   // stamped with the touch itself
    }

    [Fact]
    public void StillScreenWithoutTouches_StillTicksAfterMinutes()
    {
        var gate = GateWithTitleAt(T0 + Ms(239_000));

        Assert.True(DecideAt(gate, T0 + Ms(240_000), new CaptureState(true, T0), lastTouch: T0 - Ms(1)).Tick);
    }

    [Fact]
    public void FramesAllRejectedForTenSeconds_Waits_WithTheReason()
    {
        var gate = GateWithTitleAt(T0 + Ms(10_000));
        long now = T0 + Ms(PhoneLoopGate.RejectedForMs);
        var capture = new CaptureState(true, T0, LastRejectedTicks: now - Ms(100), RejectReason: "unexpected image 1080x1920");

        var d = DecideAt(gate, now, capture);

        Assert.False(d.Tick);
        Assert.Equal(LoopWait.FramesRejected, d.Kind);
        Assert.Contains("1080x1920", d.Reason);
    }

    [Fact]
    public void FramesRejectedBriefly_OrLongAgo_StillTick()
    {
        var gate = GateWithTitleAt(T0 + Ms(20_000));

        // Rejected for under 10 s since the last good frame.
        Assert.True(DecideAt(gate, T0 + Ms(9_000), new CaptureState(true, T0, T0 + Ms(8_900), "black frame")).Tick);
        // The last rejection was long ago and the screen is simply still.
        Assert.True(DecideAt(gate, T0 + Ms(20_000), new CaptureState(true, T0, T0 + Ms(5_000), "black frame")).Tick);
        // A good frame came after the rejections.
        Assert.True(DecideAt(gate, T0 + Ms(20_000), new CaptureState(true, T0 + Ms(19_000), T0 + Ms(18_000), "black frame")).Tick);
    }

    [Fact]
    public void OnlyRejectedFramesSinceTheStart_Waits()
    {
        var gate = new PhoneLoopGate();
        long now = T0 + Ms(30_000);

        var d = DecideAt(gate, now, new CaptureState(true, 0, now - Ms(50), "black frame"));

        Assert.Equal(LoopWait.FramesRejected, d.Kind);
    }

    // --- C-1 / I-5: when the engine lifts every touch -----------------------------------------------------------------

    [Fact]
    public void MustRelease_OnEnteringNotInFrontOrPanelOpen_Once()
    {
        var go = LoopDecision.Go;
        var behind = LoopDecision.Wait(LoopWait.NotInFront, "x");
        var panel = LoopDecision.Wait(LoopWait.PanelOpen, "x");
        var noTitle = LoopDecision.Wait(LoopWait.NoTitle, "x");

        Assert.True(PhoneLoopGate.MustRelease(go, behind));
        Assert.True(PhoneLoopGate.MustRelease(go, panel));
        Assert.True(PhoneLoopGate.MustRelease(behind, panel));
        Assert.True(PhoneLoopGate.MustRelease(noTitle, behind));
        Assert.False(PhoneLoopGate.MustRelease(behind, behind));   // still behind: once is enough
        Assert.False(PhoneLoopGate.MustRelease(panel, panel));
        Assert.False(PhoneLoopGate.MustRelease(behind, go));
        Assert.False(PhoneLoopGate.MustRelease(noTitle, noTitle));
    }

    [Fact]
    public void MustRelease_OnEnteringAnyWaitFromTicking()
    {
        // M4 review #2: a held finger must not stay down through a disconnect (the login / character screen under it,
        // DELETE CHARACTER there) or while the picture is old - every wait entered from a ticking pass lifts it.
        foreach (var kind in Enum.GetValues<LoopWait>().Where(k => k != LoopWait.None))
        {
            Assert.True(PhoneLoopGate.MustRelease(LoopDecision.Go, LoopDecision.Wait(kind, "x")), kind.ToString());
            Assert.False(PhoneLoopGate.MustRelease(LoopDecision.Wait(kind, "x"), LoopDecision.Wait(kind, "x")), kind.ToString());
        }
    }

    [Fact]
    public void MustRelease_ThroughTheGate_NoTitleAndStaleFrameAfterTicking()
    {
        var gate = new PhoneLoopGate();
        long now = T0 + Ms(1_000);
        gate.TitleChecked(now, read: true);
        var ticking = DecideAt(gate, now, new CaptureState(true, now));
        Assert.True(ticking.Tick);

        // The title stops reading (a disconnect): past the grace the pass waits for it - and lifts the finger.
        long later = now + Ms(PhoneLoopGate.TitleGraceMs + 1_000);
        var noTitle = gate.Decide(later, new CaptureState(true, later), gameInFront: true, panelOpen: false, PilotPhase.LodAttack);
        Assert.Equal(LoopWait.NoTitle, noTitle.Kind);
        Assert.True(PhoneLoopGate.MustRelease(ticking, noTitle));

        // A touch with no frame after it: the picture is from before the touch - wait and lift.
        var gate2 = new PhoneLoopGate();
        gate2.TitleChecked(now, read: true);
        long afterTouch = now + Ms(PhoneLoopGate.TouchFrameMs + 500);
        var stale = gate2.Decide(afterTouch, new CaptureState(true, now), true, false, PilotPhase.LodAttack, lastTouchTicks: now + Ms(100));
        Assert.Equal(LoopWait.NoFrameSinceTouch, stale.Kind);
        Assert.True(PhoneLoopGate.MustRelease(ticking, stale));
    }

    [Fact]
    public void TicksWithoutTitle_OnlyWhileTheTitleIsAllThatIsMissing()
    {
        // Task 7 ruling: the login screens have no minimap - on a no-title pass the engine still hands the frame to the
        // pilot's disconnect check (AutomationPilot.TickWithoutTitleAsync); on every other wait it hands over nothing.
        Assert.True(PhoneLoopGate.TicksWithoutTitle(LoopDecision.Wait(LoopWait.NoTitle, "x")));
        Assert.False(PhoneLoopGate.TicksWithoutTitle(LoopDecision.Go));
        foreach (var kind in Enum.GetValues<LoopWait>().Where(k => k is not (LoopWait.NoTitle or LoopWait.None)))
            Assert.False(PhoneLoopGate.TicksWithoutTitle(LoopDecision.Wait(kind, "x")), kind.ToString());
    }

    [Fact]
    public void LoginScreen_NoTitle_GateHandsTheFrameOver()
    {
        // A login screen while the pilot is mid-LOD: no title for longer than the grace -> NoTitle, handed to the pilot.
        var gate = new PhoneLoopGate();
        long now = 1_000 * TimeSpan.TicksPerSecond;
        gate.TitleChecked(now, read: true);
        now += (PhoneLoopGate.TitleGraceMs + 1) * TimeSpan.TicksPerMillisecond;
        gate.TitleChecked(now, read: false);

        var decision = gate.Decide(now, captureAlive: true, frameTicks: now, gameInFront: true, panelOpen: false, PilotPhase.LodAttack);

        Assert.Equal(LoopWait.NoTitle, decision.Kind);
        Assert.True(PhoneLoopGate.TicksWithoutTitle(decision));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void StoppedDuringTick_OnlyOnRunningToStopped(bool before, bool after, bool expected) =>
        Assert.Equal(expected, PhoneLoopGate.StoppedDuringTick(before, after));

    [Theory]
    [InlineData("[Piercing City]", true)]
    [InlineData("[Piercing Cityl", true)]
    [InlineData("[The Labyrinth (Adep", true)]
    [InlineData("(Piercing City]", true)]      // ML Kit, MuMu 2560x1440 at scale 1.11: '[' read as '('
    [InlineData("{Piercing City]", true)]
    [InlineData("(Piercing ityl", true)]
    [InlineData("(10] minute(s) to opening", false)]
    [InlineData("(", false)]
    [InlineData("3592 , 7297", false)]
    [InlineData("", false)]
    [InlineData("Piercing", false)]
    public void TitleReads_IsAZoneTitleLine(string line, bool expected) =>
        Assert.Equal(expected, PhoneLoopGate.TitleReads([line]));
}
