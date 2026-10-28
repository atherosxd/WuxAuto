using WuxAuto.Phone;

namespace WuxAuto.Core.Tests;

public class FloatingSpotTests
{
    // The clear top strip at 1920x1080: x 640-1110, y 0-55; the button is 54 px.
    const int W = 1920, H = 1080, Size = 54;

    static void AssertInsideTheStrip(int left, int top, int size = Size)
    {
        Assert.InRange(left, 640, 1110 - size);
        Assert.InRange(top, 0, 55 - size);
    }

    [Fact]
    public void Size_IsAboutFivePercentOfTheShortSide()
    {
        Assert.Equal(54, FloatingSpot.SizeFor(1920, 1080));   // fits the 55 px strip
        Assert.Equal(60, FloatingSpot.SizeFor(2608, 1200));
        Assert.Equal(54, FloatingSpot.SizeFor(1080, 1920));   // portrait: the same short side
        Assert.Equal(FloatingSpot.MinSize, FloatingSpot.SizeFor(320, 200));
    }

    [Fact]
    public void DefaultSpot_SitsInTheTopStrip()
    {
        // The note's spot: centre (0.40, 0.03) = 768,32 - moved up just enough to keep the whole button above y 55.
        var (left, top) = FloatingSpot.TopLeft(0.40, 0.03, W, H, Size);

        Assert.Equal(768 - 27, left);
        AssertInsideTheStrip(left, top);
    }

    [Theory]
    [InlineData(900, 300)]    // the world view
    [InlineData(120, 540)]    // the left edge (too narrow for the button)
    [InlineData(1800, 1000)]  // the bottom-right skill cluster
    [InlineData(1700, 80)]    // the minimap
    [InlineData(1188, 28)]    // ☰
    [InlineData(300, 20)]     // the level / HP block
    [InlineData(-50, -50)]    // off screen
    public void DropOutsideTheStrip_RestsInsideIt(int x, int y)
    {
        var (fx, fy) = FloatingSpot.Snap(x, y, W, H, Size);
        var (left, top) = FloatingSpot.TopLeft(fx, fy, W, H, Size);

        AssertInsideTheStrip(left, top);
    }

    [Fact]
    public void Drop_GoesToTheNearestSpot()
    {
        // Below the strip at x 900: straight up, x kept.
        var (fx, _) = FloatingSpot.Snap(900, 300, W, H, Size);
        Assert.Equal(900.0 / W, fx, 6);

        // Left of the strip: its left end.
        var (lx, _) = FloatingSpot.Snap(100, 20, W, H, Size);
        Assert.Equal((640 + 27.0) / W, lx, 6);

        // Right of it (☰): its right end.
        var (rx, _) = FloatingSpot.Snap(1500, 20, W, H, Size);
        Assert.Equal((1110 - 27.0) / W, rx, 6);
    }

    [Fact]
    public void DropInsideTheStrip_StaysPut()
    {
        var (fx, fy) = FloatingSpot.Snap(800, 27, W, H, Size);

        Assert.Equal(800.0 / W, fx, 6);
        Assert.Equal(27.0 / H, fy, 6);
    }

    [Fact]
    public void SavedSpotOutsideEveryBand_IsLaidOutInside()
    {
        // A spot saved by the first version (bottom-right edge snap).
        var (left, top) = FloatingSpot.TopLeft(0.938, 0.971, W, H, Size);

        AssertInsideTheStrip(left, top);
    }

    [Fact]
    public void OtherScreen_TheStripScalesWithTheHeight_AroundTheCentre()
    {
        // 2608x1200 (Xiaomi, not checked live): the strip is 1200/1080 bigger and centred like the HUD's top strip.
        int size = FloatingSpot.SizeFor(2608, 1200);
        var band = FloatingSpot.BandsFor(2608, 1200)[0];
        var (fx, fy) = FloatingSpot.Snap(2000, 600, 2608, 1200, size);
        var (left, top) = FloatingSpot.TopLeft(fx, fy, 2608, 1200, size);

        Assert.Equal(61, band.Height);
        Assert.InRange(left, band.X, band.Right - size);
        Assert.InRange(top, band.Y, band.Bottom - size);
    }
}
