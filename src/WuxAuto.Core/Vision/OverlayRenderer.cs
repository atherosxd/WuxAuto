using System.IO;
using System.Numerics;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Core;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace WuxAuto.Vision;

/// <summary>Tiny average-hash signature used to notice that the screen finished loading.</summary>
public static class FrameSignature
{
    public static ulong Compute(Mat frameBgra, PixelRect roi)
    {
        var area = roi.Clamp(frameBgra.Width, frameBgra.Height);
        if (area.Width < 8 || area.Height < 8)
            return 0;

        using var gray = TemplateMatcher.ToGray(frameBgra, area);
        using var small = new Mat();
        CvInvoke.Resize(gray, small, new Size(8, 8), 0, 0, Inter.Area);
        byte[] pixels = CvHelpers.Bytes(small);

        double sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
                sum += pixels[y * 8 + x];
        }

        double mean = sum / 64.0;
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (pixels[y * 8 + x] > mean)
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

public static class OverlayRenderer
{
    // Alpha 255 on purpose: the frames are BGRA and a 3-value scalar leaves the 4th channel 0, so every
    // drawn pixel came out fully transparent (a PNG viewer showed the label boxes white, the preview
    // bitmap is Bgra32). OpenCvSharp 4.13 did the same; opaque is what was always meant.
    public static MCvScalar Color(OverlayColor color) => color switch
    {
        OverlayColor.Green => new MCvScalar(80, 220, 90, 255),
        OverlayColor.Orange => new MCvScalar(60, 170, 255, 255),
        OverlayColor.Blue => new MCvScalar(255, 170, 70, 255),
        OverlayColor.Red => new MCvScalar(70, 70, 240, 255),
        OverlayColor.Yellow => new MCvScalar(60, 215, 245, 255),
        _ => new MCvScalar(180, 180, 180, 255),
    };

    public static void Draw(Mat bgra, IEnumerable<OverlayItem> overlays, (int X, int Y)? click = null, double scale = 1.0)
    {
        if (bgra.NumberOfChannels != 4)
        {
            DrawOn(bgra, overlays, click, scale);
            return;
        }

        // OpenCV 5 blends antialiased text/lines into a 4-channel image per channel, alpha included, so the
        // edge pixels came out half transparent. Draw on a 3-channel copy and convert back: alpha 255 throughout
        // (the frames are opaque screen captures).
        using var bgr = new Mat();
        CvInvoke.CvtColor(bgra, bgr, ColorConversion.Bgra2Bgr);
        DrawOn(bgr, overlays, click, scale);
        CvInvoke.CvtColor(bgr, bgra, ColorConversion.Bgr2Bgra);
    }

    private static void DrawOn(Mat bgra, IEnumerable<OverlayItem> overlays, (int X, int Y)? click, double scale)
    {
        foreach (var overlay in overlays)
        {
            if (overlay.Color == OverlayColor.None)
                continue;

            var color = Color(overlay.Color);
            var bounds = Scale(overlay.Bounds, scale);
            var rect = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            CvHelpers.DrawRectangle(bgra, rect, color, 2, LineType.AntiAlias);

            if (overlay.LineTo.HasValue)
            {
                CvInvoke.Line(
                    bgra,
                    new Point(bounds.CenterX, bounds.CenterY),
                    new Point((int)Math.Round(overlay.LineTo.Value.X * scale), (int)Math.Round(overlay.LineTo.Value.Y * scale)),
                    color,
                    3,
                    LineType.AntiAlias);
            }

            if (!string.IsNullOrEmpty(overlay.Label))
            {
                const double fontScale = 0.55;
                int baseline = 0;
                var size = CvInvoke.GetTextSize(overlay.Label, HersheyFonts.Simplex, fontScale, 1, ref baseline);
                int textY = Math.Max(size.Height + 4, rect.Y - 4);
                var boxRect = new Rectangle(rect.X, textY - size.Height - 4, size.Width + 8, size.Height + 6);
                using var roi = new Mat(bgra, ClampRect(boxRect, bgra.Width, bgra.Height));
                roi.SetTo(new MCvScalar(20, 20, 20, 255));
                CvInvoke.PutText(bgra, overlay.Label, new Point(rect.X + 4, textY - 3), HersheyFonts.Simplex, fontScale, color, 1, LineType.AntiAlias);
            }
        }

        if (click.HasValue)
        {
            var cross = Color(OverlayColor.Red);
            var point = new Point((int)Math.Round(click.Value.X * scale), (int)Math.Round(click.Value.Y * scale));
            CvInvoke.DrawMarker(bgra, point, cross, MarkerTypes.Cross, 18, 2, LineType.AntiAlias);
            CvInvoke.Circle(bgra, point, 14, cross, 2, LineType.AntiAlias);
        }
    }

    private static PixelRect Scale(PixelRect rect, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.001)
            return rect;

        return new PixelRect(
            (int)Math.Round(rect.X * scale),
            (int)Math.Round(rect.Y * scale),
            Math.Max(3, (int)Math.Round(rect.Width * scale)),
            Math.Max(3, (int)Math.Round(rect.Height * scale)));
    }

    private static Rectangle ClampRect(Rectangle rect, int width, int height)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        int w = Math.Clamp(rect.Width, 1, width - x);
        int h = Math.Clamp(rect.Height, 1, height - y);
        return new Rectangle(x, y, w, h);
    }
}

public static class DebugArtifacts
{
    public static string SaveFrame(Mat frameBgra, IEnumerable<OverlayItem> overlays, string tag, (int X, int Y)? click = null)
    {
        try
        {
            AppPaths.EnsureCreated();
            using var copy = frameBgra.Clone();
            OverlayRenderer.Draw(copy, overlays, click);

            string safeTag = string.Concat(tag.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            string file = Path.Combine(AppPaths.DebugFolder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safeTag}.png");
            ImageFile.Write(file, copy);
            return file;
        }
        catch
        {
            return string.Empty;
        }
    }
}
