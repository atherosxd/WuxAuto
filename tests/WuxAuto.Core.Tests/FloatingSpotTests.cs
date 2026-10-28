using WuxAuto.Phone;

namespace WuxAuto.Core.Tests;

public class FloatingSpotTests
{
    [Fact]
    public void Size_IsAboutFivePercentOfTheShortSide()
    {
        Assert.Equal(54, FloatingSpot.SizeFor(1920, 1080));   // the note's limit at 1920x1080: 56 px
        Assert.Equal(60, FloatingSpot.SizeFor(2608, 1200));
        Assert.Equal(54, FloatingSpot.SizeFor(1080, 1920));   // portrait: the same short side
        Assert.Equal(FloatingSpot.MinSize, FloatingSpot.SizeFor(320, 200));
    }

    [Fact]
    public void DefaultSpot_SitsInTheTopStrip()
    {
        // The note's spot: centre (0.40, 0.03) = 768,32 at 1920x1080, inside x 640-1110, y 0-55.
        var (left, top) = FloatingSpot.TopLeft(0.40, 0.03, 1920, 1080, 54);

        Assert.Equal(768 - 27, left);
        Assert.Equal(5, top);
        Assert.InRange(left, 640, 1110 - 54);
        Assert.InRange(top + 54, 0, 60);
    }

    [Fact]
    public void TopLeft_KeepsTheButtonOnScreen()
    {
        Assert.Equal((0, 0), FloatingSpot.TopLeft(-1, -1, 1920, 1080, 54));
        Assert.Equal((1920 - 54, 1080 - 54), FloatingSpot.TopLeft(2, 2, 1920, 1080, 54));
    }

    [Fact]
    public void Snap_GoesToTheNearestEdge()
    {
        const int w = 1920, h = 1080, size = 54, half = 27, m = FloatingSpot.EdgeMargin;

        // Near the top: y snaps to the top, x stays.
        var top = FloatingSpot.Snap(800, 60, w, h, size);
        Assert.Equal(800 / (double)w, top.X, 6);
        Assert.Equal((m + half) / (double)h, top.Y, 6);

        // Near the left: x snaps to the left, y stays.
        var left = FloatingSpot.Snap(90, 530, w, h, size);
        Assert.Equal((m + half) / (double)w, left.X, 6);
        Assert.Equal(530 / (double)h, left.Y, 6);

        // Near the right and the bottom.
        Assert.Equal((w - m - half) / (double)w, FloatingSpot.Snap(1880, 500, w, h, size).X, 6);
        Assert.Equal((h - m - half) / (double)h, FloatingSpot.Snap(900, 1070, w, h, size).Y, 6);
    }

    [Fact]
    public void Snap_ClampsTheOtherAxis()
    {
        // Dropped half off the top-left corner: both axes end up on screen.
        var p = FloatingSpot.Snap(-30, 10, 1920, 1080, 54);
        var (left, top) = FloatingSpot.TopLeft(p.X, p.Y, 1920, 1080, 54);

        Assert.InRange(left, 0, 1920 - 54);
        Assert.InRange(top, 0, 1080 - 54);
    }

    [Fact]
    public void Snap_RoundTripsThroughTopLeft()
    {
        var p = FloatingSpot.Snap(768, 20, 1920, 1080, 54);
        var (left, top) = FloatingSpot.TopLeft(p.X, p.Y, 1920, 1080, 54);

        Assert.Equal(768 - 27, left);
        Assert.Equal(FloatingSpot.EdgeMargin, top);
    }
}
