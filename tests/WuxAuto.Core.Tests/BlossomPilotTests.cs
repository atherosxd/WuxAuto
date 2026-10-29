using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto Blossom (user, 2026-10-29) on a blank 1440x900 frame with a scripted OCR scene: the minimap title and coordinates,
/// and a target bar line when the test says so. No templates (no pillars, no chest).
/// </summary>
[Collection("AppPaths")] // AppPaths is static state
public class BlossomPilotTests
{
    private const int Width = 1440;
    private const int Height = 900;

    [Fact]
    public async Task Walk_FirstClickIsStraightUp()
    {
        var (settings, input, ocr) = Harness(() => "19148 , 19062");

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => input.Clicks > 0);

        string click = input.Calls.First(c => c.StartsWith("Click ", StringComparison.Ordinal));
        var xy = click["Click ".Length..].Split(',').Select(int.Parse).ToArray();
        Assert.True(xy[0] == Width / 2 && xy[1] < Height / 2, $"first click {click}; log:\n" + string.Join("\n", run.Lines));
    }

    [Fact]
    public async Task Start_InsideTheMap_WalksToTheNearestWaypoint()
    {
        var (settings, input, ocr) = Harness(() => "12990 , 13190");

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Walking the route", StringComparison.Ordinal)));

        Assert.Contains(run.Lines, l => l.StartsWith("Walking the route from waypoint 10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Waypoint_PausesAtMostWaypointWaitMs_ThenNext()
    {
        var (settings, input, ocr) = Harness(() => "13947 , 14697"); // on waypoint 6
        settings.Blossom.WaypointWaitMs = 1000;
        long reachedAt = 0, nextAt = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            if (reachedAt == 0 && lines.Any(l => l.StartsWith("Waypoint 6 reached", StringComparison.Ordinal)))
                reachedAt = Environment.TickCount64;
            if (nextAt == 0 && lines.Any(l => l.Contains("on to waypoint 7", StringComparison.Ordinal)))
                nextAt = Environment.TickCount64;
            return nextAt > 0;
        });

        string log = string.Join("\n", run.Lines);
        Assert.True(reachedAt > 0 && nextAt > 0, log);
        Assert.InRange(nextAt - reachedAt, 800, 2500);
        Assert.Equal(0, input.Clicks); // standing on the waypoint: no walk click before moving on
        Assert.True(Tabs(input) >= 1, "expected Tab during the pause:\n" + string.Join("\n", input.Calls));
    }

    [Fact]
    public async Task JasoTargeted_StopsWalking_Attacks_ThenLoots_ThenWalksOn()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: i => Tabs(i) >= 3 && SkillTaps(i) < 4 ? "[S]Jaso Practitioner" : null);
        settings.Blossom.LootMs = 1000;
        settings.Blossom.LootEveryMs = 200;
        settings.Blossom.LootDelayMs = 0;
        settings.Blossom.TargetGoneMs = 500;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (p, lines) => p.Phase == PilotPhase.BlossomWalk && lines.Any(l => l.StartsWith("Looted", StringComparison.Ordinal)), 20000);

        string log = string.Join("\n", run.Lines);
        Assert.Contains(run.Lines, l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("down (kill 1)", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);

        int firstSkill = input.Calls.IndexOf("KeyTap 1");
        int lastLoot = input.Calls.LastIndexOf("KeyTap `");
        Assert.True(firstSkill >= 0 && lastLoot > firstSkill, log + "\n" + string.Join("\n", input.Calls));
        Assert.True(input.Calls.Count(c => c == "KeyTap `") >= 4, "loot presses:\n" + string.Join("\n", input.Calls));
        Assert.DoesNotContain(input.Calls.Skip(firstSkill).Take(lastLoot - firstSkill), c => c.StartsWith("Click", StringComparison.Ordinal));
        Assert.DoesNotContain(input.Calls.Skip(input.Calls.IndexOf("KeyTap `")).Take(lastLoot - input.Calls.IndexOf("KeyTap `")),
            c => c == "KeyTap Tab"); // no Tab while looting
    }

    [Fact]
    public async Task OtherTarget_IsNotAttacked()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => "Forest Bandit");
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 3000);

        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);
        Assert.DoesNotContain("KeyTap 1", input.Calls);
    }

    [Fact]
    public async Task Turn_NoPair_GivesUpAndWalks()
    {
        var (settings, input, ocr) = Harness(() => "19148 , 19062");
        settings.Blossom.TurnTaps = 3;
        settings.Blossom.TurnMaxTaps = 5;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomTurn, (p, _) => p.Phase == PilotPhase.BlossomWalk);

        string log = string.Join("\n", run.Lines);
        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);
        Assert.Equal(5, input.Calls.Count(c => c == "KeyDown D"));
        Assert.Contains(run.Lines, l => l.Contains("not lined up after 3 taps", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("no pillars", StringComparison.Ordinal));
        Assert.DoesNotContain("KeyTap Tab", input.Calls); // no Tab while turning
    }

    private static int Tabs(FakeGameInput input) => input.Calls.Count(c => c == "KeyTap Tab");

    private static int SkillTaps(FakeGameInput input) => input.Calls.Count(c => c == "KeyTap 1");

    private static (AppSettings Settings, FakeGameInput Input, FakeOcrEngine Ocr) Harness(Func<string> coords, Func<FakeGameInput, string?>? target = null)
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoBlossom;
        settings.Lod.BuffKey = string.Empty;
        settings.Lod.HoldSkill = false;

        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        {
            var words = new List<OcrWord>
            {
                // minimap title and coordinates (Regions.Minimap = right 40 %, top 42 %)
                FakeOcrEngine.Line("[Blossom Garden]", 1000, 10),
                FakeOcrEngine.Line(coords(), 1100, 200, 120),
            };
            if (target?.Invoke(input) is { } name)
                words.Add(FakeOcrEngine.Line(name, 620, 50)); // the target bar, top centre
            return words;
        };
        return (settings, input, ocr);
    }

    private static async Task<(PilotPhase Phase, List<string> Lines)> RunAsync(AppSettings settings, FakeGameInput input,
        FakeOcrEngine ocr, PilotPhase startAt, Func<AutomationPilot, IReadOnlyList<string>, bool> done, int timeoutMs = 15000)
    {
        using var frame = Blank.Bgra(Height, Width);
        return await PilotRun.RunAsync(settings, input, ocr, frame, startAt, done, timeoutMs);
    }
}
