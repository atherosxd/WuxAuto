using WuxAuto.Phone;

namespace WuxAuto.Core.Tests;

public class FrameGateTests
{
    static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

    [Fact]
    public void NoFreshFrame_PausesThePilot()
    {
        var gate = new FrameGate(1500);
        long now = Ms(100_000);

        Assert.False(gate.Fresh(now, 0));                   // capture never delivered a frame
        Assert.False(gate.Fresh(now, now - Ms(1501)));      // capture stopped: the last frame is old
        Assert.False(gate.Fresh(now, now - Ms(60_000)));
    }

    [Fact]
    public void FreshFrame_Ticks()
    {
        var gate = new FrameGate(1500);
        long now = Ms(100_000);

        Assert.True(gate.Fresh(now, now));
        Assert.True(gate.Fresh(now, now - Ms(200)));
        Assert.True(gate.Fresh(now, now - Ms(1500)));       // the limit itself still counts
    }

    [Fact]
    public void FrameFromTheFuture_IsNotFresh()
    {
        var gate = new FrameGate(1500);
        long now = Ms(100_000);

        Assert.False(gate.Fresh(now, now + Ms(10)));
    }

    [Fact]
    public void CaptureResumes_TicksAgain()
    {
        var gate = new FrameGate(1000);
        long stamp = Ms(50_000);

        Assert.False(gate.Fresh(stamp + Ms(5000), stamp));  // paused
        long newer = stamp + Ms(5000);
        Assert.True(gate.Fresh(newer + Ms(30), newer));     // a new frame arrives: ticking again
    }

    [Fact]
    public void NowTicks_IsMonotonicWholeMilliseconds()
    {
        long a = FrameGate.NowTicks();
        long b = FrameGate.NowTicks();

        Assert.True(b >= a);
        Assert.Equal(0, a % TimeSpan.TicksPerMillisecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveLimit_Throws(int staleMs) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameGate(staleMs));
}
