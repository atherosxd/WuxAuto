using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto Blossom's "lined up" check: the shipped pillar base template pasted twice onto a plain frame. The camera looks at
/// the middle between the two pillars when lined up (user, 2026-10-29).
/// </summary>
[Collection("AppPaths")]
public class PillarPairTests : AppPathsScope
{
    private const int Width = 1440, Height = 900, BaseY = 200;

    [Fact]
    public void FindPillarPair_TwoBases_Centred()
    {
        var pair = Find(560, 860);
        Assert.NotNull(pair);
        Assert.True(Math.Abs(pair.OffsetFraction) < 0.01, $"offset {pair.OffsetFraction:0.000}");
        Assert.True(pair.Left.Bounds.CenterX < pair.Right.Bounds.CenterX);
    }

    [Fact]
    public void FindPillarPair_OffCentre_SignedOffset()
    {
        var pair = Find(700, 1000);
        Assert.NotNull(pair);
        Assert.InRange(pair.OffsetFraction, 0.080, 0.100); // midpoint 850 of 1440: +0.090
    }

    [Fact]
    public void FindPillarPair_OneBase_IsNotAPair() => Assert.Null(Find(560));

    [Fact]
    public void FindPillarPair_TooClose_IsNotAPair() => Assert.Null(Find(600, 700));

    [Fact]
    public void FindPillarPair_NotOnOneRow_IsNotAPair() => Assert.Null(Find((560, BaseY), (860, BaseY + 120)));

    private PillarPair? Find(params int[] centresX) => Find(centresX.Select(x => (x, BaseY)).ToArray());

    private PillarPair? Find(params (int X, int Y)[] centres)
    {
        AppPaths.Configure(TempRoot(), Path.Combine(AppContext.BaseDirectory, "Assets"));
        var settings = new AppSettings();
        using var analyzer = new ScreenAnalyzer(settings, new FakeOcrEngine());
        using var canvas = new Mat(Height, Width, DepthType.Cv8U, 4);
        canvas.SetTo(new MCvScalar(70, 110, 90, 255));

        using var png = CvInvoke.Imread(Path.Combine(AppContext.BaseDirectory, "Assets", "blossompillar.png"), ImreadModes.ColorBgr);
        using var tile = new Mat();
        CvInvoke.CvtColor(png, tile, ColorConversion.Bgr2Bgra);
        foreach (var (x, y) in centres)
        {
            using var target = new Mat(canvas, new Rectangle(x - tile.Width / 2, y - tile.Height / 2, tile.Width, tile.Height));
            tile.CopyTo(target);
        }

        return analyzer.FindPillarPair(canvas, settings.Blossom.PillarTemplate, settings.Blossom.PillarMatchScore);
    }
}
