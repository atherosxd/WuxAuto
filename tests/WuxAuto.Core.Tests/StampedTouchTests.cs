using WuxAuto.Phone;

namespace WuxAuto.Core.Tests;

public class StampedTouchTests
{
    private sealed class Inner : ITouchInput
    {
        public bool Result = true;
        public int Releases;
        public bool IsDryRun => false;
        public bool IsGameInFront => true;
        public bool BringGameToFront() => true;
        public bool Tap(int x, int y, int holdMs) => Result;
        public bool Swipe(int x1, int y1, int x2, int y2, int durationMs) => Result;
        public bool Drag(int x1, int y1, int x2, int y2, int durationMs, int endHoldMs) => Result;
        public bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs) => Result;
        public bool Joystick(int cx, int cy, int dx, int dy, int durationMs) => Result;
        public bool PressDown(int x, int y) => Result;
        public void Release() => Releases++;
    }

    [Fact]
    public void EveryGesture_StampsItsStart()
    {
        long now = 100;
        var touch = new StampedTouch(new Inner(), () => now);

        Assert.Equal(0, touch.LastSentTicks);
        Action[] gestures =
        [
            () => touch.Tap(1, 2, 60), () => touch.Swipe(1, 2, 3, 4, 100), () => touch.Drag(1, 2, 3, 4, 100, 100),
            () => touch.Pinch(1, 2, 720, 192, 400), () => touch.Joystick(1, 2, 3, 4, 900), () => touch.PressDown(1, 2),
        ];
        foreach (var gesture in gestures)
        {
            now += 10;
            gesture();
            Assert.Equal(now, touch.LastSentTicks);
        }
    }

    [Fact]
    public void RefusedGesture_AndRelease_DoNotStamp()
    {
        long now = 500;
        var inner = new Inner { Result = false };
        var touch = new StampedTouch(inner, () => now);

        touch.Tap(1, 2, 60);
        touch.Release();

        Assert.Equal(0, touch.LastSentTicks);
        Assert.Equal(1, inner.Releases);
    }

    [Fact]
    public void Reset_Forgets()
    {
        var touch = new StampedTouch(new Inner(), () => 42);
        touch.Tap(1, 2, 60);
        touch.Reset();
        Assert.Equal(0, touch.LastSentTicks);
    }
}
