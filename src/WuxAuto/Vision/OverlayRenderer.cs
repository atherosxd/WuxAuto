using System.IO;
using System.Numerics;
using OpenCvSharp;
using WuxAuto.Core;

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
        Cv2.Resize(gray, small, new Size(8, 8), 0, 0, InterpolationFlags.Area);

        double sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
                sum += small.At<byte>(y, x);
        }

        double mean = sum / 64.0;
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (small.At<byte>(y, x) > mean)
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
    public static Scalar Color(OverlayColor color) => color switch
    {
        OverlayColor.Green => new Scalar(80, 220, 90),
        OverlayColor.Orange => new Scalar(60, 170, 255),
        OverlayColor.Blue => new Scalar(255, 170, 70),
        OverlayColor.Red => new Scalar(70, 70, 240),
        OverlayColor.Yellow => new Scalar(60, 215, 245),
        _ => new Scalar(180, 180, 180),
    };

    public static void Draw(Mat bgra, IEnumerable<OverlayItem> overlays, (int X, int Y)? click = null, double scale = 1.0)
    {
        foreach (var overlay in overlays)
        {
            if (overlay.Color == OverlayColor.None)
                continue;

            var color = Color(overlay.Color);
            var bounds = Scale(overlay.Bounds, scale);
            var rect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            Cv2.Rectangle(bgra, rect, color, 2, LineTypes.AntiAlias);

            if (overlay.LineTo.HasValue)
            {
                Cv2.Line(
                    bgra,
                    new Point(bounds.CenterX, bounds.CenterY),
                    new Point((int)Math.Round(overlay.LineTo.Value.X * scale), (int)Math.Round(overlay.LineTo.Value.Y * scale)),
                    color,
                    3,
                    LineTypes.AntiAlias);
            }

            if (!string.IsNullOrEmpty(overlay.Label))
            {
                const double fontScale = 0.55;
                var size = Cv2.GetTextSize(overlay.Label, HersheyFonts.HersheySimplex, fontScale, 1, out _);
                int textY = Math.Max(size.Height + 4, rect.Y - 4);
                var boxRect = new Rect(rect.X, textY - size.Height - 4, size.Width + 8, size.Height + 6);
                using var roi = new Mat(bgra, ClampRect(boxRect, bgra.Width, bgra.Height));
                roi.SetTo(new Scalar(20, 20, 20));
                Cv2.PutText(bgra, overlay.Label, new Point(rect.X + 4, textY - 3), HersheyFonts.HersheySimplex, fontScale, color, 1, LineTypes.AntiAlias);
            }
        }

        if (click.HasValue)
        {
            var cross = Color(OverlayColor.Red);
            var point = new Point((int)Math.Round(click.Value.X * scale), (int)Math.Round(click.Value.Y * scale));
            Cv2.DrawMarker(bgra, point, cross, MarkerTypes.Cross, 18, 2, LineTypes.AntiAlias);
            Cv2.Circle(bgra, point, 14, cross, 2, LineTypes.AntiAlias);
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

    private static Rect ClampRect(Rect rect, int width, int height)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        int w = Math.Clamp(rect.Width, 1, width - x);
        int h = Math.Clamp(rect.Height, 1, height - y);
        return new Rect(x, y, w, h);
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
            Cv2.ImWrite(file, copy);
            return file;
        }
        catch
        {
            return string.Empty;
        }
    }
}
