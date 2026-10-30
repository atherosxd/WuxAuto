using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto Blossom's "Record route" (user, 2026-10-29: "I walk the road once by hand and WuxAuto saves the waypoints"): the
/// coordinate readings while the player walks become waypoints every <c>spacing</c> units; A / D presses become the camera
/// turns of the latest waypoint; "Loop starts here" marks the loop.
/// </summary>
public class RouteRecordingTests
{
    [Fact]
    public void Readings_BecomeWaypoints_EverySpacingUnits()
    {
        var r = new RouteRecording(spacing: 120);
        foreach (int x in new[] { 1000, 1050, 1100, 1130, 1200, 1260 })
            r.AddReading(x, 500);

        Assert.Equal(["1000,500", "1130,500", "1260,500"], r.WaypointStrings());
    }

    [Fact]
    public void OneMisreadJump_IsIgnored_TwoAgreeingReadsAreTaken()
    {
        var r = new RouteRecording(spacing: 120, jump: 400);
        r.AddReading(13521, 12757);
        r.AddReading(1352, 12757);     // a digit dropped: ignored
        r.AddReading(13525, 12900);    // fine again
        Assert.Equal(["13521,12757", "13525,12900"], r.WaypointStrings());

        r.AddReading(16000, 12900);    // a real teleport-sized move: the next read agrees
        r.AddReading(16010, 12905);
        Assert.Equal(3, r.Count);
        Assert.Equal("16010,12905", r.WaypointStrings()[^1]);
    }

    [Fact]
    public void Turns_AttachToTheLatestWaypoint_AndCancelOut()
    {
        var r = new RouteRecording();
        r.AddReading(1000, 1000);
        for (int i = 0; i < 5; i++)
            r.AddTurn(TurnDirection.Left);
        r.AddReading(1200, 1000);
        r.AddTurn(TurnDirection.Right);
        r.AddTurn(TurnDirection.Left);  // back again: no turn at waypoint 2
        r.AddReading(1400, 1000);
        r.AddTurn(TurnDirection.Right);
        r.AddTurn(TurnDirection.Right);

        Assert.Equal(["1:5A", "3:2D"], r.TurnStrings());
    }

    [Fact]
    public void TurnsBeforeTheFirstReading_AreDropped()
    {
        var r = new RouteRecording();
        r.AddTurn(TurnDirection.Right);
        r.AddReading(1000, 1000);
        Assert.Empty(r.TurnStrings());
    }

    [Fact]
    public void LoopStart_IsTheLatestWaypoint_OrTheCurrentSpotWhenFarFromIt()
    {
        var r = new RouteRecording(spacing: 120);
        r.AddReading(1000, 1000);
        r.AddReading(1130, 1000);
        r.AddReading(1150, 1000);          // 20 from waypoint 2: no new waypoint
        r.MarkLoopStart();
        Assert.Equal(2, r.LoopFrom);

        r.AddReading(1300, 1000);          // waypoint 3
        r.AddReading(1380, 1000);          // 80 from it
        r.MarkLoopStart();                 // >= 60 away: the spot itself becomes waypoint 4, the loop start
        Assert.Equal(4, r.LoopFrom);
        Assert.Equal("1380,1000", r.WaypointStrings()[3]);
    }

    [Fact]
    public void NoLoopMark_LoopsFromTheStart() => Assert.Equal(1, new RouteRecording().LoopFrom);

    [Fact]
    public void ClosingGap_IsFromTheLastWaypointToTheLoopStart()
    {
        // Live 2026-10-30: a first-island recording ended 5052 from its start - looping would cross the map in one line.
        var r = new RouteRecording();
        Assert.Equal(0, r.ClosingGap);
        r.AddReading(1000, 1000);
        r.AddReading(1300, 1000);
        r.AddReading(1600, 1000);
        Assert.Equal(600, r.ClosingGap, 3);
        r.AddReading(1300, 1000);   // walked back: 300 from the start
        Assert.Equal(300, r.ClosingGap, 3);
    }

    [Fact]
    public void WalkBack_OnlyWhenTheRouteEndsFarFromItsLoopStart()
    {
        // User, 2026-10-30: "no need to loop? instead from last waypoint go back in reverse" - for a route that does not close.
        var r = new RouteRecording(spacing: 120, jump: 400);
        r.AddReading(1000, 1000);
        r.AddReading(1300, 1000);
        r.AddReading(1500, 1000);
        Assert.True(r.WalkBack);    // 500 from the start
        r.AddReading(1300, 1100);
        r.AddReading(1100, 1100);
        Assert.False(r.WalkBack);   // came round: 100 from the start, a loop is fine
    }
}
