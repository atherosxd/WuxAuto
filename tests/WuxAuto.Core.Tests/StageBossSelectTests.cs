using Emgu.CV;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Drives the pilot through one Labyrinth stage's boss selection (WaitBoss -> SelectBoss -> WatchBoss)
/// on a blank frame with a scripted OCR scene: the minimap title, the stage 1 boss name and a full
/// combat log whose lines change after every click. Checks the input order the user relies on:
/// stages 1-4 press J and then click round the centre until a new "Attack succeeded" line shows.
/// </summary>
public class StageBossSelectTests : AppPathsScope
{
    private const int Width = 1440;
    private const int Height = 900;

    [Fact]
    public async Task Stage1_HoldBeforeAutohunt_HoldsThenJ_ThenCentreClicksUntilAHit()
    {
        var (settings, input, ocr) = Harness(holdBeforeAutohunt: true);
        settings.Dungeon.HoldSkillMs = 100;

        var run = await RunStageAsync(settings, input, ocr);

        Assert.Equal(PilotPhase.WatchBoss, run.Phase);
        int tab = Index(input, "KeyTap Tab");
        int down = Index(input, "KeyDown 1");
        int up = Index(input, "KeyUp 1");
        int ctrl = Index(input, "CtrlKeyTap 1");
        int j = Index(input, "KeyTap J");
        int click = input.Calls.FindIndex(c => c.StartsWith("Click ", StringComparison.Ordinal));

        Assert.True(tab < down && down < up && up < ctrl && ctrl < j && j < click,
            "expected Tab, hold (down/up), Ctrl+skill, J, then the centre click:\n" + string.Join("\n", input.Calls));
        Assert.Equal(1, input.Calls.Count(c => c == "KeyTap J"));
        Assert.Equal(1, input.Clicks); // the first click's new line counted as the hit
        Assert.Equal($"Click {Width / 2},{Height / 2}", input.Calls[click]);
        Assert.Contains(run.Lines, l => l.Contains("Select click 1/", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("boss selected on click 1", StringComparison.Ordinal));
    }

    /// <summary>
    /// Live 2026-10-29 (v0.21.0): the Ctrl + skill tap right before J still printed hits ~1-2 s after
    /// it, the baseline was taken 1 s after J, and one of those late lines after centre click 1 read as
    /// "boss selected" - the clicks stopped at the centre while the stage 2 boss stood above it (before
    /// the hold, stage 2 always needed click 2, stage 3 click 3). The clicks must wait for a quiet log.
    /// </summary>
    [Fact]
    public async Task Stage1_HoldBeforeAutohunt_LateHitsAfterJ_DoNotCountAsTheSelection()
    {
        var (settings, input, _) = Harness(holdBeforeAutohunt: true);
        settings.Dungeon.HoldSkillMs = 100;
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        {
            // the held press' late hits: a new line every 250 ms for 2 s after J
            long? jAt = input.FirstAt("KeyTap J");
            int late = jAt is long at ? (int)Math.Min((Environment.TickCount64 - at) / 250, 8) : 0;
            // click 1 (the centre) misses, click 2 hits
            int hits = Math.Max(0, input.Clicks - 1);
            return
            [
                FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1000, 10),
                FakeOcrEngine.Line(settings.Dungeon.Stages[0], 700, 300),
                .. Enumerable.Range(0, 8).Select(i =>
                    FakeOcrEngine.Line($"Attack succeeded!Normal damage(+{100 + i + late + 50 * hits}) Attribu", 20, 200 + i * 18, 300)),
            ];
        };

        var run = await RunStageAsync(settings, input, ocr);

        Assert.Equal(PilotPhase.WatchBoss, run.Phase);
        Assert.Equal(2, input.Clicks);
        Assert.Contains(run.Lines, l => l.Contains("boss selected on click 2", StringComparison.Ordinal));
        long jAt = input.FirstAt("KeyTap J")!.Value;
        long firstClick = input.FirstAt($"Click {Width / 2},{Height / 2}")!.Value;
        Assert.True(firstClick - jAt >= 2000, $"first click {firstClick - jAt} ms after J, inside the late hits");
    }

    /// <summary>A log that never goes quiet (something keeps hitting) must not hold the clicks back for ever.</summary>
    [Fact]
    public async Task Stage1_HoldBeforeAutohunt_LogNeverQuiet_ClicksAfterTheCap()
    {
        var (settings, input, _) = Harness(holdBeforeAutohunt: true);
        settings.Dungeon.HoldSkillMs = 100;
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        [
            FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1000, 10),
            FakeOcrEngine.Line(settings.Dungeon.Stages[0], 700, 300),
            .. Enumerable.Range(0, 8).Select(i =>
                FakeOcrEngine.Line($"Attack succeeded!Normal damage(+{100 + i + Environment.TickCount64 / 250}) Attribu", 20, 200 + i * 18, 300)),
        ];

        var run = await RunStageAsync(settings, input, ocr);

        Assert.Equal(PilotPhase.WatchBoss, run.Phase);
        Assert.True(input.Clicks >= 1);
        Assert.Contains(run.Lines, l => l.Contains("still changing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage1_HoldSwitchOff_PressesJAfterTheSpawnWait_ThenCentreClicks()
    {
        var (settings, input, ocr) = Harness(holdBeforeAutohunt: false);

        var run = await RunStageAsync(settings, input, ocr);

        Assert.Equal(PilotPhase.WatchBoss, run.Phase);
        int j = Index(input, "KeyTap J");
        int tab = Index(input, "KeyTap Tab");
        int click = input.Calls.FindIndex(c => c.StartsWith("Click ", StringComparison.Ordinal));
        Assert.True(j < tab && tab < click, "expected J, Tab, then the centre click:\n" + string.Join("\n", input.Calls));
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("KeyDown", StringComparison.Ordinal));
        Assert.Equal(1, input.Calls.Count(c => c == "KeyTap J"));
        Assert.Equal(1, input.Clicks);
    }

    private static int Index(FakeGameInput input, string call)
    {
        int at = input.Calls.IndexOf(call);
        Assert.True(at >= 0, $"'{call}' was never sent:\n" + string.Join("\n", input.Calls));
        return at;
    }

    private static (AppSettings Settings, FakeGameInput Input, FakeOcrEngine Ocr) Harness(bool holdBeforeAutohunt)
    {
        var settings = new AppSettings();
        settings.Dungeon.HoldBeforeAutohunt = holdBeforeAutohunt;
        settings.Dungeon.BossSpawnWaitMs = 0;
        settings.Dungeon.TabSettleMs = 0;
        settings.Dungeon.BossSelectDelayMs = 0;
        settings.Dungeon.AttackConfirmMs = 100;

        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        [
            // minimap title (Regions.Minimap = right 40 %, top 42 %)
            FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1000, 10),
            // the stage 1 boss's name tag in the middle of the screen
            FakeOcrEngine.Line(settings.Dungeon.Stages[0], 700, 300),
            // the combat log (Regions.AttackMessage = left 40 %): 8 lines, always full; every click
            // scrolls a new hit in, so the damage numbers shift by the click count
            .. Enumerable.Range(0, 8).Select(i =>
                FakeOcrEngine.Line($"Attack succeeded!Normal damage(+{100 + i + input.Clicks}) Attribu", 20, 200 + i * 18, 300)),
        ];
        return (settings, input, ocr);
    }

    private sealed record StageRun(PilotPhase Phase, List<string> Lines);

    private static async Task<StageRun> RunStageAsync(AppSettings settings, FakeGameInput input, FakeOcrEngine ocr)
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets")); // no templates: the boss is found by name
        try
        {
            using var analyzer = new ScreenAnalyzer(settings, ocr);
            var pilot = new AutomationPilot(settings, analyzer, new KeyboardMouseControls(settings, input));
            var lines = new List<string>();
            pilot.Log += (_, text) => lines.Add(text);
            pilot.Start(PilotPhase.WaitBoss, stageIndex: 0);

            using var frame = Blank.Bgra(Height, Width);
            long deadline = Environment.TickCount64 + 15000;
            while (pilot.Phase != PilotPhase.WatchBoss && pilot.IsRunning && Environment.TickCount64 < deadline)
            {
                await pilot.TickAsync(frame, CancellationToken.None);
                await Task.Delay(20);
            }

            var phase = pilot.Phase;
            pilot.Stop("test over");
            return new StageRun(phase, lines);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
