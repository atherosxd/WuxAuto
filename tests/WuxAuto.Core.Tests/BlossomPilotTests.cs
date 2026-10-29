using Emgu.CV;
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

    [Fact]
    public async Task Rebuff_Due_RunsAfterLoot_NotMidFight()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: i => Tabs(i) >= 1 && SkillTaps(i) < 4 ? "[S]Jaso Practitioner" : null,
            autoBuffScroll: true);
        settings.Lod.BuffKey = "O";
        settings.Blossom.LootMs = 1000;
        settings.Blossom.LootDelayMs = 0;
        settings.Blossom.TargetGoneMs = 500;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Buffed with 'O'", StringComparison.Ordinal)), 20000);

        string calls = string.Join("\n", input.Calls);
        int buff = input.Calls.IndexOf("KeyTap O");
        Assert.True(buff >= 0, string.Join("\n", run.Lines) + "\n" + calls);
        Assert.True(buff > input.Calls.LastIndexOf("KeyTap `"), "the buff must follow the loot:\n" + calls);
        Assert.True(buff > input.Calls.LastIndexOf("KeyTap 1"), "no buff mid-fight:\n" + calls);
        Assert.Contains("KeyTap `", input.Calls);
    }

    [Fact]
    public async Task Arrival_BuffsThenTurns()
    {
        var (settings, input, ocr) = Harness(() => "19148 , 19062", autoBuffScroll: true);
        settings.Lod.BuffKey = "O";

        var run = await RunAsync(settings, input, ocr, PilotPhase.ZoomOut, (_, _) => input.Calls.Contains("KeyDown D"), 20000);

        string calls = string.Join("\n", input.Calls);
        int buff = input.Calls.IndexOf("KeyTap O");
        Assert.True(buff >= 0 && buff < input.Calls.IndexOf("KeyDown D"), string.Join("\n", run.Lines) + "\n" + calls);
    }

    // ---- final review fixes

    [Fact]
    public async Task FightGiveUp_WalksOnInsteadOfReEngaging()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => "[S]Jaso Practitioner"); // never goes away
        settings.Blossom.FightMaxMs = 1000;
        long gaveUpAt = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            if (gaveUpAt == 0 && lines.Any(l => l.Contains("giving it up", StringComparison.Ordinal)))
                gaveUpAt = Environment.TickCount64;
            return gaveUpAt > 0 && Environment.TickCount64 - gaveUpAt > 3000;
        }, 20000);

        string log = string.Join("\n", run.Lines);
        Assert.True(gaveUpAt > 0, log);
        Assert.Equal(1, run.Lines.Count(l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal)));
        Assert.True(input.Calls.Count(c => c.StartsWith("Click", StringComparison.Ordinal)) >= 2,
            "expected walk clicks after giving up:\n" + log + "\n" + string.Join("\n", input.Calls));
    }

    [Fact]
    public async Task Heading_WrongFirstSample_IsReLearnt()
    {
        // The view really looks north (screen-up = world -y), but the first straight-up step is pushed east by a wall:
        // the heading learnt from it is 90 degrees off, and the walk must correct it from the steps that follow.
        int x = 19136, y = 19062, seen = 0;
        FakeGameInput? sent = null;
        var (settings, input, ocr) = Harness(() =>
        {
            var clicks = sent!.Calls.Where(c => c.StartsWith("Click ", StringComparison.Ordinal)).ToList();
            for (; seen < clicks.Count; seen++)
            {
                var p = clicks[seen]["Click ".Length..].Split(',').Select(int.Parse).ToArray();
                double sx = p[0] - Width / 2.0, sy = p[1] - Height / 2.0;
                if (seen == 0) { x += 150; continue; }  // the wall push
                x += (int)Math.Round(sx * 0.75);        // a click walks about 3/4 of its length in world units
                y += (int)Math.Round(sy * 0.75);
            }

            return $"{x} , {y}";
        });
        sent = input;
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Waypoint 1 reached", StringComparison.Ordinal)), 20000);

        string log = string.Join("\n", run.Lines);
        Assert.True(run.Lines.Any(l => l.Contains("re-learnt", StringComparison.Ordinal)), log);
        Assert.True(run.Lines.Any(l => l.StartsWith("Waypoint 1 reached", StringComparison.Ordinal)), log);
    }

    [Fact]
    public async Task StartInside_TurnsTheAutohuntOff()
    {
        var (settings, input, ocr) = Harness(() => "19148 , 19062", log: i => i.Calls.Contains("KeyTap J")
            ? ["Auto hunt started.", "Auto hunt ended."]
            : ["Auto hunt started."]);

        var run = await RunAsync(settings, input, ocr, PilotPhase.CheckZone, (p, _) => p.Phase is PilotPhase.WarBuff or PilotPhase.BlossomTurn);

        string log = string.Join("\n", run.Lines);
        Assert.Contains("KeyTap J", input.Calls);
        Assert.True(run.Phase is PilotPhase.WarBuff or PilotPhase.BlossomTurn, log);
    }

    [Fact]
    public async Task Buff_WithTheMinimapTitleUnread_StillFinishes()
    {
        var (settings, input, ocr) = Harness(() => "19148 , 19062", title: false);
        settings.Lod.BuffKey = "O";

        var run = await RunAsync(settings, input, ocr, PilotPhase.WarBuff,
            (_, lines) => lines.Any(l => l.StartsWith("Buffed with 'O'", StringComparison.Ordinal)), 10000);

        Assert.Contains(run.Lines, l => l.StartsWith("Buffed with 'O'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_NamesTheBlossomPhase()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        string text = "";

        await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (p, _) =>
        {
            text = p.PhaseText;
            return p.Phase == PilotPhase.BlossomWalk && input.Clicks > 0;
        });

        Assert.NotEqual("idle", text);
    }

    [Fact]
    public async Task Stuck_SkipsAfterFiveStillSteps_AndSnapshotsAtMostThree()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000"); // never moves
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;
        int skips = 0, shots = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            skips = lines.Count(l => l.StartsWith("Not moving for", StringComparison.Ordinal));
            if (Directory.Exists(AppPaths.DebugFolder))
                shots = Directory.GetFiles(AppPaths.DebugFolder, "*blossom_stuck*").Length;
            return skips >= 5;
        }, 30000);

        Assert.True(skips >= 5, string.Join("\n", run.Lines));
        Assert.True(run.Lines.Any(l => l.StartsWith("Not moving for 5 steps", StringComparison.Ordinal)), string.Join("\n", run.Lines));
        Assert.InRange(shots, 1, 3);
    }

    [Fact]
    public async Task Pause_EndsOnTime_EvenWithTheCoordinatesUnread()
    {
        bool reached = false;
        var (settings, input, ocr) = Harness(() => reached ? "" : "13947 , 14697"); // on waypoint 6, then unreadable
        settings.Blossom.WaypointWaitMs = 1000;
        long reachedAt = 0, nextAt = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            if (reachedAt == 0 && lines.Any(l => l.StartsWith("Waypoint 6 reached", StringComparison.Ordinal)))
            {
                reachedAt = Environment.TickCount64;
                reached = true;
            }

            if (nextAt == 0 && lines.Any(l => l.Contains("on to waypoint 7", StringComparison.Ordinal)))
                nextAt = Environment.TickCount64;
            return nextAt > 0;
        });

        Assert.True(reachedAt > 0 && nextAt > 0, string.Join("\n", run.Lines));
        Assert.InRange(nextAt - reachedAt, 800, 2500);
    }

    // ---- live test 2026-10-29: Blossom Garden's bright yellow minimap - the whole-band OCR read no title at all

    [Fact]
    public async Task Arrival_TitleOnlyReadableInASmallStrip_StillZoomsOut()
    {
        var (settings, input, ocr) = Harness(() => "19148 , 19062");
        var blind = new BandBlindOcr(ocr, maxHeight: Height / 5);

        using var frame = Blank.Bgra(Height, Width);
        var run = await PilotRun.RunAsync(settings, input, blind, frame, PilotPhase.ZoomOut,
            (_, lines) => lines.Any(l => l.StartsWith("Zoomed out", StringComparison.Ordinal)), 8000);

        Assert.True(run.Lines.Any(l => l.StartsWith("Zoomed out", StringComparison.Ordinal)), string.Join("\n", run.Lines));
    }

    // Live 2026-10-29: stuck against the side of the bridge (camera zoomed in there) - every waypoint was skipped in turn.

    [Fact]
    public async Task Blocked_ZoomsOutAndSidesteps_BeforeSkipping()
    {
        int y = 15700, clicks = 0;
        FakeGameInput? sent = null;
        var (settings, input, ocr) = Harness(() =>
        {
            int now = sent!.Clicks;
            if (clicks == 0 && now > 0)
                y -= 150;      // the first step goes north: the heading is learnt
            clicks = now;      // then it never moves again
            return $"13947 , {y}";
        });
        sent = input;
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Not moving for", StringComparison.Ordinal)), 20000);

        string log = string.Join("\n", run.Lines);
        int skip = run.Lines.FindIndex(l => l.StartsWith("Not moving for", StringComparison.Ordinal));
        int right = run.Lines.FindIndex(l => l.Contains("sidestepping right", StringComparison.Ordinal));
        int left = run.Lines.FindIndex(l => l.Contains("sidestepping left", StringComparison.Ordinal));
        int back = run.Lines.FindIndex(l => l.Contains("stepping back", StringComparison.Ordinal));
        Assert.True(skip > 0 && right >= 0 && right < left && left < back && back < skip, log);
        Assert.Contains(input.Calls, c => c.StartsWith("CtrlWheel", StringComparison.Ordinal)); // zoomed out again
    }

    [Fact]
    public async Task Waypoint_TooManyClicks_IsSkipped()
    {
        int n = 0;
        var (settings, input, ocr) = Harness(() => (n++ / 2) % 2 == 0 ? "16500 , 17000" : "16600 , 17000"); // moves, never arrives
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("taking too long", StringComparison.Ordinal)), 25000);

        Assert.True(run.Lines.Any(l => l.Contains("taking too long", StringComparison.Ordinal)), string.Join("\n", run.Lines));
    }

    [Fact]
    public async Task GameBehindAnotherWindow_WalkWaits_NothingCountsAsStuck()
    {
        // Live 2026-10-29: the game lost focus mid-walk - its clicks were refused, and the still character must not read
        // as "blocked" (that sidesteps and skips waypoints for nothing).
        var (settings, input, ocr) = Harness(() => "16500 , 17000");
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;
        input.InFront = false;
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 4000);

        string log = string.Join("\n", run.Lines);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Blocked at", StringComparison.Ordinal) || l.StartsWith("Not moving", StringComparison.Ordinal));
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("Click", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);
    }

    [Fact]
    public async Task Walk_ClicksContinuously_ReadsCoordinatesLessOften()
    {
        // User, 2026-10-29: "try to continuously click to run instead of staggered and not every click is checked".
        int reads = 0;
        var (settings, input, ocr) = Harness(() => $"{16500 - 40 * reads++} , 17000"); // keeps moving west, far from any waypoint
        long start = 0;
        var clickTimes = new List<long>();

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) =>
        {
            if (input.Clicks > clickTimes.Count)
                clickTimes.Add(Environment.TickCount64);
            if (start == 0 && input.Clicks > 0)
                start = Environment.TickCount64;
            return start > 0 && Environment.TickCount64 - start > 3000;
        });

        string log = string.Join("\n", run.Lines);
        Assert.True(input.Clicks >= 7, $"{input.Clicks} clicks in 3 s:\n{log}");
        var gaps = clickTimes.Zip(clickTimes.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps.Count(g => g > 600) <= 1, "gaps: " + string.Join(", ", gaps));
    }

    [Fact]
    public async Task Waypoint1_TurnsTheCameraLeft4_ThenRelearnsTheHeading()
    {
        var (settings, input, ocr) = Harness(() => "19136 , 18463"); // on waypoint 1
        settings.Blossom.WaypointWaitMs = 500;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => input.Clicks > 0);

        string log = string.Join("\n", run.Lines);
        Assert.Equal(4, input.Calls.Count(c => c == "KeyDown A"));
        Assert.DoesNotContain("KeyDown D", input.Calls);
        int lastTurn = input.Calls.LastIndexOf("KeyUp A");
        int click = input.Calls.FindIndex(c => c.StartsWith("Click ", StringComparison.Ordinal));
        Assert.True(lastTurn >= 0 && click > lastTurn, log);
        Assert.StartsWith($"Click {Width / 2},", input.Calls[click]); // heading unknown again: straight up
        Assert.Contains(run.Lines, l => l.Contains("turned the camera", StringComparison.Ordinal));
    }

    /// <summary>OCR that misses the minimap title in a tall read (the whole minimap band) and reads it in a small one.</summary>
    private sealed class BandBlindOcr(FakeOcrEngine inner, int maxHeight) : IOcrEngine
    {
        public string Status => inner.Status;
        public bool IsAvailable => true;
        public double LastDurationMs => 0;
        public bool EnsureInitialized() => true;

        public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(Mat bgra, PixelRect area, double upscale)
        {
            var words = await inner.RecognizeAsync(bgra, area, upscale);
            return area.Height <= maxHeight ? words : words.Where(w => !w.LineText.StartsWith('[')).ToList();
        }
    }

    private static int Tabs(FakeGameInput input) => input.Calls.Count(c => c == "KeyTap Tab");

    private static int SkillTaps(FakeGameInput input) => input.Calls.Count(c => c == "KeyTap 1");

    private static (AppSettings Settings, FakeGameInput Input, FakeOcrEngine Ocr) Harness(Func<string> coords, Func<FakeGameInput, string?>? target = null,
        bool autoBuffScroll = true, bool title = true, Func<FakeGameInput, string[]>? log = null)
    {
        // The scroll in the item list keeps the buff round short: a Start buffs first (the timer starts at 0), and with no
        // buff key and no templates the round clicks nothing and goes back to the walk.
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
                // the minimap coordinates (Regions.Minimap = right 40 %, top 42 %)
                FakeOcrEngine.Line(coords(), 1100, 200, 120),
            };
            if (title)
                words.Add(FakeOcrEngine.Line("[Blossom Garden]", 1130, 55)); // where the live 2048x1152 frame has it, scaled
            if (log?.Invoke(input) is { } lines)
            {
                // the combat log, left side (Regions.AttackMessage)
                for (int i = 0; i < lines.Length; i++)
                    words.Add(FakeOcrEngine.Line(lines[i], 20, 300 + i * 20));
            }
            if (target?.Invoke(input) is { } name)
                words.Add(FakeOcrEngine.Line(name, 620, 50)); // the target bar, top centre
            if (autoBuffScroll)
                words.Add(FakeOcrEngine.Line("[Auto buff scroll] 01.01.2027", 1000, 700)); // the item list, bottom right
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
