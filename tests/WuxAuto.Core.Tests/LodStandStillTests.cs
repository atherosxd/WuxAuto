using OpenCvSharp;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto LOD after the south turn (LodRun) on a blank frame with a scripted OCR scene: by default the
/// character stays where the teleport put it and only Tab goes out until the boss name shows (user,
/// 2026-10-27); with <see cref="LodSettings.WalkToBossSpot"/> the old walk to the boss spot clicks.
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
