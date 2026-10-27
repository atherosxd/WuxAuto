using OpenCvSharp;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto LOD after the south turn (LodRun) on a blank frame with a scripted OCR scene. By default
/// (<see cref="LodSettings.WalkToBossSpot"/>, user 2026-10-28) the character walks from where the
/// teleport put it to 1199,1782 and waits there tabbing; off, it stays put and only Tab goes out until
/// the boss name shows (user, 2026-10-27).
/// </summary>
[Collection("AppPaths")] // AppPaths is static state
public class LodStandStillTests
{
    private const int Width = 1440;
    private const int Height = 900;
    private const int TabsBeforeBoss = 3;

    [Fact]
    public async Task StandStill_OnlyTabsUntilTheBossShows_ThenAttacks()
    {
        var (settings, input, ocr) = Harness(walk: false, bossAfterTabs: TabsBeforeBoss);

        var run = await RunAsync(settings, input, ocr, done: p => p.Phase == PilotPhase.LodAttack);

        Assert.True(run.Phase == PilotPhase.LodAttack, $"phase {run.Phase}; log:\n" + string.Join("\n", run.Lines));
        Assert.True(Tabs(input) >= TabsBeforeBoss, "expected Tab presses before the boss:\n" + string.Join("\n", input.Calls));
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("Click", StringComparison.Ordinal)
                                                || c.StartsWith("CtrlClick", StringComparison.Ordinal)
                                                || c.StartsWith("Drag", StringComparison.Ordinal)
                                                || c.StartsWith("Wheel", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.StartsWith("Staying here", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("targeted - spamming skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WalkToBossSpot_On_ClicksTowardsTheSpot()
    {
        var (settings, input, ocr) = Harness(walk: true, bossAfterTabs: int.MaxValue);

        var run = await RunAsync(settings, input, ocr, done: _ => input.Clicks > 0);

        Assert.Equal(PilotPhase.LodRun, run.Phase);
        Assert.True(input.Clicks > 0, "expected a walk click:\n" + string.Join("\n", input.Calls));
        Assert.Contains(run.Lines, l => l.StartsWith("LOD: at 1000, 2000", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Staying here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Defaults_WalkFromTheLandingSpotTo1199_1782_ThenOnlyTab()
    {
        var settings = new AppSettings();
        Assert.True(settings.Lod.WalkToBossSpot);
        Assert.Equal((1199, 1782), (settings.Lod.TargetX, settings.Lod.TargetY));
        settings.Sequence.Mode = RunMode.AutoLod;
        settings.Lod.TabEveryMs = 500;
        settings.Lod.BuffKey = string.Empty;

        // Where the teleport puts the character (5 of the 6 LOD walks in the logs of 2026-10-22/24 began there).
        string coords = "1195 , 1578";
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine
        {
            Scene = () =>
            [
                FakeOcrEngine.Line("[Ultimate Den of Rebirth]", 1000, 10),
                FakeOcrEngine.Line(coords, 1100, 200, 120),
            ],
        };

        long arrivedAt = 0;
        int clicksAtArrival = -1, tabsAtArrival = -1;
        using var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.LodRun, (_, lines) =>
        {
            if (input.Clicks > 0)
                coords = "1201 , 1760"; // the walk got there (22 from the spot)
            if (arrivedAt == 0 && lines.Any(l => l.StartsWith("At 1201, 1760, the boss spot", StringComparison.Ordinal)))
            {
                arrivedAt = Environment.TickCount64;
                clicksAtArrival = input.Clicks;
                tabsAtArrival = Tabs(input);
            }

            return arrivedAt > 0 && Environment.TickCount64 - arrivedAt > 1500;
        });

        string log = string.Join("\n", run.Lines);
        Assert.Contains(run.Lines, l => l.StartsWith("LOD: at 1195, 1578 (204 from 1199,1782)", StringComparison.Ordinal));
        Assert.True(arrivedAt > 0, "never reached the spot:\n" + log);
        Assert.Equal(clicksAtArrival, input.Clicks); // no walk click once there
        Assert.True(Tabs(input) > tabsAtArrival, "expected Tab to go on at the spot:\n" + string.Join("\n", input.Calls));
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Staying here", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.LodRun, run.Phase);
    }

    private static int Tabs(FakeGameInput input) => input.Calls.Count(c => c == "KeyTap Tab");

    private static (AppSettings Settings, FakeGameInput Input, FakeOcrEngine Ocr) Harness(bool walk, int bossAfterTabs)
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLod;
        settings.Lod.WalkToBossSpot = walk;
        settings.Lod.TabEveryMs = 500; // the settings clamp (300+) keeps it past the 250 ms wait after a Tab
        settings.Lod.BuffKey = string.Empty; // keeps the input list to Tab (+ clicks)

        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        {
            var words = new List<OcrWord>
            {
                // minimap title and coordinates (Regions.Minimap = right 40 %, top 42 %)
                FakeOcrEngine.Line("[Ultimate Den of Rebirth]", 1000, 10),
                FakeOcrEngine.Line("1000 , 2000", 1100, 200, 120),
            };
            // the target bar after enough Tabs
            if (Tabs(input) >= bossAfterTabs)
                words.Add(FakeOcrEngine.Line("[LOD] " + settings.Lod.BossName, 640, 50));
            return words;
        };
        return (settings, input, ocr);
    }

    private static async Task<(PilotPhase Phase, List<string> Lines)> RunAsync(AppSettings settings, FakeGameInput input,
        FakeOcrEngine ocr, Func<AutomationPilot, bool> done)
    {
        using var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
        return await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.LodRun, (pilot, _) => done(pilot));
    }
}
