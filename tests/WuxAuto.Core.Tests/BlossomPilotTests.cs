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
        var (settings, input, ocr) = Harness(() => "13964 , 14686"); // on waypoint 6
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
        settings.Blossom.LootWhileFighting = false;   // the loot phase after the kill
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
    public async Task StartInside_SkipsThePillars_AndWalks()
    {
        // User, 2026-10-30: "it is possible to start it already in the map and no need to look for the pillar" - away from
        // the arrival spot the pillars are not in view anyway (the turn took 50 taps, ~1 min, for nothing).
        var (settings, input, ocr) = Harness(() => "15705 , 18357");

        var run = await RunAsync(settings, input, ocr, PilotPhase.CheckZone,
            (_, lines) => lines.Any(l => l.StartsWith("Walking the route from", StringComparison.Ordinal)), 15000);

        string log = string.Join("\n", run.Lines);
        Assert.True(run.Lines.Any(l => l.StartsWith("Walking the route from", StringComparison.Ordinal)), log);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Turning (", StringComparison.Ordinal));
        Assert.DoesNotContain("KeyDown D", input.Calls);
    }

    [Theory]
    [InlineData("19150 , 19060", true)]    // back at the arrival spot: a real death - line up on the pillars
    [InlineData("14913 , 18473", false)]   // live 10:20: alive, the target lost after a chase - no pillars here
    public async Task AfterADeath_ThePillarTurn_OnlyAtTheArrivalSpot(string at, bool turns)
    {
        var (settings, input, ocr) = Harness(() => at);

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomRespawn,
            (p, _) => p.Phase is PilotPhase.BlossomTurn or PilotPhase.BlossomWalk, 15000);

        Assert.Equal(turns ? PilotPhase.BlossomTurn : PilotPhase.BlossomWalk, run.Phase);
    }

    [Fact]
    public async Task Turn_NoPair_GivesUpAfterTheTimeLimit()
    {
        // User, 2026-10-30: "reduce the turn to pillars to 15 turns or 10s".
        Assert.Equal((15, 10000), (new AppSettings().Blossom.TurnMaxTaps, new AppSettings().Blossom.TurnMaxMs));
        var (settings, input, ocr) = Harness(() => "19148 , 19062");
        settings.Blossom.TurnMaxMs = 2000;
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomTurn, (p, _) => p.Phase == PilotPhase.BlossomWalk, 15000);

        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);
        Assert.True(Environment.TickCount64 - start < 5000, $"took {Environment.TickCount64 - start} ms");
        Assert.InRange(input.Calls.Count(c => c == "KeyDown D"), 1, 14);
        Assert.Contains(run.Lines, l => l.Contains("no pillars", StringComparison.Ordinal));
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
    public async Task Stuck_SnapshotsAtMostThree()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000"); // never moves
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;
        int notes = 0, shots = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            notes = lines.Count(l => l.StartsWith("Not moving for", StringComparison.Ordinal));
            if (Directory.Exists(AppPaths.DebugFolder))
                shots = Directory.GetFiles(AppPaths.DebugFolder, "*blossom_stuck*").Length;
            return notes >= 5;
        }, 40000);

        Assert.True(notes >= 5, string.Join("\n", run.Lines));
        Assert.InRange(shots, 1, 3);
    }

    [Fact]
    public async Task Stuck_TriesEveryWay_ZoomingOutEachRound_UntilItMoves()
    {
        // User, 2026-10-30: "If the character didn't move for 5s, try to zoom out and move in every direction until it moves" -
        // it used to try right, left and back, then skip the waypoint.
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
            (_, lines) => lines.Count(l => l.StartsWith("Blocked at", StringComparison.Ordinal) && l.Contains("stepping", StringComparison.Ordinal)) >= 9, 25000);

        string log = string.Join("\n", run.Lines);
        foreach (string way in new[] { "stepping right", "stepping left", "stepping back,", "stepping back-right", "stepping back-left", "stepping ahead-right", "stepping ahead-left" })
            Assert.True(run.Lines.Any(l => l.Contains(way.TrimEnd(','), StringComparison.Ordinal) && (way.EndsWith(',') ? l.TrimEnd('.').EndsWith("stepping back", StringComparison.Ordinal) : true)), $"no '{way}':\n{log}");
        Assert.DoesNotContain(run.Lines, l => l.Contains(" skipped", StringComparison.Ordinal));
        Assert.True(input.Calls.Count(c => c.StartsWith("CtrlWheel", StringComparison.Ordinal)) >= 2, "expected a zoom-out each round:\n" + log);
    }

    [Fact]
    public async Task Pause_EndsOnTime_EvenWithTheCoordinatesUnread()
    {
        bool reached = false;
        var (settings, input, ocr) = Harness(() => reached ? "" : "13964 , 14686"); // on waypoint 6, then unreadable
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
    public async Task Blocked_ZoomsOutAndSidesteps_RightThenLeftThenBack()
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
            (_, lines) => lines.Any(l => l.Contains("stepping back-right", StringComparison.Ordinal)), 20000);

        string log = string.Join("\n", run.Lines);
        int zoom = run.Lines.FindIndex(l => l.Contains("zoomed out again", StringComparison.Ordinal));
        int right = run.Lines.FindIndex(l => l.Contains("stepping right", StringComparison.Ordinal));
        int left = run.Lines.FindIndex(l => l.Contains("stepping left", StringComparison.Ordinal));
        int back = run.Lines.FindIndex(l => l.TrimEnd('.').EndsWith("stepping back", StringComparison.Ordinal));
        Assert.True(zoom >= 0 && zoom < right && right < left && left < back, log);
        Assert.Contains(input.Calls, c => c.StartsWith("CtrlWheel", StringComparison.Ordinal)); // zoomed out again
    }

    [Fact]
    public async Task AfterAFight_FarFromTheWaypoint_NextToAnother_WalksOnFromThatOne()
    {
        // Live 2026-10-30 11:20: a chase (TAB target "Nearest monster") ended 111 units from waypoint 20, and the walk then
        // tried to reach waypoint 7, 2750 units south, in a straight line through the terrain - blocked for minutes.
        FakeGameInput? sent = null;
        IReadOnlyList<string>? seen = null;
        bool Paused() => seen?.Any(l => l.StartsWith("Waypoint 1 reached", StringComparison.Ordinal)) == true;
        var (settings, input, ocr) = Harness(() => SkillTaps(sent!) == 0 ? "10000 , 9980" : "14050 , 9000",
            target: i => Paused() && SkillTaps(i) < 4 ? "[S]Jaso Practitioner" : null);   // met at waypoint 1, chased far
        sent = input;
        settings.Blossom.Waypoints = ["10000,10000", "10000,9000", "14000,9000"];
        settings.Blossom.LoopFrom = 1;
        settings.Blossom.WaypointWaitMs = 5000;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            seen = lines;
            return lines.Any(l => l.Contains("Off the route", StringComparison.Ordinal)) || lines.Count(l => l.Contains("Blocked", StringComparison.Ordinal)) > 2;
        }, 20000);

        string log = string.Join("\n", run.Lines);
        Assert.True(run.Lines.Any(l => l.Contains("Off the route", StringComparison.Ordinal) && l.Contains("waypoint 3", StringComparison.Ordinal)), log);
    }

    [Fact]
    public async Task DenseRoute_RunsPastNearWaypoints_NoPauseAtEach()
    {
        // User, 2026-10-30: "dont walk, run far away instead of staggered". A recorded route has a waypoint every 120 units:
        // homing in on each and pausing there made the character stop and go all the way.
        int reads = 0;
        var (settings, input, ocr) = Harness(() => $"10000 , {10000 - 40 * (reads++ / 4)}");   // running north
        settings.Blossom.Waypoints = Enumerable.Range(0, 30).Select(i => $"10000,{10000 - 120 * i}").ToList();
        settings.Blossom.WaypointTurns = [];   // as a recorded route without A / D taps
        settings.Blossom.RunAheadDistance = 600;
        settings.Blossom.LoopFrom = 1;
        settings.Blossom.WalkReadEveryMs = 200;
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 5000, 10000);

        string log = string.Join("\n", run.Lines);
        Assert.DoesNotContain(run.Lines, l => l.Contains(" reached at ", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);
        Assert.True(input.Clicks > 5, log);
    }

    [Fact]
    public async Task Blocked_ShortSidestepMoves_DoNotRestartTheWays()
    {
        // Live 2026-10-30 11:20-11:24: each sidestep moved the character 20+ units, which reset the still-step count, so it
        // stepped right, left, right, left ... and never tried back or the diagonals.
        int y = 15700, clicks = 0;
        FakeGameInput? sent = null;
        IReadOnlyList<string>? log = null;
        var (settings, input, ocr) = Harness(() =>
        {
            int now = sent!.Clicks;
            if (clicks == 0 && now > 0)
                y -= 150;      // the first step goes north: the heading is learnt
            clicks = now;
            int steps = log?.Count(l => l.StartsWith("Blocked at", StringComparison.Ordinal) && l.Contains("stepping", StringComparison.Ordinal)) ?? 0;
            return $"{13947 + steps % 2 * 40} , {y}";   // each sidestep shuffles it 40 units, never closer
        });
        sent = input;
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            log = lines;
            return lines.Count(l => l.StartsWith("Blocked at", StringComparison.Ordinal) && l.Contains("stepping", StringComparison.Ordinal)) >= 4;
        }, 30000);

        var ways = run.Lines.Where(l => l.StartsWith("Blocked at", StringComparison.Ordinal) && l.Contains("stepping", StringComparison.Ordinal))
            .Select(l => l[(l.LastIndexOf("stepping ", StringComparison.Ordinal) + 9)..].TrimEnd('.')).Take(4).ToList();
        Assert.Equal(["right", "left", "back", "back-right"], ways);
    }

    [Theory]
    [InlineData("615378/750000", false)]   // another player is on it
    [InlineData("750000/750000", true)]
    public async Task OnlyAFullHealthTarget_IsAttacked(string hp, bool attacks)
    {
        // User, 2026-10-30: "Only Attack monster if full health".
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => "[S]Jaso Practitioner", hp: _ => hp);
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal)) || Environment.TickCount64 - start > 4000, 8000);

        string log = string.Join("\n", run.Lines);
        Assert.True(attacks == run.Lines.Any(l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal)), log);
        if (!attacks)
            Assert.True(run.Lines.Any(l => l.Contains("not at full health", StringComparison.Ordinal)), log);
    }

    [Theory]
    [InlineData("(Whisper) [AFK Checker] : Start 1/5", true)]
    [InlineData("(Whisper) [AFK Checker] : Reset 0/5", false)]
    public async Task AfkChecker_OneOrMore_StepsALittle(string line, bool steps)
    {
        // User, 2026-10-30: "add a AFK Checker, if 1+/5, try to move a little bit to other directions".
        var (settings, input, ocr) = Harness(() => "15000 , 15000", chat: _ => [line]);
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("AFK Checker", StringComparison.Ordinal)) || Environment.TickCount64 - start > 3000, 8000);

        string log = string.Join("\n", run.Lines);
        Assert.True(steps == run.Lines.Any(l => l.StartsWith("AFK Checker at 1/5", StringComparison.Ordinal)), log);
        Assert.Equal(steps, input.Calls.Any(c => c.StartsWith("CtrlClick", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LootWhileFighting_LootsDuringTheFight_AndWalksOnRightAfterTheKill()
    {
        // User, 2026-10-30: "When attacking a monster, try looting also, so that when killed after 1s, the pilot can proceed to
        // other monster/waypoint."
        Assert.True(new AppSettings().Blossom.LootWhileFighting);
        var (settings, input, ocr) = Harness(() => "15000 , 15000",
            target: i => Tabs(i) >= 1 && SkillTaps(i) < 4 ? "[S]Jaso Practitioner" : null);
        settings.Blossom.LootWhileFighting = true;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (p, lines) => p.Phase == PilotPhase.BlossomWalk && lines.Any(l => l.Contains("down (kill 1)", StringComparison.Ordinal)), 15000);

        string log = string.Join("\n", run.Lines);
        Assert.True(run.Lines.Any(l => l.Contains("down (kill 1)", StringComparison.Ordinal)), log);
        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Looted", StringComparison.Ordinal));   // no loot phase after it
        int firstSkill = input.Calls.IndexOf("KeyTap 1"), lastSkill = input.Calls.LastIndexOf("KeyTap 1");
        Assert.Contains(input.Calls.Skip(firstSkill).Take(lastSkill - firstSkill), c => c == "KeyTap `");   // looted while fighting
    }

    [Fact]
    public async Task SlidingAlongAWall_LongAfterTheHeadingWasLearnt_DoesNotRelearnIt()
    {
        // Live run 10: blocked by the bridge, the moves slid along the railing, three of them read as "steps that went
        // elsewhere" and the heading was re-learnt as +90 - the walk then went east, away from the route. Re-learning is for a
        // bad first sample right after the heading was set, not for a wall met many steps later.
        int x = 15000, y = 18000, seen = 0;   // the view looks north; at y <= 15700 every click slides east along a wall
        FakeGameInput? sent = null;
        var (settings, input, ocr) = Harness(() =>
        {
            var clicks = sent!.Calls.Where(c => c.StartsWith("Click ", StringComparison.Ordinal)).ToList();
            for (; seen < clicks.Count; seen++)
            {
                var p = clicks[seen]["Click ".Length..].Split(',').Select(int.Parse).ToArray();
                double dx = (p[0] - Width / 2.0) * 0.5, dy = (p[1] - Height / 2.0) * 0.5;
                if (y <= 15700)
                {
                    x += (int)Math.Round(Math.Sqrt(dx * dx + dy * dy));
                    continue;
                }

                x += (int)Math.Round(dx);
                y += (int)Math.Round(dy);
            }

            return $"{x} , {y}";
        });
        sent = input;
        settings.Blossom.Waypoints = ["15000,18000", "15000,12000", "15000,10000"];
        settings.Blossom.LoopFrom = 1;
        settings.Blossom.WaypointTurns = [];
        settings.Blossom.WaypointWaitMs = 0;
        settings.Blossom.WalkReadEveryMs = 200;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => y <= 15700 && x > 16500, 20000);

        string log = string.Join("\n", run.Lines);
        Assert.True(y <= 15700, "never reached the wall:\n" + log);
        Assert.DoesNotContain(run.Lines, l => l.Contains("re-learnt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackAndForth_WalksTheRouteBack_AndUndoesItsCameraTurns()
    {
        // User, 2026-10-30: "1 - 83 then 83, 82, 81 to 1 then loop". The character "arrives" wherever the walk heads next.
        // A turn is undone at the waypoint it was made at, so each stretch keeps the camera it had on the way out, and a
        // turnaround (here waypoint 3, whose own turn is never made going forward) taps nothing.
        List<string>? log = null;
        int callsAtWaypoint1 = -1;
        string Goal()
        {
            var last = log?.LastOrDefault(l => l.Contains("on to waypoint", StringComparison.Ordinal) || l.StartsWith("Walking the route from", StringComparison.Ordinal));
            if (last == null)
                return "10000 , 10000";
            int open = last.IndexOf('(') + 1, close = last.IndexOf(')', open);
            var xy = last[open..close].Split(',');
            return $"{xy[0]} , {xy[1]}";
        }

        var (settings, input, ocr) = Harness(Goal);
        settings.Blossom.Waypoints = ["10000,10000", "10000,9000", "10000,8000"];
        settings.Blossom.LoopFrom = 1;
        settings.Blossom.BackAndForth = true;
        settings.Blossom.WaypointTurns = ["2:1D", "3:2A"];
        settings.Blossom.WaypointWaitMs = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            log = (List<string>)lines;
            int legs = lines.Count(l => l.Contains("on to waypoint", StringComparison.Ordinal));
            if (legs >= 4 && callsAtWaypoint1 < 0)
                callsAtWaypoint1 = input.Calls.Count;   // just left waypoint 2 on the way back, heading for waypoint 1
            return legs >= 5;
        }, 20000);

        var order = run.Lines.Where(l => l.Contains("on to waypoint", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf("on to waypoint ", StringComparison.Ordinal) + 15)..].Split(' ')[0]).ToList();
        Assert.Equal(["2", "3", "2", "1", "2"], order.Take(5));
        string downs = string.Join("\n", input.Calls.Where(c => c.Contains("Down")));
        int right = input.Calls.IndexOf("KeyDown D");
        var lefts = input.Calls.Select((c, i) => (c, i)).Where(p => p.c == "KeyDown A").Select(p => p.i).ToList();
        Assert.True(right >= 0 && right < callsAtWaypoint1, "expected D leaving waypoint 2 forward:\n" + downs);
        Assert.True(lefts.Count == 1 && lefts[0] >= callsAtWaypoint1,
            $"expected one A, leaving waypoint 2 on the way back (calls from {callsAtWaypoint1}):\n" + downs);
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
    public async Task Waypoint1_TurnsTheCameraLeft5_ThenRelearnsTheHeading()
    {
        var (settings, input, ocr) = Harness(() => "19107 , 18424"); // on waypoint 1
        settings.Blossom.WaypointWaitMs = 500;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => input.Clicks > 0);

        string log = string.Join("\n", run.Lines);
        Assert.Equal(5, input.Calls.Count(c => c == "KeyDown A"));
        Assert.DoesNotContain("KeyDown D", input.Calls);
        int lastTurn = input.Calls.LastIndexOf("KeyUp A");
        int click = input.Calls.FindIndex(c => c.StartsWith("Click ", StringComparison.Ordinal));
        Assert.True(lastTurn >= 0 && click > lastTurn, log);
        Assert.StartsWith($"Click {Width / 2},", input.Calls[click]); // heading unknown again: straight up
        Assert.Contains(run.Lines, l => l.Contains("turned the camera", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongLeg_StillGettingCloser_IsNotSkipped()
    {
        // Live 2026-10-29: the 3200-unit leg to waypoint 2 was skipped after 40 steps while it was still closing in.
        int reads = 0;
        var (settings, input, ocr) = Harness(() => $"{17400 - 25 * reads++} , 18388"); // slowly west, nearest waypoint 2
        settings.Blossom.WalkReadEveryMs = 100;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Waypoint 2 reached", StringComparison.Ordinal)), 25000);

        string log = string.Join("\n", run.Lines);
        Assert.Contains(run.Lines, l => l.StartsWith("Walking the route from waypoint 2", StringComparison.Ordinal));
        Assert.True(run.Lines.Any(l => l.StartsWith("Waypoint 2 reached", StringComparison.Ordinal)), log);
        Assert.DoesNotContain(run.Lines, l => l.Contains("taking too long", StringComparison.Ordinal));
    }

    // Live run 5 (user): "when going to waypoint 1, it stopped more to its north and did not correct it ... same for the
    // others, that's why it is not aligned" - reached 61 units off at a 100-unit circle.

    [Fact]
    public async Task Waypoint_IsReachedPrecisely()
    {
        int x = 19107, y = 18424 + 700, seen = 0;
        FakeGameInput? sent = null;
        var (settings, input, ocr) = Harness(() =>
        {
            var clicks = sent!.Calls.Where(c => c.StartsWith("Click ", StringComparison.Ordinal)).ToList();
            for (; seen < clicks.Count; seen++)
            {
                var p = clicks[seen]["Click ".Length..].Split(',').Select(int.Parse).ToArray();
                x += (int)Math.Round((p[0] - Width / 2.0) * 0.5);   // the view looks north; a click walks half its length
                y += (int)Math.Round((p[1] - Height / 2.0) * 0.5);
            }

            return $"{x} , {y}";
        });
        sent = input;
        settings.Blossom.WalkReadEveryMs = 900;  // the default: between two reads the clicks run on blind
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Waypoint 1 reached", StringComparison.Ordinal)), 20000);

        string line = run.Lines.FirstOrDefault(l => l.StartsWith("Waypoint 1 reached at ", StringComparison.Ordinal)) ?? string.Join("\n", run.Lines);
        var at = line["Waypoint 1 reached at ".Length..].Split(' ')[0..2].Select(s => int.Parse(s.Trim(','))).ToArray();
        double off = Math.Sqrt(Math.Pow(at[0] - 19107, 2) + Math.Pow(at[1] - 18424, 2));
        Assert.True(off <= 40, $"reached {off:0} units off: {line}");
    }

    [Fact]
    public async Task OffTheLine_IsPulledBackOntoTheRoadBetweenWaypoints()
    {
        // Live run 7 (user): "around waypoint 4, since it is not aligned by the dirt road, it goes straight to the water beside
        // the bridge" - the walk steered at the next waypoint and never back onto the line between the two (the road).
        int x = 10150, y = 8900, seen = 0;   // 150 east of the line waypoint 1 (10000,10000) -> waypoint 2 (10000,8000)
        double off = -1;
        FakeGameInput? sent = null;
        var (settings, input, ocr) = Harness(() =>
        {
            var clicks = sent!.Calls.Where(c => c.StartsWith("Click ", StringComparison.Ordinal)).ToList();
            for (; seen < clicks.Count; seen++)
            {
                var p = clicks[seen]["Click ".Length..].Split(',').Select(int.Parse).ToArray();
                x += (int)Math.Round((p[0] - Width / 2.0) * 0.3);   // the view looks north
                y += (int)Math.Round((p[1] - Height / 2.0) * 0.3);
            }

            if (off < 0 && y <= 8400)
                off = Math.Abs(x - 10000);
            return $"{x} , {y}";
        });
        sent = input;
        settings.Blossom.Waypoints = ["10000,10000", "10000,8000", "10000,6000"];
        settings.Blossom.LoopFrom = 1;
        settings.Blossom.WaypointTurns = [];
        settings.Blossom.WalkReadEveryMs = 300;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => off >= 0, 20000);

        Assert.True(off >= 0, string.Join("\n", run.Lines));
        Assert.True(off < 40, $"{off:0} off the road half way to waypoint 2");
    }

    [Fact]
    public async Task OneMisreadCoordinate_MidFight_IsNotADeath()
    {
        // Live run 7: "Moved to 12757, 1352 (from 12757,13521) during the fight - killed?" - one read dropped a digit.
        int reads = 0;
        var (settings, input, ocr) = Harness(() => ++reads == 12 ? "12757 , 1352" : "12757 , 13521",
            target: _ => "[S]Jaso Practitioner");
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 6000, 12000);

        Assert.True(reads > 12, $"only {reads} reads");
        Assert.DoesNotContain(run.Lines, l => l.Contains("killed?", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.BlossomAttack, run.Phase);
    }

    [Fact]
    public async Task BigMove_WhileTheTargetIsStillShown_IsAChase_NotADeath()
    {
        // Live 2026-10-30, TAB target "Nearest monster" + the Run skill: the character ran ~500 units to a far Jaso within one
        // coordinate read, alive at full HP and still hitting it - three times taken for a death ("killed? Lining up again").
        int reads = 0;
        var (settings, input, ocr) = Harness(() => ++reads < 6 ? "15000 , 15000" : "15600 , 15000",
            target: _ => "[S]Jaso Practitioner");
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 7000, 12000);

        Assert.True(reads > 8, $"only {reads} reads");
        Assert.DoesNotContain(run.Lines, l => l.Contains("killed?", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.BlossomAttack, run.Phase);
    }

    [Fact]
    public async Task BigMove_WithTheTargetGone_IsADeath()
    {
        // A death puts the character back at the start with no target: still taken for one.
        int reads = 0;
        bool moved = false;
        var (settings, input, ocr) = Harness(() => (moved = ++reads >= 6) ? "19148 , 19062" : "15000 , 15000",
            target: _ => moved ? null : "[S]Jaso Practitioner");
        settings.Blossom.TargetGoneMs = 5000;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("killed?", StringComparison.Ordinal)), 15000);

        Assert.Contains(run.Lines, l => l.Contains("killed?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HoldSkill_KeepsTheSkillKeyDown_WhileFighting()
    {
        // User, 2026-10-30: "spam or hold attack skill, currently it skills then normal attacks".
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => "[S]Jaso Practitioner");
        settings.Blossom.HoldSkill = true;
        settings.Dungeon.CtrlSkill = false;
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 3000, 8000);

        Assert.Equal(PilotPhase.BlossomAttack, run.Phase);
        Assert.Equal(1, input.Calls.Count(c => c == "KeyDown 1"));
        Assert.Equal(1, input.Calls.Count(c => c == "KeyUp 1"));
        Assert.Equal("KeyUp 1", input.Calls[^1]);   // let go only when the run stops
        Assert.DoesNotContain("KeyTap 1", input.Calls);
    }

    [Fact]
    public void HoldSkill_IsOnByDefault() => Assert.True(new AppSettings().Blossom.HoldSkill);

    [Fact]
    public async Task Fight_NoMoveAndNoHit_StepsAsideToReposition()
    {
        // User, 2026-10-30: "if a monster is tab, but there is 5s that it didn't move and no attack succeeded log, try to click
        // on other direction to reposition".
        Assert.Equal(5000, new AppSettings().Blossom.FightStuckMs);
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => "[S]Jaso Practitioner",
            log: _ => ["Attack succeeded!Normal damage(+100) Attribution"]);   // the same line all along: no new hit
        settings.Blossom.FightStuckMs = 1500;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, _) => input.Calls.Count(c => c.StartsWith("CtrlClick", StringComparison.Ordinal)) >= 2, 12000);

        var steps = input.Calls.Where(c => c.StartsWith("CtrlClick", StringComparison.Ordinal))
            .Select(c => c["CtrlClick ".Length..].Split(',').Select(int.Parse).ToArray()).ToList();
        Assert.True(steps.Count >= 2, string.Join("\n", run.Lines));
        Assert.True(steps[0][0] > Width / 2 + 50, $"first step not to the right: {steps[0][0]},{steps[0][1]}");
        Assert.True(steps[1][0] < Width / 2 - 50, $"second step not to the left: {steps[1][0]},{steps[1][1]}");
        Assert.Contains(run.Lines, l => l.Contains("stepping", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.BlossomAttack, run.Phase);
        Assert.DoesNotContain(run.Lines, l => l.Contains("down (kill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fight_TargetHpFalling_WithTheLogFullOfLootLines_DoesNotStepAside()
    {
        // Live 2026-10-30 11:19: looting while fighting fills the combat log with "No items or [Silver] to obtain", so no
        // "Attack succeeded" line shows - the Jaso was at 73934/750000 and dying, and the pilot stepped aside for nothing.
        int reads = 0;
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => "[S]Jaso Practitioner",
            log: _ => ["No items or [Silver] to obtain", "No items or [Silver] to obtain"],
            hp: i => SkillTaps(i) == 0 ? "750000/750000" : $"{Math.Max(1000, 750000 - 4000 * ++reads)}/750000");   // full until hit
        settings.Blossom.FightStuckMs = 1500;
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 5000, 10000);

        Assert.Equal(PilotPhase.BlossomAttack, run.Phase);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("CtrlClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fight_NewHits_DoesNotStepAside()
    {
        int n = 0;
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => "[S]Jaso Practitioner",
            log: _ => [$"Attack succeeded!Normal damage(+{++n}) Attribution"]);   // a new hit on every read
        settings.Blossom.FightStuckMs = 1500;
        long start = Environment.TickCount64;

        await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 5000, 10000);

        Assert.DoesNotContain(input.Calls, c => c.StartsWith("CtrlClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fight_Moving_DoesNotStepAside()
    {
        int reads = 0;
        var (settings, input, ocr) = Harness(() => $"{15000 + 60 * (++reads / 4)} , 15000", target: _ => "[S]Jaso Practitioner");
        settings.Blossom.FightStuckMs = 1500;
        long start = Environment.TickCount64;

        await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 5000, 10000);

        Assert.DoesNotContain(input.Calls, c => c.StartsWith("CtrlClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackAndForth_FightAtTheLastWaypoint_TurnsBack_DoesNotLoop()
    {
        // A Jaso met while standing at the last waypoint: after it the walk heads back to the one before, not to waypoint 1.
        var (settings, input, ocr) = Harness(() => "10000 , 8000",
            target: i => Tabs(i) >= 3 && SkillTaps(i) < 3 ? "[S]Jaso Practitioner" : null);   // during the pause
        settings.Blossom.Waypoints = ["10000,10000", "10000,9000", "10000,8000"];
        settings.Blossom.LoopFrom = 1;
        settings.Blossom.BackAndForth = true;
        settings.Blossom.LootWhileFighting = false;   // its "Looted ... on to waypoint N" line names the next waypoint
        settings.Blossom.LootMs = 400;
        settings.Blossom.LootDelayMs = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Looted", StringComparison.Ordinal)), 15000);

        string looted = run.Lines.FirstOrDefault(l => l.StartsWith("Looted", StringComparison.Ordinal)) ?? string.Join("\n", run.Lines);
        Assert.EndsWith("on to waypoint 2.", looted);
    }

    [Fact]
    public async Task Overshot_ClicksBackPastTheCharactersBody_AndReachesTheWaypoint()
    {
        // Live run 7: waypoint 1 overshot ~50 north; the short click back south landed on the character's own body (just
        // below the middle of the screen), did nothing, and the walk took "close enough" 52 off. A click within 70 px under
        // the character's feet does nothing here.
        int x = 19107, y = 18424 - 60, seen = 0;  // 60 north of waypoint 1, the view looks north
        FakeGameInput? sent = null;
        var (settings, input, ocr) = Harness(() =>
        {
            var clicks = sent!.Calls.Where(c => c.StartsWith("Click ", StringComparison.Ordinal)).ToList();
            for (; seen < clicks.Count; seen++)
            {
                var p = clicks[seen]["Click ".Length..].Split(',').Select(int.Parse).ToArray();
                double fx = p[0] - Width / 2.0, fy = p[1] - Height * 0.55;   // from the feet
                if (fy > -40 && fy < 70 && Math.Abs(fx) < 50)
                    continue;                                                // on the body: nothing happens
                x += (int)Math.Round(fx * 0.5);
                y += (int)Math.Round(fy * 0.5);
            }

            return $"{x} , {y}";
        });
        sent = input;
        settings.Blossom.WalkReadEveryMs = 300;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Waypoint 1 reached", StringComparison.Ordinal)), 20000);

        string line = run.Lines.FirstOrDefault(l => l.StartsWith("Waypoint 1 reached", StringComparison.Ordinal)) ?? string.Join("\n", run.Lines);
        Assert.DoesNotContain("close enough", line);
        Assert.StartsWith("Waypoint 1 reached at", line);
    }

    [Fact]
    public async Task BlockedCloseToTheWaypoint_CountsAsReached()
    {
        var (settings, input, ocr) = Harness(() => "13964 , 14806"); // 120 south of waypoint 6, never moves
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.StartsWith("Waypoint 6", StringComparison.Ordinal) && !l.Contains("Walking", StringComparison.Ordinal)), 15000);

        string log = string.Join("\n", run.Lines);
        Assert.True(run.Lines.Any(l => l.StartsWith("Waypoint 6 reached", StringComparison.Ordinal) && l.Contains("close enough", StringComparison.Ordinal)), log);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Waypoint 6 skipped", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[B/ossom Garden")]   // live: the l read as a slash
    [InlineData("[Blossorn Garden")]
    public async Task TitleWithAMisreadLetter_IsStillBlossomGarden(string title)
    {
        var (settings, input, ocr) = Harness(() => "16500 , 17000", titleText: title);
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 9000, 15000);

        Assert.DoesNotContain(run.Lines, l => l.Contains("starting the next run", StringComparison.Ordinal) || l.Contains("does not show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TitleReadCutShort_IsStillBlossomGarden()
    {
        // Live run 6: the minimap title read as "[Blossom X" (the panel's close button next to the cut-off name) three times
        // and the zone guard restarted the run as if the character had left Blossom Garden.
        var (settings, input, ocr) = Harness(() => "16500 , 17000", titleText: "[Blossom X");
        long start = Environment.TickCount64;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => Environment.TickCount64 - start > 9000, 15000);

        string log = string.Join("\n", run.Lines);
        Assert.DoesNotContain(run.Lines, l => l.Contains("starting the next run", StringComparison.Ordinal) || l.Contains("does not show", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.BlossomWalk, run.Phase);
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
        bool autoBuffScroll = true, bool title = true, Func<FakeGameInput, string[]>? log = null,
        string titleText = "[Blossom Garden]", Func<FakeGameInput, string?>? hp = null, Func<FakeGameInput, string[]>? chat = null)
    {
        // The scroll in the item list keeps the buff round short: a Start buffs first (the timer starts at 0), and with no
        // buff key and no templates the round clicks nothing and goes back to the walk.
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoBlossom;
        settings.Lod.BuffKey = string.Empty;
        settings.Lod.HoldSkill = false;
        settings.Blossom.HoldSkill = false;   // the tests count skill taps; HoldSkill_KeepsTheSkillKeyDown_WhileFighting holds
        settings.Blossom.RunAheadDistance = 0;   // every waypoint reached; DenseRoute_RunsPastNearWaypoints runs ahead
        settings.Blossom.RandomSkipMax = 0;      // no random walk after a kill

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
                words.Add(FakeOcrEngine.Line(titleText, 1130, 55)); // where the live 2048x1152 frame has it, scaled
            if (log?.Invoke(input) is { } lines)
            {
                // the combat log, left side (Regions.AttackMessage)
                for (int i = 0; i < lines.Length; i++)
                    words.Add(FakeOcrEngine.Line(lines[i], 20, 300 + i * 20));
            }
            if (target?.Invoke(input) is { } name)
            {
                words.Add(FakeOcrEngine.Line(name, 620, 50)); // the target bar, top centre
                if (hp?.Invoke(input) is { } numbers)
                    words.Add(FakeOcrEngine.Line(numbers, 620, 75)); // its HP numbers, under the name
            }
            if (autoBuffScroll)
                words.Add(FakeOcrEngine.Line("[Auto buff scroll] 01.01.2027", 1000, 700)); // the item list, bottom right
            if (chat?.Invoke(input) is { } said)
            {
                // the chat box, bottom left
                for (int i = 0; i < said.Length; i++)
                    words.Add(FakeOcrEngine.Line(said[i], 20, 700 + i * 20, 320));
            }
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
