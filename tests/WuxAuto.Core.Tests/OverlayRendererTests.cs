using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>Overlays are drawn on BGRA frames: every pixel they touch must stay opaque (a 3-value scalar leaves alpha 0).</summary>
public class OverlayRendererTests
{
    [Fact]
    public void Draw_LeavesEveryPixelOpaque()
    {
        const int w = 300, h = 200;
        using var frame = new Mat(h, w, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(100, 100, 100, 255));

        var items = new[]
        {
            new OverlayItem(new PixelRect(50, 60, 120, 50), "Label text", OverlayColor.Green, (250, 150)),
            new OverlayItem(new PixelRect(10, 10, 40, 20), "x", OverlayColor.Yellow),
        };
        OverlayRenderer.Draw(frame, items, click: (200, 120));

        byte[] bytes = CvHelpers.Bytes(frame);
        int changed = 0;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            Assert.Equal(255, bytes[i + 3]);
            if (bytes[i] != 100 || bytes[i + 1] != 100 || bytes[i + 2] != 100)
                changed++;
        }

        Assert.True(changed > 500, "the overlays drew nothing");
    }
}
