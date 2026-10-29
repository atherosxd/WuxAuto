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
        Assert.Equal((19136, 18463), route[0]);
        Assert.Equal((13947, 14697), route[5]);
        Assert.Equal((14359, 14565), route[16]);
        Assert.Equal(6, b.LoopFrom);
        Assert.Equal("Blossom Garden", b.Destination);
        Assert.Equal(1, b.ListPage);
        Assert.Equal(3000, b.WaypointWaitMs);
        Assert.Equal("[S]Jaso Practitioner", b.TargetName);
        Assert.Equal(7, b.TurnTaps);
        Assert.Equal(100, b.TurnTapMs);
        // Live 2026-10-29: waypoints 2 and 4 lie just past a railing - the walk stopped 86 and 67 units short.
        Assert.Equal(100, b.WaypointTolerance);
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
    public void Nearest_PicksTheClosest()
    {
        var route = BlossomRoute.Parse(new AppSettings().Blossom.Waypoints);
        Assert.Equal(0, BlossomRoute.Nearest(route, 19148, 19062)); // the arrival point (user's frame)
        Assert.Equal(9, BlossomRoute.Nearest(route, 12990, 13190));
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
