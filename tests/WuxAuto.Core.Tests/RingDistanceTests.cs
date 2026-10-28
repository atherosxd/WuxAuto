using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// ScreenAnalyzer.DistanceFromRing on the ring boxes FindBossRing read from six saved 1920x1080
/// lod_approach snapshots (2026-10-24), each checked by eye: the character's feet are at (960, 583).
/// </summary>
public class RingDistanceTests
{
    [Theory]
    [InlineData(825, 466, 296, 148, 0.59)] // whole ring, character on it
    [InlineData(700, 473, 382, 300, 0.45)] // whole ring, character inside
    [InlineData(837, 576, 232, 67, 0.07)]  // flat arc: measured from the whole ring's centre
    [InlineData(893, 608, 160, 50, 0.89)]  // character under the boss, only the front arc shows (2.0 from the arc's own centre)
    [InlineData(665, 456, 241, 121, 1.82)] // character beside the ring (right of it)
    [InlineData(951, 436, 220, 102, 1.98)] // character below the ring
    public void SavedFrames(int x, int y, int width, int height, double expected)
    {
        double away = ScreenAnalyzer.DistanceFromRing(new PixelRect(x, y, width, height), 1920, 1080);
        Assert.Equal(expected, away, 2);
    }

    [Fact]
    public void DefaultRingStayMax_KeepsTheCharacterUnderTheBoss_AndSendsTheOneBesideIt()
    {
        double stay = new AppSettings().Lod.RingStayMax;
        Assert.True(ScreenAnalyzer.DistanceFromRing(new PixelRect(893, 608, 160, 50), 1920, 1080) <= stay);
        Assert.True(ScreenAnalyzer.DistanceFromRing(new PixelRect(665, 456, 241, 121), 1920, 1080) > stay);
    }
}
