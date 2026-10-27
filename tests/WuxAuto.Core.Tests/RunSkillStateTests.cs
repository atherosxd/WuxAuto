using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The town Run skill check with the icon in two slots (the user's 1273x1024 bar: x 664 and 858,
/// 30x30). The game rings only the slot that was pressed, so any ringed copy = on.
/// </summary>
public class RunSkillStateTests
{
    const double RingMin = 0.25;

    static RunSkillCopy Copy(int centerX, double ring, double score = 0.99, int centerY = 971) =>
        new(new TemplateMatch("runskill", score, new PixelRect(centerX - 15, centerY - 15, 30, 30), 0.90), ring);

    [Fact]
    public void NoHits_IsNull() => Assert.Null(RunSkillState.Decide([], RingMin));

    [Fact]
    public void RingOnTheRightCopyOnly_IsOn_PicksTheRingedCopy()
    {
        // 20261025_190008_028: the leftmost copy alone read "off" and would have been clicked.
        var run = RunSkillState.Decide([Copy(664, 0.00), Copy(858, 0.50)], RingMin)!;
        Assert.True(run.On);
        Assert.Equal(858, run.Pick.Icon.Bounds.CenterX);
        Assert.Equal(2, run.Copies.Count);
    }

    [Fact]
    public void RingOnTheLeftCopyOnly_IsOn_PicksTheRingedCopy()
    {
        var run = RunSkillState.Decide([Copy(858, 0.00), Copy(664, 0.53)], RingMin)!;
        Assert.True(run.On);
        Assert.Equal(664, run.Pick.Icon.Bounds.CenterX);
    }

    [Fact]
    public void NoRingOnAnyCopy_IsOff_ClicksTheLeftmostOfEqualScores()
    {
        var run = RunSkillState.Decide([Copy(858, 0.03), Copy(664, 0.00)], RingMin)!;
        Assert.False(run.On);
        Assert.Equal(664, run.Pick.Icon.Bounds.CenterX);
    }

    [Fact]
    public void NoRingOnAnyCopy_ClearlyBetterScoreBeatsLeftmost()
    {
        var run = RunSkillState.Decide([Copy(664, 0.00, score: 0.90), Copy(858, 0.00, score: 0.99)], RingMin)!;
        Assert.False(run.On);
        Assert.Equal(858, run.Pick.Icon.Bounds.CenterX);
    }

    [Fact]
    public void RingJustBelowTheMinimum_IsOff() =>
        Assert.False(RunSkillState.Decide([Copy(664, RingMin - 0.01)], RingMin)!.On);

    [Fact]
    public void SameSpotTwice_CountsOnce()
    {
        // 1920x1080 frames: the matcher returned 1190,1024 twice.
        var run = RunSkillState.Decide([Copy(1190, 0.53, 1.0, 1024), Copy(1190, 0.53, 1.0, 1024)], RingMin)!;
        Assert.Single(run.Copies);
        Assert.True(run.On);
    }
}
