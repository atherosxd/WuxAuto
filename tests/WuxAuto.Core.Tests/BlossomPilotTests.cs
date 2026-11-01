using Emgu.CV;
using Emgu.CV.Structure;
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
    public async Task Blocked_BeforeTheHeadingIsKnown_StillTriesTheWays_AndLearnsTheHeadingFromTheStep()
    {
        // Live 18:08 (0.25.10): after a fight the heading was unknown (the fight scan had turned the camera ~190 deg), the
        // first step straight up the screen ran into a wall, and the ways round it were only tried with a known heading - it
        // stood there 20 s, a waypoint skipped, until the AFK checker's step moved it.
        bool stepped = false;
        int y = 15000;
        var (settings, input, ocr) = Harness(() =>
        {
            if (stepped)
                y -= 40;   // moves once a way round goes out
            return $"15000 , {y}";
        });
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, lines) =>
        {
            stepped |= lines.Any(l => l.StartsWith("Blocked at", StringComparison.Ordinal) && l.Contains("stepping", StringComparison.Ordinal));
            return lines.Any(l => l.Contains("Heading learnt from the first step", StringComparison.Ordinal));
        }, 15000);

        string log = string.Join("\n", run.Lines);
        Assert.True(stepped, log);
        Assert.Contains(run.Lines, l => l.Contains("Heading learnt from the first step", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Lines, l => l.Contains(" skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stuck_GoesBackToTheNearestWaypoint_OncePerWaypoint()
    {
        // User, 18:10: "if stuck try to sidestep and return back to nearest waypoint". From waypoint 1 the walk heads for
        // waypoint 2 (3190 units on) and never moves: after the sidesteps it goes back to waypoint 1, the nearest, and from
        // there for waypoint 2 again - stuck on waypoint 2 a second time, it does not go back again (that would loop) but
        // keeps stepping round.
        var (settings, input, ocr) = Harness(() => "19107 , 18424");   // waypoint 1, never moves
        settings.Blossom.WalkReadEveryMs = 150;
        settings.Blossom.WalkClickEveryMs = 100;
        settings.Blossom.WaypointWaitMs = 0;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Count(l => l.StartsWith("Not moving for", StringComparison.Ordinal)) >= 2, 20000);

        string log = string.Join("\n", run.Lines);
        Assert.True(run.Lines.Count(l => l.StartsWith("Not moving for", StringComparison.Ordinal)) >= 2, log);
        Assert.Single(run.Lines, l => l.Contains("back to the nearest waypoint 1", StringComparison.Ordinal));
        int back = run.Lines.FindIndex(l => l.Contains("back to the nearest waypoint 1", StringComparison.Ordinal));
        Assert.Contains(run.Lines.Skip(back), l => l.StartsWith("Waypoint 1 reached", StringComparison.Ordinal));
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
        // The first default route (17 waypoints, 1-5 then 6-17 looped, camera turns at 1-4): the walk tests were written on it.
        settings.Blossom.Waypoints = BlossomSettings.First17Waypoints();
        settings.Blossom.LoopFrom = 6;
        settings.Blossom.BackAndForth = false;
        settings.Blossom.WaypointTurns = ["1:A5", "2:D5", "3:A5", "4:D5"];
        settings.Blossom.ScanEveryMs = 0;        // no camera scans; the Jaso click tests turn them on
        settings.Blossom.FightScanEveryMs = 0;
        settings.Blossom.CameraTiltPx = 0;       // no camera tilt; the CameraTilt tests turn it on

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

    // ---- 2026-11-01: Tab no longer targets in Blossom Garden - a Jaso seen on screen is clicked

    /// <summary>A Jaso as <see cref="ScreenAnalyzer.FindJasos"/> knows her: a dark magenta fan over an orange body (BGRA).</summary>
    private static void DrawJaso(Mat frame, int x, int y)
    {
        CvInvoke.Rectangle(frame, new System.Drawing.Rectangle(x, y, 30, 15), new MCvScalar(45, 30, 70, 255), -1);
        CvInvoke.Rectangle(frame, new System.Drawing.Rectangle(x + 8, y + 15, 14, 40), new MCvScalar(40, 80, 170, 255), -1);
    }

    /// <summary>A plain left click inside the figure drawn at (x, y).</summary>
    private static bool ClickedJasoAt(FakeGameInput input, int x, int y) => input.Calls
        .Where(c => c.StartsWith("Click ", StringComparison.Ordinal) && c.Split(' ').Length == 2)   // a left click, no button named
        .Select(c => c["Click ".Length..].Split(',').Select(int.Parse).ToArray())
        .Any(p => p[0] >= x - 4 && p[0] <= x + 34 && p[1] >= y && p[1] <= y + 58);

    [Fact]
    public async Task NoTab_AJasoOnScreen_IsClicked_ThenFought()
    {
        // User, 2026-11-01: "they have disabled tabbing in the Blossom, can you possibly click this mob instead by using the
        // color, shape".
        FakeGameInput? sent = null;
        var (settings, input, ocr) = Harness(() => "15000 , 15000",
            target: i => ClickedJasoAt(i, 600, 250) ? "[S]Jaso Practitioner" : null);   // the target bar shows her once clicked
        sent = input;
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal)), 10000);

        string log = string.Join("\n", run.Lines);
        Assert.True(ClickedJasoAt(input, 600, 250), log + "\n" + string.Join("\n", input.Calls));
        Assert.Contains(run.Lines, l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal));
    }

    private static bool IsTurn(string call) => call is "KeyDown A" or "KeyDown D";

    [Fact]
    public async Task NoJasoInView_TheCameraLooksAside_AndOneThatComesIntoViewIsClicked()
    {
        // User, 2026-11-01: "also add when running, to rotate the camera to scan for the mob"; 18:10: "randomly do left or
        // right, stay on any direction longer than 5s then return back, as when you are trying to rotate there is a jaso but it
        // continued to rotate back and never target it".
        var (settings, input, ocr) = Harness(MovingNorth());
        settings.Blossom.ScanEveryMs = 1000;
        settings.Blossom.ScanSideMs = 100;
        settings.Blossom.LookAsideMs = 3000;
        using var frame = Blank.Bgra(Height, Width);
        bool drawn = false;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) => ClickedJasoAt(input, 900, 250), 12000,
            beforeTick: () =>
            {
                if (!drawn && input.Calls.Any(IsTurn))
                {
                    drawn = true;
                    DrawJaso(frame, 900, 250);   // she shows once the camera looks aside
                }
            });

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        Assert.True(ClickedJasoAt(input, 900, 250), log);
        // User, 18:20: "rotating to right, sees jaso rotates left then continues instead of going back and retargeting" - the
        // turn is three short holds with a look after each: she shows after the first, and the turn stops there.
        Assert.Single(input.Calls, IsTurn);
        Assert.Contains(run.Lines, l => l.Contains("looking", StringComparison.Ordinal) && l.Contains("while walking on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJasoClickedMidTurn_EndsTheTurn_ItDoesNotTurnOnAfterTheWait()
    {
        // Live 18:37 (0.25.11): the walk's own Jaso check clicked one while the camera was still turning out; nothing was
        // targeted, and after the 7 s wait the turn carried on with its remaining steps - away from where she was.
        var (settings, input, ocr) = Harness(MovingNorth());
        settings.Blossom.ScanEveryMs = 1000;
        settings.Blossom.ScanSideMs = 1500;   // 500 ms a step: the walk's 700 ms check comes round mid-turn
        settings.Blossom.LookAsideMs = 1000;
        using var frame = Blank.Bgra(Height, Width);
        bool drawn = false, erased = false;
        string? outKey = null;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("turning back", StringComparison.Ordinal))
                          && input.Calls.Any(c => IsTurn(c) && c != outKey), 20000,
            beforeTick: () =>
            {
                outKey ??= input.Calls.FirstOrDefault(IsTurn);
                if (!drawn && outKey != null)
                {
                    drawn = true;
                    DrawJaso(frame, 900, 250);
                }

                if (drawn && !erased && ClickedJasoAt(input, 900, 250))
                {
                    erased = true;   // she walks off - nothing is targeted
                    frame.SetTo(new MCvScalar(0, 0, 0, 255));
                }
            });

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        Assert.True(erased, log);
        Assert.Equal(1, input.Calls.Count(c => c == outKey));                       // the turn stopped at the click
        Assert.Equal(1, input.Calls.Count(c => IsTurn(c) && c != outKey));          // and goes back as far as it went
    }

    [Fact]
    public async Task AJasoSeenWhileTurningBack_StopsTheTurnThere_AndIsClicked()
    {
        // User, 18:20: "rotating to right, sees jaso rotates left then continues instead of going back and retargeting".
        var (settings, input, ocr) = Harness(MovingNorth());
        settings.Blossom.ScanEveryMs = 1000;
        settings.Blossom.ScanSideMs = 150;
        settings.Blossom.LookAsideMs = 1000;
        using var frame = Blank.Bgra(Height, Width);
        bool drawn = false;
        string? outKey = null;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) => ClickedJasoAt(input, 900, 250), 15000,
            beforeTick: () =>
            {
                outKey ??= input.Calls.FirstOrDefault(IsTurn);
                if (!drawn && outKey != null && input.Calls.Any(c => IsTurn(c) && c != outKey))
                {
                    drawn = true;
                    DrawJaso(frame, 900, 250);   // she shows on the way back
                }
            });

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        Assert.True(ClickedJasoAt(input, 900, 250), log);
        Assert.Equal(3, input.Calls.Count(c => c == outKey));                      // all the way out
        Assert.Single(input.Calls, c => IsTurn(c) && c != outKey);                  // one step back, then she was clicked
    }

    [Fact]
    public async Task LookAside_StaysThatWayWhileTheWalkGoesOn_ThenTurnsBack_MeasuringTheHeadingEachTime()
    {
        // User, 18:10: "actively walk to the waypoints but randomly do left or right, stay on any direction longer than 5s then
        // return back" / "it should be still similar to when tab is allowed". One turn, the walk goes on as always (walk
        // clicks, the target read, Jaso clicks), then one turn back the other way. The camera never turns back exactly
        // (live 17:35-17:48, 0.25.9: the walk ran ~2700 units east of the route), so the step after each turn re-measures the
        // heading, and no turn comes before that.
        var (settings, input, ocr) = Harness(MovingNorth());
        settings.Blossom.ScanEveryMs = 1000;
        settings.Blossom.ScanSideMs = 100;
        settings.Blossom.LookAsideMs = 1500;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("restored after looking aside", StringComparison.Ordinal)), 15000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        var turns = input.Calls.Select((c, i) => (c, i)).Where(t => IsTurn(t.c)).Take(6).ToList();
        Assert.True(turns.Count == 6, log);
        Assert.All(turns.Take(3), t => Assert.Equal(turns[0].c, t.c));                 // out: three short holds
        Assert.All(turns.Skip(3), t => Assert.NotEqual(turns[0].c, t.c));              // back the other way, as many
        Assert.Contains(input.Calls.Skip(turns[2].i).Take(turns[3].i - turns[2].i), c => c.StartsWith("Click ", StringComparison.Ordinal));   // walking on meanwhile
        long away = input.FirstAt(turns[0].c)!.Value, back = input.FirstAt(turns[3].c)!.Value;
        Assert.True(back - away >= 1400, $"turned back after {back - away} ms\n{log}");
        int learnt = run.Lines.FindIndex(l => l.Contains("Heading learnt from the first step", StringComparison.Ordinal));
        int aside = run.Lines.FindIndex(l => l.Contains("while walking on", StringComparison.Ordinal));
        int measured = run.Lines.FindIndex(l => l.Contains("re-measured after the camera turned", StringComparison.Ordinal));
        int turnBack = run.Lines.FindIndex(l => l.Contains("turning back", StringComparison.Ordinal));
        int restored = run.Lines.FindIndex(l => l.Contains("restored after looking aside", StringComparison.Ordinal));
        Assert.True(learnt >= 0 && aside > learnt && measured > aside && turnBack > aside && restored > turnBack, log);
        // Live 18:44-18:48 (0.25.11): one step measured right after a turn read 19 -> -11 -> 169 -> 27 deg in the walled
        // stretch (sliding along walls). Turned back as far as it went, the walk takes the heading it had before - learnt over
        // many steps with the camera ahead - and the usual rules correct what the turn left over.
        Assert.Equal(Degrees(run.Lines[learnt]), Degrees(run.Lines[restored]));

        static string Degrees(string line) => line[(line.IndexOf("turned ", StringComparison.Ordinal) + 7)..];
    }

    [Fact]
    public async Task NoLookAside_BeforeTheWalkKnowsItsHeading()
    {
        // The coordinates never move, so the heading is never learnt: turning the camera then would only lose the way more.
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        settings.Blossom.ScanEveryMs = 1000;
        settings.Blossom.ScanSideMs = 100;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomWalk, (_, _) => false, 4000);

        Assert.DoesNotContain(run.Lines, l => l.Contains("while walking on", StringComparison.Ordinal));
        Assert.DoesNotContain(input.Calls, IsTurn);
    }

    /// <summary>Coordinates that move north 200 units a second from 15000, 15000: the walk learns its heading from them.</summary>
    private static Func<string> MovingNorth()
    {
        long start = Environment.TickCount64;
        return () => $"15000 , {15000 - (Environment.TickCount64 - start) / 5}";
    }

    [Fact]
    public async Task InAFight_TheCameraLooksForTheNextJaso_AndSheIsClickedRightAfterTheKill()
    {
        // User, 2026-11-01: "do the rotate also even when attacking the current mob, and set that as a second subject".
        var (settings, input, ocr) = Harness(() => "15000 , 15000",
            target: i => SkillTaps(i) < 8 ? "[S]Jaso Practitioner" : null);   // the one being fought dies after 8 taps
        settings.Blossom.FightScanEveryMs = 1000;
        settings.Blossom.TargetGoneMs = 1000;
        using var frame = Blank.Bgra(Height, Width);
        bool drawn = false;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("down (kill 1)", StringComparison.Ordinal)) && ClickedJasoAt(input, 1000, 300), 15000,
            beforeTick: () =>
            {
                if (!drawn && input.Calls.Contains("KeyDown D"))
                {
                    drawn = true;
                    DrawJaso(frame, 1000, 300);   // the camera step shows the next one
                }
            });

        string log = string.Join("\n", run.Lines);
        Assert.True(ClickedJasoAt(input, 1000, 300), log);
        int kill = run.Lines.FindIndex(l => l.Contains("down (kill 1)", StringComparison.Ordinal));
        int next = run.Lines.FindIndex(l => l.Contains("next Jaso", StringComparison.Ordinal));
        Assert.True(next >= 0 && next < kill, "the next one should be seen during the fight:\n" + log);
        Assert.Contains(run.Lines.Skip(kill), l => l.Contains("clicking", StringComparison.Ordinal));
    }

    // ---- 2026-11-01: "also try to move the screen up instead of facing down"

    /// <summary>The right-drags sent, as (from y, to y - from y) - positive = down, towards the steepest view.</summary>
    private static List<(int Index, int FromY, int Dy)> Drags(FakeGameInput input) => input.Calls
        .Select((c, i) => (c, i))
        .Where(t => t.c.StartsWith("Drag ", StringComparison.Ordinal))
        .Select(t =>
        {
            var ends = t.c["Drag ".Length..].Split("->");
            int from = int.Parse(ends[0].Split(',')[1]), to = int.Parse(ends[1].Split(',')[1]);
            return (t.i, from, to - from);
        })
        .ToList();

    [Fact]
    public async Task CameraTilt_OnArrival_OneDragUp_BeforeThePillarTurn_NeverDown()
    {
        // User, 2026-11-01: "also upon teleporting it look down, look up by dragging right click up" / "becuase you tilt it
        // down!" / "upon arrival tilt up a little bit": one drag up before the pillar turn's first tap - no drag down to a
        // limit first (that looked straight down) - and the walk after the turn does not tilt again.
        var (settings, input, ocr) = Harness(() => "19148 , 19062");
        settings.Blossom.TurnMaxTaps = 2;
        settings.Blossom.CameraTiltPx = 60;

        var run = await RunAsync(settings, input, ocr, PilotPhase.BlossomTurn,
            (_, _) => input.Calls.Any(c => c.StartsWith("Click ", StringComparison.Ordinal)), 15000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        var drag = Assert.Single(Drags(input));
        Assert.Equal(-60, drag.Dy);                                                  // 60 px up
        Assert.True(drag.Index < input.Calls.IndexOf("KeyDown D"), log);              // before the pillar turn
        Assert.Contains(run.Lines, l => l.Contains("Tilted the camera up", StringComparison.Ordinal));
    }

    // ---- 2026-11-01 14:26: a different cursor picture also showed where no sword was (the plain Windows arrow right after the
    // game comes to the front; another picture at the character's own spot) - the sword is confirmed by a second read and
    // learnt from a click that targeted a Jaso.

    [Fact]
    public async Task SwordCursorOff_ByDefault_NoHovering_AFullJasoIsClickedInHerMiddle_APartialOneNever()
    {
        // User, 2026-11-01 15:00: "Turn it off" - the game's cursor read from outside was not reliable (the plain Windows
        // arrow after the game comes to the front, the game's pointer over characters, at times no cursor at all).
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        Assert.False(settings.Blossom.UseSwordCursor);
        input.Cursor = (x, y) => 2;   // whatever it would read
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);
        DrawFarPartial(frame);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk, (_, _) => LeftClicks(input).Count > 0, 10000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("Hover ", StringComparison.Ordinal));
        var (cx, cy) = LeftClicks(input)[0];
        Assert.True(cx is >= 596 and <= 634 && cy is >= 250 and <= 308, log);      // the full one, in her middle
        Assert.DoesNotContain(LeftClicks(input), p => OnFarPartial(p.X, p.Y));
    }

    private static void DrawFarPartial(Mat frame)
    {
        CvInvoke.Rectangle(frame, new System.Drawing.Rectangle(900, 260, 14, 7), new MCvScalar(45, 30, 70, 255), -1);
        CvInvoke.Rectangle(frame, new System.Drawing.Rectangle(904, 267, 6, 12), new MCvScalar(30, 30, 150, 255), -1);
    }

    private static bool OnFarPartial(int x, int y) => x is >= 898 and <= 916 && y is >= 258 and <= 281;

    [Fact]
    public async Task SwordCursor_AOneReadFlicker_IsNotASword()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        var reads = new HashSet<(int, int)>();
        settings.Blossom.UseSwordCursor = true;
        input.Cursor = (x, y) => reads.Add((x, y)) && OnFarPartial(x, y) ? 2 : 1;   // 2 only on the first read at a point
        using var frame = Blank.Bgra(Height, Width);
        DrawFarPartial(frame);

        await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk, (_, _) => false, 2500);

        Assert.DoesNotContain(LeftClicks(input), p => OnFarPartial(p.X, p.Y));
    }

    [Fact]
    public async Task SwordCursor_AClickThatTargetsAJaso_TeachesTheSwordPicture()
    {
        bool targeted = false;
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => targeted ? "[S]Jaso Practitioner" : null);
        settings.Blossom.UseSwordCursor = true;
        input.Cursor = (x, y) => x is >= 596 and <= 634 && y is >= 250 and <= 308 ? 7 : 1;
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal)), 10000,
            beforeTick: () => targeted = LeftClicks(input).Count > 0);

        string log = string.Join("\n", run.Lines);
        Assert.Equal(7, settings.Blossom.SwordCursor);
        Assert.Contains(run.Lines, l => l.Contains("sword cursor", StringComparison.Ordinal) && l.Contains("0x7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LearntSword_OnlyThatPictureCounts()
    {
        foreach (var (over, clicked) in ((long, bool)[])[(9, false), (7, true)])
        {
            var (settings, input, ocr) = Harness(() => "15000 , 15000");
            settings.Blossom.SwordCursor = 7;
            settings.Blossom.UseSwordCursor = true;
            input.Cursor = (x, y) => OnFarPartial(x, y) ? over : 1;
            using var frame = Blank.Bgra(Height, Width);
            DrawFarPartial(frame);

            await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk, (_, _) => LeftClicks(input).Count > 0, 2500);

            Assert.Equal(clicked, LeftClicks(input).Any(p => OnFarPartial(p.X, p.Y)));
        }
    }

    // ---- 2026-11-01 15:55: "Yes, add snapshots" - every click on a Jaso is saved (her box, every other figure seen, the click
    // point), and once the outcome is known the file is marked _hit (the target bar showed her) or _miss.

    /// <summary>The click snapshots in this run's debug folder (only inside a <see cref="PilotRun.RunAsync"/> callback).</summary>
    private static string[] ClickShots()
    {
        string folder = Path.Combine(AppPaths.DebugFolder, "jaso-clicks");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.jpg").Select(f => Path.GetFileName(f)).OfType<string>().Order().ToArray() : [];
    }

    [Fact]
    public async Task EveryJasoClick_IsSaved_AndMarkedHitWhenTheTargetBarShowsHer()
    {
        bool targeted = false;
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => targeted ? "[S]Jaso Practitioner" : null);
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);
        string[] shots = [];

        await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, lines) =>
            {
                shots = ClickShots();
                return lines.Any(l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal));
            }, 10000,
            beforeTick: () => targeted = LeftClicks(input).Count > 0);

        var shot = Assert.Single(shots);
        Assert.EndsWith("_hit.jpg", shot);
    }

    [Fact]
    public async Task AClickThatTargetsNothing_IsMarkedMiss()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);
        string[] shots = [];

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) =>
            {
                shots = ClickShots();
                return LeftClicks(input).Count >= 2;   // the second click: the first one targeted nothing
            }, 20000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", shots);
        Assert.True(shots.Length >= 2, log);
        Assert.EndsWith("_miss.jpg", shots[0]);
        Assert.Contains(run.Lines, l => l.Contains("jaso-clicks", StringComparison.Ordinal) || l.Contains("snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJasoNextToTheCharacter_IsClickedWhereTheSwordShows()
    {
        // User, 2026-11-01 13:45: the Jaso fighting the character (moving, or in her AOE attack) stood in the character's own
        // spot, which the search left out - the pilot scanned on instead.
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        int jx = Width / 2 - 15, jy = Height / 2 - 20;
        settings.Blossom.UseSwordCursor = true;
        input.Cursor = (x, y) => x >= jx && x <= jx + 30 && y >= jy + 15 && y <= jy + 55 ? 2 : 1;   // her body
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, jx, jy);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) => LeftClicks(input).Count > 0, 10000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        var (cx, cy) = LeftClicks(input)[0];
        Assert.True(cx >= jx && cx <= jx + 30 && cy >= jy + 15 && cy <= jy + 55, log);
    }

    [Fact]
    public async Task SideOnJaso_TheSwordBesideTheMiddle_IsFound()
    {
        // User, 2026-11-01 13:45: "it tried to click the monster but on the side not directly on it". Seen side-on the fan
        // sticks out to one side and her body is off the figure's middle: points left and right of it are tried as well.
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        settings.Blossom.UseSwordCursor = true;
        input.Cursor = (x, y) => x is >= 600 and <= 606 ? 2 : 1;   // only the left edge of the drawn figure
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) => LeftClicks(input).Count > 0, 10000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        var (cx, _) = LeftClicks(input)[0];
        Assert.True(cx is >= 600 and <= 606, log);
        Assert.Contains(run.Lines, l => l.Contains("sword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJasoAtTheHorizon_IsNotClicked_OneInRangeIs()
    {
        // Live 2026-11-01 13:37-13:38 (0.25.3): three clicks on a Jaso at 1106,233 (0.20 h) with the sword cursor over her -
        // she stayed put, nothing was targeted; every click that targeted one that day was 0.23 h or lower. The game ignores
        // clicks on a monster that far: figures above JasoMinClickY are left for the walk to bring closer.
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        settings.Blossom.UseSwordCursor = true;
        input.Cursor = (x, y) => (x is >= 596 and <= 634 && y is >= 150 and <= 210) || (x is >= 996 and <= 1034 && y is >= 300 and <= 360) ? 2 : 1;
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 150);    // at the horizon: 150-205 of 900, centre 0.20 h
        DrawJaso(frame, 1000, 300);   // in range

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) => LeftClicks(input).Count > 0, 10000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        var (cx, cy) = LeftClicks(input)[0];
        Assert.True(cx is >= 996 and <= 1034 && cy is >= 300 and <= 360, log);       // the one in range
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("Hover ", StringComparison.Ordinal)
                                                 && c["Hover ".Length..].Split(',').Select(int.Parse).ToArray() is [>= 596 and <= 634, < 210]);
    }

    [Fact]
    public async Task AfterAClickOnAJaso_TheWalkWaitsForTheTargetBar_NoWalkClickNoScan()
    {
        // Live 2026-11-01 12:27-12:36: a click from afar works - the character runs to her and the target bar shows 3-6 s
        // later - but the walk clicked on after 1.5 s and the camera scan started at 4 s, which took the character away.
        bool shown = false;
        var (settings, input, ocr) = Harness(() => "15000 , 15000", target: _ => shown ? "[S]Jaso Practitioner" : null);
        settings.Blossom.ScanEveryMs = 1000;
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);
        long clickedAt = 0;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, lines) => lines.Any(l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal)), 15000,
            beforeTick: () =>
            {
                if (clickedAt == 0 && ClickedJasoAt(input, 600, 250))
                    clickedAt = Environment.TickCount64;
                shown = clickedAt > 0 && Environment.TickCount64 - clickedAt > 4500;   // she is reached 4.5 s after the click
            });

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("Jaso Practitioner targeted", StringComparison.Ordinal));
        int jasoClick = input.Calls.FindIndex(c => c.StartsWith("Click ", StringComparison.Ordinal));
        var after = input.Calls.Skip(jasoClick + 1).ToList();
        Assert.DoesNotContain(after, c => c.StartsWith("Click ", StringComparison.Ordinal));   // no walk click while she is reached
        Assert.DoesNotContain(after, IsTurn);                                                 // no camera turn either
    }

    // ---- 2026-11-01: "when clicking jaso, the cursor will change into a sword" / "it should be able to click a jaso from
    // afar with just some of the characteristics of the monster"

    /// <summary>The plain left clicks sent, as points.</summary>
    private static List<(int X, int Y)> LeftClicks(FakeGameInput input) => input.Calls
        .Where(c => c.StartsWith("Click ", StringComparison.Ordinal) && c.Split(' ').Length == 2)
        .Select(c => c["Click ".Length..].Split(',').Select(int.Parse).ToArray())
        .Select(p => (p[0], p[1]))
        .ToList();

    [Fact]
    public async Task SwordCursor_TheClickGoesWhereTheCursorTurnsIntoASword()
    {
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        static bool Sword(int x, int y) => x is >= 610 and <= 620 && y is >= 264 and <= 272;   // only a small part of her
        settings.Blossom.UseSwordCursor = true;
        input.Cursor = (x, y) => Sword(x, y) ? 2 : 1;
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) => LeftClicks(input).Count > 0, 10000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        var (cx, cy) = LeftClicks(input)[0];
        Assert.True(Sword(cx, cy), log);                                               // clicked where the sword showed
        Assert.True(input.Calls.Count(c => c.StartsWith("Hover ", StringComparison.Ordinal)) >= 2, log);
        Assert.Contains(run.Lines, l => l.Contains("sword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwordCursor_NeverShowsOverAFullJaso_SheIsClickedInHerMiddleAnyway()
    {
        // The cursor can be read but stays the normal one over her (the hover too quick for the game, say): her whole look
        // is there, so she is clicked as before the cursor check - after the pointer went over her.
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        settings.Blossom.UseSwordCursor = true;
        input.Cursor = (_, _) => 1;
        using var frame = Blank.Bgra(Height, Width);
        DrawJaso(frame, 600, 250);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk,
            (_, _) => LeftClicks(input).Count > 0, 10000);

        string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input.Calls);
        var first = LeftClicks(input)[0];
        Assert.True(first.X is >= 596 and <= 634 && first.Y is >= 250 and <= 308, log);
        Assert.True(input.Calls.Count(c => c.StartsWith("Hover ", StringComparison.Ordinal)) >= 5, log);   // the ground + 4 points over her
        Assert.Contains(run.Lines, l => l.Contains("no sword cursor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FarPartialJaso_ClickedWhenTheSwordShows_NeverWithoutIt()
    {
        // Far up the field only part of her shows (a small fan over a red body, no orange skirt to see).
        static void DrawFar(Mat frame)
        {
            CvInvoke.Rectangle(frame, new System.Drawing.Rectangle(900, 200, 14, 7), new MCvScalar(45, 30, 70, 255), -1);
            CvInvoke.Rectangle(frame, new System.Drawing.Rectangle(904, 207, 6, 12), new MCvScalar(30, 30, 150, 255), -1);
        }

        static bool OnHer((int X, int Y) p) => p.X is >= 898 and <= 916 && p.Y is >= 198 and <= 221;

        // The cursor cannot be read, or stays the normal one over her: a partial figure is never clicked on its looks alone.
        foreach (Func<int, int, long>? cursor in (Func<int, int, long>?[])[null, (_, _) => 1])
        {
            var (settings, input, ocr) = Harness(() => "15000 , 15000");
            settings.Blossom.UseSwordCursor = true;
            input.Cursor = cursor;
            using var frame = Blank.Bgra(Height, Width);
            DrawFar(frame);
            await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.BlossomWalk, (_, _) => false, 2500);
            Assert.DoesNotContain(LeftClicks(input), OnHer);
        }

        // The sword shows over her: clicked.
        var (settings2, input2, ocr2) = Harness(() => "15000 , 15000");
        settings2.Blossom.UseSwordCursor = true;
        input2.Cursor = (x, y) => x is >= 900 and <= 914 && y is >= 200 and <= 219 ? 2 : 1;
        using (var frame = Blank.Bgra(Height, Width))
        {
            DrawFar(frame);
            var run = await PilotRun.RunAsync(settings2, input2, ocr2, frame, PilotPhase.BlossomWalk, (_, _) => LeftClicks(input2).Count > 0, 10000);
            string log = string.Join("\n", run.Lines) + "\n" + string.Join("\n", input2.Calls);
            Assert.True(LeftClicks(input2).Count > 0 && OnHer(LeftClicks(input2)[0]), log);
        }
    }

    [Fact]
    public async Task CameraTilt_StartInside_LeavesTheCameraAsItIs()
    {
        // Only the teleport's arrival leaves the camera looking down; started inside, the camera is as the player left it -
        // an up drag on every Start would add up.
        var (settings, input, ocr) = Harness(() => "15000 , 15000");
        settings.Blossom.CameraTiltPx = 80;

        var run = await RunAsync(settings, input, ocr, PilotPhase.CheckZone,
            (_, _) => input.Calls.Any(c => c.StartsWith("Click ", StringComparison.Ordinal)), 15000);

        Assert.Empty(Drags(input));
    }
}
