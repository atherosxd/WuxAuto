using System.IO;
using OpenCvSharp;
using WuxAuto.Core;

namespace WuxAuto.Vision;

public sealed class TemplateImage : IDisposable
{
    internal TemplateImage(string name, string path, Mat gray)
    {
        Name = name;
        Path = path;
        Gray = gray;
        Width = gray.Width;
        Height = gray.Height;
    }

    public string Name { get; }
    public string Path { get; }
    public Mat Gray { get; }
    public int Width { get; }
    public int Height { get; }

    public void Dispose() => Gray.Dispose();
}

/// <summary>
/// Loads the PNG references used for detection. Files are read from %LOCALAPPDATA%\WuxAuto\templates
/// first and then from the shipped Assets folder, so users can re-crop a reference (or add
/// gateopen.png) without rebuilding.
/// </summary>
public sealed class TemplateLibrary : IDisposable
{
    private readonly Dictionary<string, TemplateImage> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _searchPaths = new();

    public TemplateLibrary(params string[] extraSearchPaths)
    {
        // User templates come first so a re-cropped flashlight.png / mapicon.png in
        // %LOCALAPPDATA%\WuxAuto\templates overrides the shipped reference.
        var roots = new List<string>
        {
            AppPaths.TemplateFolder,
            AppPaths.AssetsFolder,
        };
        roots.AddRange(extraSearchPaths);
        _searchPaths.AddRange(roots.Where(Directory.Exists));
        Reload();
    }

    public IReadOnlyList<string> SearchPaths => _searchPaths;
    public IReadOnlyList<string> Names => _templates.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string name, out TemplateImage template) => _templates.TryGetValue(name, out template!);

    public TemplateImage? Get(string name) => _templates.TryGetValue(name, out var t) ? t : null;

    public void Reload()
    {
        foreach (var template in _templates.Values)
            template.Dispose();
        _templates.Clear();

        foreach (string root in _searchPaths)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.png", SearchOption.TopDirectoryOnly))
            {
                string key = Path.GetFileNameWithoutExtension(file);
                if (_templates.ContainsKey(key))
                    continue;

                var gray = LoadGray(file);
                if (gray == null)
                    continue;

                _templates[key] = new TemplateImage(key, file, gray);
            }
        }

        // The map's monster list is paged with tiny up/down arrows that are the same glyph as the
        // right chevron, just rotated. Generating them from rightchevron.png means there is no
        // extra image to keep in sync (a real upchevron.png / downchevron.png still wins if present).
        AddRotatedVariant("rightchevron", "downchevron", RotateFlags.Rotate90Clockwise);
        AddRotatedVariant("rightchevron", "upchevron", RotateFlags.Rotate90Counterclockwise);

        // The teleport list's "previous page" arrow: the right chevron mirrored.
        AddFlippedVariant("rightchevron", "leftchevron", FlipMode.Y);

        // "Facing west" searchlights: the straight-up beams turned a quarter to the left.
        foreach (string beam in new[] { "flashlight", "flashlight2", "flashlight3", "flashlightLOD" })
        {
            AddRotatedVariant(beam, beam + "west", RotateFlags.Rotate90Counterclockwise);
            AddRotatedVariant(beam, beam + "south", RotateFlags.Rotate180);
        }
    }

    /// <summary>
    /// Loads a template as grayscale. A PNG with transparent areas (flashlight3.png: 395x341 with
    /// only the beam and a stray mark opaque) is cropped to its largest opaque patch - matching
    /// ignores alpha, so the transparent canvas would otherwise be part of the template.
    /// </summary>
    private static Mat? LoadGray(string file)
    {
        using var raw = Cv2.ImRead(file, ImreadModes.Unchanged);
        if (raw.Empty())
            return null;

        if (raw.Channels() == 1)
            return raw.Clone();

        var gray = new Mat();
        Cv2.CvtColor(raw, gray, raw.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        if (raw.Channels() != 4)
            return gray;

        using var alpha = new Mat();
        Cv2.ExtractChannel(raw, alpha, 3);
        using var opaque = new Mat();
        Cv2.Threshold(alpha, opaque, 0, 255, ThresholdTypes.Binary);
        if (Cv2.CountNonZero(opaque) == opaque.Rows * opaque.Cols)
            return gray;

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(opaque, labels, stats, centroids);
        int best = 0, bestArea = 0;
        for (int i = 1; i < count; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }

        if (best == 0)
        {
            gray.Dispose();
            return null;
        }

        var box = new Rect(
            stats.At<int>(best, (int)ConnectedComponentsTypes.Left),
            stats.At<int>(best, (int)ConnectedComponentsTypes.Top),
            stats.At<int>(best, (int)ConnectedComponentsTypes.Width),
            stats.At<int>(best, (int)ConnectedComponentsTypes.Height));
        var cropped = new Mat(gray, box).Clone();
        gray.Dispose();
        return cropped;
    }

    private void AddFlippedVariant(string sourceName, string targetName, FlipMode mode)
    {
        if (_templates.ContainsKey(targetName) || !_templates.TryGetValue(sourceName, out var source))
            return;

        var flipped = new Mat();
        Cv2.Flip(source.Gray, flipped, mode);
        _templates[targetName] = new TemplateImage(targetName, source.Path, flipped);
    }

    private void AddRotatedVariant(string sourceName, string targetName, RotateFlags flags)
    {
        if (_templates.ContainsKey(targetName))
            return;

        if (!_templates.TryGetValue(sourceName, out var source))
            return;

        var rotated = new Mat();
        Cv2.Rotate(source.Gray, rotated, flags);
        _templates[targetName] = new TemplateImage(targetName, source.Path, rotated);
    }

    public void Dispose()
    {
        foreach (var template in _templates.Values)
            template.Dispose();
        _templates.Clear();
    }
}
