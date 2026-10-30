using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>Auto Blossom's settings and route rules (user, 2026-10-29: waypoints 1-5 once, then 6-17 in a loop).</summary>
public class BlossomRouteTests
{
    [Fact]
    public void Defaults_AreTheUsersRoute()
    {
        var b = new AppSettings().Blossom;
        var route = BlossomRoute.Parse(b.Waypoints);
        Assert.Equal(17, route.Count);
        Assert.Equal((19107, 18424), route[0]);
        Assert.Equal((13964, 14686), route[5]);
        Assert.Equal((14359, 14565), route[16]);
        Assert.Equal(6, b.LoopFrom);
        Assert.Equal("Blossom Garden", b.Destination);
        Assert.Equal(1, b.ListPage);
        Assert.Equal(3000, b.WaypointWaitMs);
        Assert.Equal("[S]Jaso Practitioner", b.TargetName);
        Assert.Equal(7, b.TurnTaps);
        Assert.Equal(100, b.TurnTapMs);
        // Live 2026-10-29: waypoints 2 and 4 lie just past a railing - the walk stopped 86 and 67 units short.
        // ...then 100 was too loose (live run 5, user: it stopped north of waypoint 1 and did not correct it): 40.
        Assert.Equal(40, b.WaypointTolerance);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 5)]
    [InlineData(15, 16)]
    [InlineData(16, 5)]
    public void Next_Loops17BackTo6(int from, int expected) => Assert.Equal(expected, BlossomRoute.Next(from, 17, 6));

    [Fact]
    public void Next_LoopFromOutOfRange_IsClamped()
    {
        Assert.Equal(0, BlossomRoute.Next(2, 3, 0));
        Assert.Equal(2, BlossomRoute.Next(2, 3, 9));
    }

    [Fact]
    public void BackAndForth_WalksBackFromTheLastWaypoint_ThenForwardAgain()
    {
        // User, 2026-10-30: "no need to loop? instead from last waypoint go back in reverse".
        bool forward = true;
        var seen = new List<int>();
        int i = 0;
        for (int n = 0; n < 8; n++)
        {
            i = BlossomRoute.Next(i, 4, loopFrom: 1, backAndForth: true, ref forward);
            seen.Add(i);
        }

        Assert.Equal([1, 2, 3, 2, 1, 0, 1, 2], seen);
    }

    [Fact]
    public void BackAndForth_TurnsAtLoopFrom_NotBeforeIt()
    {
        bool forward = false;
        Assert.Equal(1, BlossomRoute.Next(2, 5, loopFrom: 2, backAndForth: true, ref forward)); // back to waypoint 2 (index 1)
        Assert.Equal(2, BlossomRoute.Next(1, 5, loopFrom: 2, backAndForth: true, ref forward)); // and forward again from it
        Assert.True(forward);
    }

    [Fact]
    public void SkipWithin_TurnsBackAtTheEndOfAWalkBackRoute()
    {
        // User, 2026-10-30: "randomly try to skip multiple waypoints so that it doesn't look like a bot".
        List<(int X, int Y)> route = [.. Enumerable.Range(0, 10).Select(i => (0, 100 * i))];
        bool forward = true;
        Assert.Equal(7, BlossomRoute.SkipWithin(route, 8, loopFrom: 1, backAndForth: true, ref forward, n: 3, from: (0, 800), reach: 1000));
        Assert.False(forward);   // 9, then back to 8, 7
    }

    [Fact]
    public void SkipWithin_StopsAtTheFirstWaypointOutOfReach()
    {
        // A random skip must not turn into a long straight line across the map (a typed route's waypoints are far apart).
        List<(int X, int Y)> route = [(0, 0), (0, 120), (0, 240), (0, 360), (0, 2000), (0, 2120)];
        bool forward = true;
        Assert.Equal(3, BlossomRoute.SkipWithin(route, 1, loopFrom: 1, backAndForth: true, ref forward, n: 4, from: (0, 100), reach: 1000));
        Assert.Equal(2, BlossomRoute.SkipWithin(route, 1, loopFrom: 1, backAndForth: true, ref forward, n: 1, from: (0, 100), reach: 1000));
        Assert.Equal(4, BlossomRoute.SkipWithin(route, 4, loopFrom: 1, backAndForth: true, ref forward, n: 3, from: (0, 100), reach: 1000));
    }

    [Fact]
    public void RunAheadAndRandomSkip_Defaults()
    {
        var b = new AppSettings().Blossom;
        Assert.Equal(600, b.RunAheadDistance);
        Assert.Equal(4, b.RandomSkipMax);
    }

    [Fact]
    public void BackAndForth_OneWaypoint_StaysPut()
    {
        bool forward = true;
        Assert.Equal(0, BlossomRoute.Next(0, 1, loopFrom: 1, backAndForth: true, ref forward));
    }

    [Fact]
    public void Nearest_PicksTheClosest()
    {
        var route = BlossomRoute.Parse(new AppSettings().Blossom.Waypoints);
        Assert.Equal(0, BlossomRoute.Nearest(route, 19148, 19062)); // the arrival point (user's frame)
        Assert.Equal(9, BlossomRoute.Nearest(route, 12990, 13190));
    }

    [Fact]
    public void Turns_DefaultsAreTheUsers()
    {
        // User, 2026-10-29: "In waypoint 1, do a left turn / 4x A, then on waypoint 2 do right turn / D 5x".
        var turns = BlossomRoute.ParseTurns(new AppSettings().Blossom.WaypointTurns);
        Assert.Equal(4, turns.Count);
        Assert.Equal((TurnDirection.Left, 5), turns[1]); // after the 4th live run: "try 5A instead of 4A"
        Assert.Equal((TurnDirection.Right, 5), turns[2]);
        Assert.Equal((TurnDirection.Left, 5), turns[3]);
        Assert.Equal((TurnDirection.Right, 5), turns[4]);
    }

    [Fact]
    public void TabTarget_DefaultsToNearestMonster_AndAnUnknownValueIsReset()
    {
        // User, 2026-10-30: "make sure that the tab target is nearest monster" - Tab then reaches monsters further away.
        var s = new AppSettings();
        Assert.Equal("Nearest monster", s.Blossom.TabTargetOption);
        Assert.Contains(s.Blossom.TabTargetOption, TabTargetOptions.All);
        s.Blossom.TabTargetOption = "Sound";   // no TAB target value
        SettingsStore.Normalize(s);
        Assert.Equal("Nearest monster", s.Blossom.TabTargetOption);
    }

    [Fact]
    public void Turns_SkipJunk() =>
        Assert.Equal([3], BlossomRoute.ParseTurns(["", "x:A4", "1:Q4", "2:D0", " 3 : d 2 "]).Keys);

    [Theory]
    [InlineData(10150, 9500, 10000, 9250)]   // 150 off the line, 500 along: aim 250 further along, on the line
    [InlineData(10000, 8100, 10000, 8000)]   // close to the end: never past the waypoint
    [InlineData(10000, 10300, 10000, 10000)] // behind the start: the start of the line
    public void LookAhead_AimsAheadOnTheLine(int atX, int atY, double wantX, double wantY)
    {
        var (x, y) = BlossomRoute.LookAhead((10000, 10000), (10000, 8000), (atX, atY), 250);
        Assert.Equal(wantX, x, 3);
        Assert.Equal(wantY, y, 3);
    }

    [Fact]
    public void Turns_CountFirstToo() // the user writes "1:5A"
        => Assert.Equal((TurnDirection.Left, 5), BlossomRoute.ParseTurns(["1:5A"])[1]);

    [Fact]
    public void TurnsText_RoundTrips()
    {
        var b = new AppSettings().Blossom;
        Assert.Equal("1:A5, 2:D5, 3:A5, 4:D5", b.WaypointTurnsText);
        b.WaypointTurnsText = "1:5A; 2:5D,  7:2a";
        Assert.Equal(["1:5A", "2:5D", "7:2a"], b.WaypointTurns);
        Assert.Equal((TurnDirection.Left, 2), BlossomRoute.ParseTurns(b.WaypointTurns)[7]);
    }

    [Fact]
    public void Parse_SkipsJunk() => Assert.Equal([(1, 2)], BlossomRoute.Parse(["", "x,y", " 1 , 2 ", "3"]));

    [Fact]
    public void Normalize_ClampsWaitAndRefillsAnEmptyRoute()
    {
        var s = new AppSettings();
        s.Blossom.WaypointWaitMs = 9000;
        s.Blossom.LoopFrom = 40;
        s.Blossom.Waypoints = [];
        SettingsStore.Normalize(s);
        Assert.Equal(5000, s.Blossom.WaypointWaitMs);
        Assert.Equal(17, BlossomRoute.Parse(s.Blossom.Waypoints).Count);
        Assert.Equal(17, s.Blossom.LoopFrom);
    }

    [Fact]
    public void Normalize_NullBlock_GetsDefaults()
    {
        var s = new AppSettings { Blossom = null! };
        SettingsStore.Normalize(s);
        Assert.Equal("Blossom Garden", s.Blossom.Destination);
    }
}
