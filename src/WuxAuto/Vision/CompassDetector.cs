using System.Diagnostics;
using OpenCvSharp;
using WuxAuto.Core;

namespace WuxAuto.Vision;

/// <summary>
/// Works out where the player is facing inside "north" space (0° == the searchlight cone
/// pointing up the screen). Two independent strategies are available: rotated template
/// matching against flashlight.png, and an image-moment estimate of the light cone.
/// </summary>
/// <remarks>
/// Both strategies work on a thresholded (Otsu) copy of the region, so they describe the
/// <em>shape</em> of the beam rather than its brightness. That is what makes them survive the
/// arbitrary scenery behind the player. Masked template matching is deliberately avoided:
/// OpenCV's masked CCORR_NORMED divides by the image energy inside the mask, which turns dark
/// windows into NaN/Inf and poisons MinMaxLoc.
/// </remarks>
public sealed class CompassDetector : IDisposable
{
    private readonly TemplateLibrary _library;
    private readonly Dictionary<(int ScaleKey, int Angle), Mat> _rotations = new();

    public CompassDetector(TemplateLibrary library)
    {
        _library = library;
    }

    /// <summary>Debug hook: reports every (angle, score) evaluated by the template compass.</summary>
    public static Action<int, double>? TraceScores;

    public CompassReading? Detect(Mat frameBgra, PixelRect roi, double scale, DetectionSettings settings)
    {
        var area = roi.Clamp(frameBgra.Width, frameBgra.Height);
        if (area.Width < 24 || area.Height < 24)
            return null;

        using var gray = TemplateMatcher.ToGray(frameBgra, area);

        CompassReading? templateReading = null;
        CompassReading? brightnessReading = null;

        if (settings.CompassMode is CompassMode.Template or CompassMode.Auto)
            templateReading = DetectWithTemplate(gray, area, scale, settings);

        if (settings.CompassMode is CompassMode.Brightness or CompassMode.Auto)
            brightnessReading = DetectWithBrightness(gray, area);

        return settings.CompassMode switch
        {
            CompassMode.Template => templateReading,
            CompassMode.Brightness => brightnessReading,
            _ => ChooseAuto(templateReading, brightnessReading, settings),
        };
    }

    public (CompassReading? Template, CompassReading? Brightness) DetectBoth(Mat frameBgra, PixelRect roi, double scale, DetectionSettings settings)
    {
        var area = roi.Clamp(frameBgra.Width, frameBgra.Height);
        if (area.Width < 24 || area.Height < 24)
            return (null, null);

        using var gray = TemplateMatcher.ToGray(frameBgra, area);
        return (DetectWithTemplate(gray, area, scale, settings), DetectWithBrightness(gray, area));
    }

    private static CompassReading? ChooseAuto(CompassReading? template, CompassReading? brightness, DetectionSettings settings)
    {
        bool templateOk = template != null && template.Score >= settings.CompassThreshold;
        bool brightnessOk = brightness != null && brightness.Score >= 0.5;

        if (brightnessOk && !templateOk)
            return brightness;

        // When both are usable, prefer the template: it does not depend on the ROI centre
        // lining up with the player, which is the usual source of compass error.
        return templateOk ? template : brightness ?? template;
    }

    private CompassReading? DetectWithTemplate(Mat gray, PixelRect area, double scale, DetectionSettings settings)
    {
        if (!_library.TryGet("flashlight", out var template))
            return null;

        var stopwatch = Stopwatch.StartNew();

        // Compare shapes, not brightness levels: the beam sits on scenery we know nothing about.
        using var scene = new Mat();
        Cv2.Threshold(gray, scene, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        int coarse = Math.Clamp(settings.CompassSearchStepDeg, 2, 30);
        int refinement = Math.Max(1, coarse / 3);

        // The 24-way angle sweep runs on a half-resolution copy; only the winners are re-scored
        // at full resolution. That is roughly twice as fast for identical final accuracy.
        int coarseFactor = scene.Width >= 320 && scene.Height >= 320 ? 2 : 1;
        using var halfScene = new Mat();
        Mat searchScene = scene;
        double searchScale = scale;
        if (coarseFactor > 1)
        {
            Cv2.Resize(scene, halfScene, new Size(scene.Width / coarseFactor, scene.Height / coarseFactor), 0, 0, InterpolationFlags.Area);
            Cv2.Threshold(halfScene, halfScene, 127, 255, ThresholdTypes.Binary);
            searchScene = halfScene;
            searchScale = scale / coarseFactor;
        }

        double bestScore = double.MinValue;
        int bestAngle = 0;

        for (int angle = 0; angle < 360; angle += coarse)
        {
            var (score, _) = ScoreAngle(searchScene, area, template, searchScale, angle);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        double bestFullScore = double.MinValue;
        int bestFullAngle = bestAngle;
        PixelRect bestBounds = default;

        for (int offset = -coarse; offset <= coarse; offset += refinement)
        {
            int angle = ((bestAngle + offset) % 360 + 360) % 360;
            var (score, bounds) = ScoreAngle(scene, area, template, scale, angle);
            if (score > bestFullScore)
            {
                bestFullScore = score;
                bestFullAngle = angle;
                bestBounds = bounds;
            }
        }

        stopwatch.Stop();
        if (!double.IsFinite(bestFullScore))
            return null;

        return new CompassReading(Normalize(-bestFullAngle), Math.Clamp(bestFullScore, -1, 1), bestBounds, "template", stopwatch.Elapsed.TotalMilliseconds);
    }

    private (double Score, PixelRect Bounds) ScoreAngle(Mat scene, PixelRect area, TemplateImage template, double scale, int angle)
    {
        var rotated = GetRotation(template, scale, angle);
        if (rotated.Width >= scene.Width || rotated.Height >= scene.Height)
            return (double.MinValue, default);

        using var result = new Mat();
        Cv2.MatchTemplate(scene, rotated, result, TemplateMatchModes.CCorrNormed);
        Cv2.PatchNaNs(result, 0);
        Cv2.MinMaxLoc(result, out _, out double max, out _, out var location);

        TraceScores?.Invoke(angle, max);
        if (!double.IsFinite(max))
            return (double.MinValue, default);

        return (max, new PixelRect(area.X + location.X, area.Y + location.Y, rotated.Width, rotated.Height));
    }

    /// <summary>
    /// A binary, centred, padded copy of the beam at the requested angle. One entry per
    /// (scale, angle) pair is cached, which is what keeps the 24 candidate angles cheap.
    /// </summary>
    private Mat GetRotation(TemplateImage template, double scale, int angle)
    {
        int scaleKey = (int)Math.Round(Math.Clamp(scale, 0.2, 4.0) * 1000);
        int normalizedAngle = ((angle % 360) + 360) % 360;
        var key = (scaleKey, normalizedAngle);
        if (_rotations.TryGetValue(key, out var cached))
            return cached;

        int width = Math.Max(4, (int)Math.Round(template.Width * scale));
        int height = Math.Max(4, (int)Math.Round(template.Height * scale));

        using var resized = new Mat();
        Cv2.Resize(template.Gray, resized, new Size(width, height), 0, 0,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);

        using var icon = new Mat();
        Cv2.Threshold(resized, icon, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        if (Cv2.CountNonZero(icon) < resized.Total() * 0.005)
            icon.SetTo(Scalar.White);
        else
        {
            // Slightly fatten the icon so a soft-edged beam in the game still overlaps it.
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.Dilate(icon, icon, kernel);
        }

        int side = (int)Math.Ceiling(Math.Sqrt(width * (double)width + height * (double)height)) + 2;
        if (side % 2 != 0)
            side++;

        int padTop = (side - height) / 2;
        int padLeft = (side - width) / 2;

        using var padded = new Mat();
        Cv2.CopyMakeBorder(icon, padded, padTop, side - height - padTop, padLeft, side - width - padLeft,
            BorderTypes.Constant, Scalar.Black);

        var rotated = new Mat();
        if (normalizedAngle == 0)
        {
            padded.CopyTo(rotated);
        }
        else
        {
            var center = new Point2f(side / 2f, side / 2f);
            using var rotation = Cv2.GetRotationMatrix2D(center, normalizedAngle, 1.0);
            Cv2.WarpAffine(padded, rotated, rotation, new Size(side, side), InterpolationFlags.Nearest,
                BorderTypes.Constant, Scalar.Black);
            Cv2.Threshold(rotated, rotated, 127, 255, ThresholdTypes.Binary);
        }

        _rotations[key] = rotated;
        return rotated;
    }

    /// <summary>
    /// Fallback compass that needs no template. It assumes the searchlight is the brightest
    /// thing in the region and points from the light source (brightest core) towards the mass
    /// of the beam. Coarse — good to roughly 20° — so the template compass is preferred.
    /// </summary>
    private static CompassReading? DetectWithBrightness(Mat gray, PixelRect area)
    {
        var stopwatch = Stopwatch.StartNew();
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        var moments = Cv2.Moments(mask, binaryImage: true);
        if (moments.M00 < 25)
            return null;

        double cx = moments.M10 / moments.M00;
        double cy = moments.M01 / moments.M00;
        double mu20 = moments.Mu20 / moments.M00;
        double mu02 = moments.Mu02 / moments.M00;
        double mu11 = moments.Mu11 / moments.M00;

        double common = Math.Sqrt(Math.Max(0, 4 * mu11 * mu11 + (mu20 - mu02) * (mu20 - mu02)));
        double major = (mu20 + mu02 + common) / 2;
        double minor = Math.Max(1e-6, (mu20 + mu02 - common) / 2);
        double elongation = Math.Sqrt(Math.Max(0, major) / minor);

        // The light source is the brightest blob; the beam opens away from it.
        Cv2.MinMaxLoc(gray, out _, out double maxValue, out _, out var maxLocation);
        double coreX = maxLocation.X;
        double coreY = maxLocation.Y;
        if (maxValue > 0)
        {
            using var coreMask = new Mat();
            Cv2.Threshold(gray, coreMask, maxValue * 0.92, 255, ThresholdTypes.Binary);
            var coreMoments = Cv2.Moments(coreMask, binaryImage: true);
            if (coreMoments.M00 > 4)
            {
                coreX = coreMoments.M10 / coreMoments.M00;
                coreY = coreMoments.M01 / coreMoments.M00;
            }
        }

        double toBeamX = cx - coreX;
        double toBeamY = cy - coreY;
        if (Math.Abs(toBeamX) < 0.5 && Math.Abs(toBeamY) < 0.5)
            return null;

        // Screen-space angle: 0 = up the screen, positive = clockwise.
        double angle = Normalize(Math.Atan2(toBeamX, -toBeamY) * 180.0 / Math.PI);
        double confidence = Math.Clamp((elongation - 1.0) / 0.6, 0, 1);

        stopwatch.Stop();
        return new CompassReading(angle, confidence, new PixelRect(area.X, area.Y, area.Width, area.Height), "brightness", stopwatch.Elapsed.TotalMilliseconds);
    }

    internal static double Normalize(double degrees)
    {
        double value = degrees % 360.0;
        if (value > 180)
            value -= 360;
        if (value <= -180)
            value += 360;
        return value;
    }

    public void Dispose()
    {
        foreach (var template in _rotations.Values)
            template.Dispose();

        _rotations.Clear();
    }
}
