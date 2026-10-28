// FROZEN COPY for the Emgu parity probe: the pre-port OpenCvSharp vision code. Do not edit.
using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Legacy;

/// <summary>
/// Normalized template matching on top of OpenCV. Scaled template copies are cached, and large
/// search areas are first scanned at half/third resolution before the winners are re-scored at
/// full resolution — an exact match on a 1080p frame otherwise costs 50 ms, which is far too much
/// for a per-frame detection loop.
/// </summary>
public sealed class TemplateMatcher : IDisposable
{
    private readonly TemplateLibrary _library;
    private readonly Dictionary<(string Name, int ScaleKey), Mat> _scaledCache = new();

    public TemplateMatcher(TemplateLibrary library)
    {
        _library = library;
    }

    /// <summary>Template pixel scale for a frame, assuming the PNGs were captured at 1080p.</summary>
    public static double ScaleForFrame(int frameHeight, DetectionSettings settings)
    {
        double scale = settings.TemplateScale;
        if (settings.AutoScaleTemplates && frameHeight > 0)
            scale *= frameHeight / 1080.0;
        return Math.Clamp(scale, 0.2, 4.0);
    }

    public IReadOnlyList<TemplateMatch> Find(
        Mat frameBgra,
        string templateName,
        PixelRect roi,
        double scale,
        double threshold,
        int maxResults = 6,
        int suppressionRadius = 0)
    {
        if (!_library.TryGet(templateName, out var template))
            return Array.Empty<TemplateMatch>();

        var area = roi.Clamp(frameBgra.Width, frameBgra.Height);
        if (area.Width < 8 || area.Height < 8)
            return Array.Empty<TemplateMatch>();

        using var gray = ToGray(frameBgra, area);
        using var full = GetScaled(template, scale);

        if (full.Width >= gray.Width || full.Height >= gray.Height)
            return Array.Empty<TemplateMatch>();

        int factor = CoarseFactor(area, full);
        if (factor <= 1)
            return Scan(gray, full, area, templateName, scale, threshold, maxResults, suppressionRadius);

        // Coarse pass: the same search at 1/factor resolution, which is f² times cheaper.
        using var smallGray = new Mat();
        Cv2.Resize(gray, smallGray,
            new Size(Math.Max(8, gray.Width / factor), Math.Max(8, gray.Height / factor)),
            0, 0, InterpolationFlags.Area);

        using var smallTemplate = GetScaled(template, scale / factor);
        if (smallTemplate.Width >= smallGray.Width || smallTemplate.Height >= smallGray.Height)
            return Scan(gray, full, area, templateName, scale, threshold, maxResults, suppressionRadius);

        var coarse = Scan(
            smallGray,
            smallTemplate,
            new PixelRect(0, 0, smallGray.Width, smallGray.Height),
            templateName,
            scale,
            Math.Max(0.45, threshold - 0.15),
            maxResults,
            Math.Max(2, suppressionRadius / factor));

        // Fine pass: re-score each candidate on a small full-resolution crop around it.
        var refined = new List<TemplateMatch>(coarse.Count);
        foreach (var candidate in coarse)
        {
            var match = Refine(gray, full, area, templateName, scale, factor, candidate.Bounds.CenterX * factor, candidate.Bounds.CenterY * factor);
            if (match != null && match.Score >= threshold)
                refined.Add(match);
        }

        return refined
            .OrderByDescending(m => m.Score)
            .Take(maxResults)
            .ToList();
    }

    public TemplateMatch? FindBest(Mat frameBgra, string templateName, PixelRect roi, double scale, double threshold)
    {
        var matches = Find(frameBgra, templateName, roi, scale, threshold, maxResults: 1);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>Scans a range of scales and returns every hit above the threshold, best first.</summary>
    public IReadOnlyList<TemplateMatch> FindMultiScale(
        Mat frameBgra,
        string templateName,
        PixelRect roi,
        double baseScale,
        double threshold,
        double minScaleFactor = 0.85,
        double maxScaleFactor = 1.15,
        double scaleStep = 0.05,
        int maxResults = 6)
    {
        var all = new List<TemplateMatch>();
        for (double factor = minScaleFactor; factor <= maxScaleFactor + 1e-9; factor += scaleStep)
        {
            all.AddRange(Find(frameBgra, templateName, roi, baseScale * factor, threshold, maxResults));
        }

        // The same object is found once per scale; keep only the best hit per location.
        var unique = new List<TemplateMatch>();
        foreach (var match in all.OrderByDescending(m => m.Score))
        {
            if (!unique.Any(seen => SameSpot(seen, match)))
                unique.Add(match);

            if (unique.Count >= maxResults)
                break;
        }

        return unique;
    }

    private static bool SameSpot(TemplateMatch a, TemplateMatch b)
    {
        int radiusX = Math.Max(5, Math.Min(a.Bounds.Width, b.Bounds.Width) / 2);
        int radiusY = Math.Max(5, Math.Min(a.Bounds.Height, b.Bounds.Height) / 2);
        return Math.Abs(a.Bounds.CenterX - b.Bounds.CenterX) <= radiusX &&
               Math.Abs(a.Bounds.CenterY - b.Bounds.CenterY) <= radiusY;
    }

    /// <summary>Picks the match closest to a previous point (used for chevrons next to a list row).</summary>
    public static TemplateMatch? Closest(IReadOnlyList<TemplateMatch> matches, int x, int y)
    {
        TemplateMatch? best = null;
        double bestDistance = double.MaxValue;
        foreach (var match in matches)
        {
            double distance = match.Bounds.DistanceTo(x, y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = match;
            }
        }

        return best;
    }

    public static Mat ToGray(Mat frame, PixelRect area)
    {
        using var view = new Mat(frame, new Rect(area.X, area.Y, area.Width, area.Height));
        var gray = new Mat();
        if (frame.Channels() == 4)
            Cv2.CvtColor(view, gray, ColorConversionCodes.BGRA2GRAY);
        else if (frame.Channels() == 3)
            Cv2.CvtColor(view, gray, ColorConversionCodes.BGR2GRAY);
        else
            view.CopyTo(gray);
        return gray;
    }

    /// <summary>Full-resolution matchTemplate inside <paramref name="area"/>; results are absolute.</summary>
    private static IReadOnlyList<TemplateMatch> Scan(
        Mat gray,
        Mat templ,
        PixelRect area,
        string name,
        double scale,
        double threshold,
        int maxResults,
        int suppressionRadius)
    {
        if (templ.Width >= gray.Width || templ.Height >= gray.Height)
            return Array.Empty<TemplateMatch>();

        using var result = new Mat();
        Cv2.MatchTemplate(gray, templ, result, TemplateMatchModes.CCoeffNormed);

        var matches = new List<TemplateMatch>();
        int radius = suppressionRadius > 0
            ? suppressionRadius
            : Math.Max(4, Math.Min(templ.Width, templ.Height) / 2);

        for (int i = 0; i < maxResults; i++)
        {
            Cv2.MinMaxLoc(result, out _, out double max, out _, out var location);
            if (!double.IsFinite(max) || max < threshold)
                break;

            matches.Add(new TemplateMatch(
                name,
                Math.Min(1.0, max),
                new PixelRect(area.X + location.X, area.Y + location.Y, templ.Width, templ.Height),
                scale));

            int x0 = Math.Max(0, location.X - radius);
            int y0 = Math.Max(0, location.Y - radius);
            int x1 = Math.Min(result.Width - 1, location.X + radius);
            int y1 = Math.Min(result.Height - 1, location.Y + radius);
            Cv2.Rectangle(result, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), new Scalar(-1), -1);
        }

        return matches;
    }

    private TemplateMatch? Refine(
        Mat gray,
        Mat templ,
        PixelRect area,
        string name,
        double scale,
        int factor,
        int centerX,
        int centerY)
    {
        int pad = factor * 2 + 2;
        int x0 = Math.Max(0, centerX - templ.Width / 2 - pad);
        int y0 = Math.Max(0, centerY - templ.Height / 2 - pad);
        int width = Math.Min(gray.Width - x0, templ.Width + pad * 2);
        int height = Math.Min(gray.Height - y0, templ.Height + pad * 2);
        if (width < templ.Width || height < templ.Height)
            return null;

        using var crop = new Mat(gray, new Rect(x0, y0, width, height));
        using var result = new Mat();
        Cv2.MatchTemplate(crop, templ, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double max, out _, out var location);
        if (!double.IsFinite(max))
            return null;

        return new TemplateMatch(
            name,
            Math.Min(1.0, max),
            new PixelRect(area.X + x0 + location.X, area.Y + y0 + location.Y, templ.Width, templ.Height),
            scale);
    }

    /// <summary>
    /// Big areas are scanned at reduced resolution, but tiny templates lose too much detail to be
    /// recognised there, so they keep the full-resolution path.
    /// </summary>
    private static int CoarseFactor(PixelRect area, Mat templ)
    {
        int smallestSide = Math.Min(templ.Width, templ.Height);
        if (area.Width < 640 || smallestSide < 14)
            return 1;
        if (area.Width >= 1400 && smallestSide >= 30)
            return 3;
        return 2;
    }

    private Mat GetScaled(TemplateImage template, double scale)
    {
        int key = (int)Math.Round(Math.Clamp(scale, 0.2, 4.0) * 1000);
        var cacheKey = (template.Name, key);
        if (_scaledCache.TryGetValue(cacheKey, out var cached))
            return cached.Clone();

        int width = Math.Max(2, (int)Math.Round(template.Width * scale));
        int height = Math.Max(2, (int)Math.Round(template.Height * scale));
        var resized = new Mat();
        Cv2.Resize(template.Gray, resized, new Size(width, height), 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
        _scaledCache[cacheKey] = resized;
        return resized.Clone();
    }

    public void Dispose()
    {
        foreach (var mat in _scaledCache.Values)
            mat.Dispose();
        _scaledCache.Clear();
    }

    /// <summary>Drops cached scaled copies (call after the template images were reloaded).</summary>
    public void ClearCache() => Dispose();
}
