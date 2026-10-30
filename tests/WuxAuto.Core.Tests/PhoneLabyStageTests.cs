using Emgu.CV;
using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The phone pilot's Labyrinth boss selection (WaitBoss -> SelectBoss -> WatchBoss) on a grey 1920x1080 frame with a
/// scripted OCR scene (minimap title, the boss name, a full combat log that shifts with every tap), as
/// <see cref="StageBossSelectTests"/> does for the desktop. Live 2026-10-30 (MuMu): the held skill press before the
/// autohunt found no attack icon and was retried silently for minutes - no autohunt, no gate banner read.
/// </summary>
public class PhoneLabyStageTests : AppPathsScope
{
    private static readonly PixelRect Hunt = new(151, 967, 32, 32);
    private static readonly PixelRect Sword = new(1595, 755, 120, 120);
    private static readonly PixelRect Attack = new(626, 984, 60, 60);

    [Fact]
    public async Task Stage1_NoAttackIcon_GivesUpTheHold_TurnsTheAutohuntOn_AndSelects()
    {
        using var phone = Harness(attackIcon: false);

        var phase = await RunToWatchBossAsync(phone, stageIndex: 0);

        Assert.Equal(PilotPhase.WatchBoss, phase);
        Assert.Contains(phone.Lines, l => l.Contains("No attack skill icon on the hotbar or the skill wheel", StringComparison.Ordinal));
        Assert.Equal(1, phone.Shortcuts.AutohuntPresses);
        Assert.Contains(phone.Touch.Taps, t => Inside(Hunt, t));
        Assert.Contains(phone.Lines, l => l.Contains("boss selected on click", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage1_AutohuntShownOnAlready_HoldsButDoesNotTapHunt()
    {
        using var phone = Harness(attackIcon: true);
        phone.Shortcuts.AutohuntOn = true;

        var phase = await RunToWatchBossAsync(phone, stageIndex: 0);

        Assert.Equal(PilotPhase.WatchBoss, phase);
        Assert.Contains(phone.Touch.Calls, c => c == $"Down {Attack.CenterX},{Attack.CenterY}"); // the held press
        Assert.Equal(0, phone.Shortcuts.AutohuntPresses);
        Assert.DoesNotContain(phone.Touch.Taps, t => Inside(Hunt, t));
        Assert.Contains(phone.Lines, l => l.Contains("on already (the HUNT button shows the '+')", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage1_AttackIconFound_HoldsIt_ThenTapsHunt()
    {
        using var phone = Harness(attackIcon: true);
        phone.Shortcuts.AutohuntOn = false;

        var phase = await RunToWatchBossAsync(phone, stageIndex: 0);

        Assert.Equal(PilotPhase.WatchBoss, phase);
        int down = phone.Touch.Calls.IndexOf($"Down {Attack.CenterX},{Attack.CenterY}");
        int hunt = phone.Touch.Calls.FindIndex(c => c == $"Tap {Hunt.CenterX},{Hunt.CenterY}");
        Assert.True(down >= 0 && hunt > down, "expected the held press, then HUNT:\n" + string.Join("\n", phone.Touch.Calls));
        Assert.Equal(1, phone.Shortcuts.AutohuntPresses);
        Assert.DoesNotContain(phone.Lines, l => l.Contains("No attack skill icon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalBoss_HuntShowsOn_TappedOff_ConfirmedByTheButton()
    {
        using var phone = Harness(attackIcon: true);
        phone.Shortcuts.AutohuntOn = true;
        phone.Shortcuts.OnAutohuntPressed = () => phone.Shortcuts.AutohuntOn = false;

        phone.Pilot.Start(PilotPhase.WaitBoss, stageIndex: phone.Settings.Dungeon.Stages.Count);
        using var frame = PhoneFrames.Grey();
        long deadline = Environment.TickCount64 + 15000;
        while (!phone.Lines.Any(l => l.Contains("Autohunt is off", StringComparison.Ordinal))
               && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        phone.Pilot.Stop("test over");
        Assert.Contains(phone.Lines, l => l.Contains("Autohunt is off at the final boss (the HUNT button shows the yellow swords)", StringComparison.Ordinal));
        Assert.Equal(1, phone.Shortcuts.AutohuntPresses);
    }

    [Fact]
    public async Task Stage1_AutohuntOnButNoHits_HuntOff_HoldAndHuntAgain()
    {
        // Live 2026-10-30: the stage 3 boss stood at 13077973 HP for 26+ min, the character inside it and the autohunt on,
        // only "Defense failed" lines. HUNT off, a held press, HUNT on and a centre tap had it hitting again at once.
        using var phone = Harness(attackIcon: true);
        phone.Settings.Dungeon.AutohuntStallMs = 3000;

        Assert.Equal(PilotPhase.WatchBoss, await RunToWatchBossAsync(phone, stageIndex: 0, stop: false));
        int downs = phone.Touch.Calls.Count(c => c.StartsWith("Down ", StringComparison.Ordinal));
        long deadline = Environment.TickCount64 + 20000;
        using var frame = PhoneFrames.Grey();
        // The combat log only moves with a tap: in WatchBoss nothing is tapped, so no new attack line comes.
        while (!(phone.Lines.Any(l => l.Contains("select clicks again (stage 1)", StringComparison.Ordinal)) && phone.Pilot.Phase == PilotPhase.WatchBoss)
               && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        var phase = phone.Pilot.Phase;
        phone.Pilot.Stop("test over");
        Assert.Contains(phone.Lines, l => l.Contains("Autohunt on but no new 'Attack succeeded'", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.WatchBoss, phase);
        Assert.True(phone.Touch.Calls.Count(c => c.StartsWith("Down ", StringComparison.Ordinal)) > downs, "the held press again");
        Assert.Equal(3, phone.Shortcuts.AutohuntPresses); // on, off after the stall, on again
    }

    [Theory]
    [InlineData(false, 1)] // HUNT shows the yellow swords: the resume holds and turns the autohunt on
    [InlineData(true, 0)]  // HUNT shows the "+": it is on, never tapped
    public async Task ResumeInsideTheLabyrinth_ReadsHunt(bool huntOn, int huntTaps)
    {
        // Live 2026-10-30: a Start inside the Labyrinth took the autohunt for on (J is a toggle) while it was off, and the
        // stage 1 boss at 1 % took 14 min.
        using var phone = Harness(attackIcon: true);
        phone.Shortcuts.AutohuntOn = huntOn;
        var scene = phone.Ocr.Scene;
        string finalBoss = phone.Settings.Dungeon.FinalBoss;
        phone.Ocr.Scene = () => scene().Where(w => w.Text != finalBoss).ToList(); // only the stage 1 boss in view

        phone.Pilot.Start(PilotPhase.CheckZone);
        using var frame = PhoneFrames.Grey();
        long deadline = Environment.TickCount64 + 20000;
        while (phone.Pilot.Phase != PilotPhase.WatchBoss && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        var phase = phone.Pilot.Phase;
        phone.Pilot.Stop("test over");
        Assert.True(phase == PilotPhase.WatchBoss, phone.Log);
        Assert.Contains(phone.Lines, l => l.Contains("resuming at stage 1", StringComparison.Ordinal));
        Assert.Equal(huntTaps, phone.Shortcuts.AutohuntPresses);
        Assert.Equal(huntTaps, phone.Touch.Taps.Count(t => Inside(Hunt, t)));
    }

    [Theory]
    [InlineData("The Labyrinth (Adept)", false)] // the map window's own title: no hold
    [InlineData("Some Other Map", true)]         // not the destination: the guard holds as before
    public async Task ReadStage_MapCoversTheMinimap_TheMapsTitleKeepsTheZoneGuardGoing(string mapTitle, bool holds)
    {
        // Live MuMu 2026-10-30: the full-screen phone map hides the minimap; paging for the stage 4 boss took ~10 s, the
        // zone guard held ReadStage after 6 s, and the stage gate closed with the map still open.
        using var phone = Harness(attackIcon: true);
        phone.Ocr.Scene = () => [FakeOcrEngine.Line(mapTitle, 310, 85, 350, 30)]; // no minimap title, no boss name

        phone.Pilot.Start(PilotPhase.ReadStage, stageIndex: 3);
        using var frame = PhoneFrames.Grey();
        long deadline = Environment.TickCount64 + 9000;
        while (phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        phone.Pilot.Stop("test over");
        Assert.Equal(holds, phone.Lines.Any(l => l.Contains("The minimap does not show Labyrinth - holding ReadStage", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task BossGone_StraightToTheMap_NoGateWait()
    {
        // User, 2026-10-30: "when the boss dies, already try to open the map and select the next boss" / "don't wait for
        // 3s" - the stage gate opens 5 s after the kill and stays open only 60 s. The old 15 s fallback is switched off.
        using var phone = Harness(attackIcon: true);
        phone.Settings.Dungeon.BossGoneMs = 500;
        phone.Settings.Dungeon.BossPollMs = 200;
        phone.Settings.Dungeon.GateFallbackMs = 0;
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 80)];

        phone.Pilot.Start(PilotPhase.WatchBoss, stageIndex: 0);
        using var frame = PhoneFrames.Grey();
        long deadline = Environment.TickCount64 + 20000;
        long goneAt = 0;
        while (phone.Pilot.Phase != PilotPhase.ClickMap && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            if (goneAt == 0 && phone.Lines.Any(l => l.Contains("Boss for stage 1 is gone", StringComparison.Ordinal)))
                goneAt = Environment.TickCount64;
            await Task.Delay(15);
        }

        var phase = phone.Pilot.Phase;
        long took = Environment.TickCount64 - goneAt;
        phone.Pilot.Stop("test over");
        Assert.Equal(PilotPhase.ClickMap, phase);
        Assert.Contains(phone.Lines, l => l.Contains("the stage 1 boss is dead - its gate opens 5 s after the kill", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.Contains("Waiting for the stage 2 gate", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.Contains("before opening the map", StringComparison.Ordinal));
        Assert.True(took < 1500, $"the map after {took} ms");
    }

    [Fact]
    public async Task Arrival_NoBannerRead_MapOpenedByTheFirstGatesTimer()
    {
        // The first gate opens 30 s after arriving (Wuxen wiki, user): with no banner read the phone opens the map then.
        // 1 s here instead of 30.
        using var phone = Harness(attackIcon: true);
        phone.Settings.Dungeon.NorthOnArrival = false;
        phone.Settings.Dungeon.GateFallbackMs = 0;
        phone.Pilot.LabyFirstGateMs = 1000;
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 80)];

        phone.Pilot.Start(PilotPhase.ZoomOut, stageIndex: 0);
        using var frame = PhoneFrames.Grey();
        long deadline = Environment.TickCount64 + 20000;
        while (phone.Pilot.Phase != PilotPhase.ClickMap && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        var phase = phone.Pilot.Phase;
        phone.Pilot.Stop("test over");
        Assert.True(phase == PilotPhase.ClickMap, phone.Log);
        Assert.True(phone.Lines.Any(l => l.Contains("stage 1 gate is open by its fixed timer (30 s after arriving)", StringComparison.Ordinal)), phone.Log);
    }

    [Fact]
    public void ToggleOff_HuntShowsOffAlready_NotTapped_ThroughTheControls()
    {
        using var phone = Harness(attackIcon: true);
        phone.Shortcuts.AutohuntOn = false;
        Assert.False(phone.Controls.AutohuntOn);
        phone.Shortcuts.AutohuntOn = null;
        Assert.Null(phone.Controls.AutohuntOn);
    }

    private static bool Inside(PixelRect r, System.Drawing.Point p) => p.X >= r.X && p.X < r.Right && p.Y >= r.Y && p.Y < r.Bottom;

    private PhonePilot Harness(bool attackIcon)
    {
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        settings.Dungeon.HoldBeforeAutohunt = true;
        settings.Dungeon.HoldSkillMs = 100;
        settings.Dungeon.BossSpawnWaitMs = 0;
        settings.Dungeon.TabSettleMs = 0;
        settings.Dungeon.BossSelectDelayMs = 0;
        settings.Dungeon.AttackConfirmMs = 100;

        var phone = new PhonePilot(TempRoot(), settings);
        phone.Shortcuts.Icons[GameAction.TargetNearest] = Sword;
        phone.Shortcuts.Icons[GameAction.ToggleAutohunt] = Hunt;
        if (attackIcon)
            phone.Shortcuts.Skills[settings.Dungeon.SkillKey] = Attack;

        phone.Ocr.Scene = () =>
        [
            FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 80),
            FakeOcrEngine.Line(settings.Dungeon.Stages[0], 900, 400),
            FakeOcrEngine.Line(settings.Dungeon.FinalBoss, 900, 440),
            // the combat log: 8 lines, always full; every tap scrolls a new hit in
            .. Enumerable.Range(0, 8).Select(i =>
                FakeOcrEngine.Line($"Attack succeeded!Normal damage(+{100 + i + phone.Touch.Taps.Count}) Attribu", 20, 300 + i * 24, 400)),
        ];
        return phone;
    }

    private static async Task<PilotPhase> RunToWatchBossAsync(PhonePilot phone, int stageIndex, bool stop = true)
    {
        phone.Pilot.Start(PilotPhase.WaitBoss, stageIndex: stageIndex);
        using var frame = PhoneFrames.Grey();
        long deadline = Environment.TickCount64 + 20000;
        while (phone.Pilot.Phase != PilotPhase.WatchBoss && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        var phase = phone.Pilot.Phase;
        if (stop)
            phone.Pilot.Stop("test over");
        return phase;
    }
}
