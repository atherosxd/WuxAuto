using System.Drawing;
using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
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
        AddRotatedVariant("rightchevron", "upchevron", RotateFlags.Rotate90CounterClockwise);

        // The teleport list's "previous page" arrow: the right chevron mirrored.
        AddFlippedVariant("rightchevron", "leftchevron", FlipType.Horizontal);

        // "Facing west" searchlights: the straight-up beams turned a quarter to the left.
        foreach (string beam in new[] { "flashlight", "flashlight2", "flashlight3", "flashlightLOD" })
        {
            AddRotatedVariant(beam, beam + "west", RotateFlags.Rotate90CounterClockwise);
            AddRotatedVariant(beam, beam + "south", RotateFlags.Rotate180);
        }

        // The phone's attack skill icons (hotbar crops) as the skill wheel draws them: the same picture x1.66, cut to a
        // circle ~115 px across (measured on skillwheel_slot1: NCC 0.998 at x1.66, centred). The centre square that stays
        // inside the circle, scaled up, is the wheel's template - the square crop with its corners scores ~0.58 there.
        foreach (string name in _templates.Keys.Where(IsPhoneAttack).ToList())
            AddWheelVariant(name, name + WheelSuffix);
    }

    /// <summary>The suffix of a phone attack icon's skill-wheel variant ("phone_attack1" → "phone_attack1wheel").</summary>
    public const string WheelSuffix = "wheel";

    /// <summary>The wheel draws a hotbar icon this much bigger.</summary>
    public const double WheelScale = 1.66;

    /// <summary>The share of the icon's side kept for the wheel variant (the circle's inscribed square, with a margin).</summary>
    public const double WheelCore = 0.6;

    private static bool IsPhoneAttack(string name) =>
        name.StartsWith("phone_attack", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(WheelSuffix, StringComparison.OrdinalIgnoreCase);

    private void AddWheelVariant(string sourceName, string targetName)
    {
        if (_templates.ContainsKey(targetName) || !_templates.TryGetValue(sourceName, out var source))
            return;

        int side = (int)Math.Round(Math.Min(source.Width, source.Height) * WheelCore);
        if (side < 8)
            return;
        var box = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);
        var scaled = new Mat();
        using (var core = new Mat(source.Gray, box))
            CvInvoke.Resize(core, scaled, new Size((int)Math.Round(side * WheelScale), (int)Math.Round(side * WheelScale)), 0, 0, Inter.Cubic);
        _templates[targetName] = new TemplateImage(targetName, source.Path, scaled);
    }

    /// <summary>
    /// Loads a template as grayscale. A PNG with transparent areas (flashlight3.png: 395x341 with
    /// only the beam and a stray mark opaque) is cropped to its largest opaque patch - matching
    /// ignores alpha, so the transparent canvas would otherwise be part of the template.
    /// </summary>
    private static Mat? LoadGray(string file)
    {
        using var raw = ImageFile.Read(file, ImreadModes.Unchanged);
        if (raw.IsEmpty)
            return null;

        if (raw.NumberOfChannels == 1)
            return raw.Clone();

        var gray = new Mat();
        CvInvoke.CvtColor(raw, gray, raw.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);
        if (raw.NumberOfChannels != 4)
            return gray;

        using var alpha = new Mat();
        CvInvoke.ExtractChannel(raw, alpha, 3);
        using var opaque = new Mat();
        CvInvoke.Threshold(alpha, opaque, 0, 255, ThresholdType.Binary);
        if (CvInvoke.CountNonZero(opaque) == opaque.Rows * opaque.Cols)
            return gray;

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = CvInvoke.ConnectedComponentsWithStats(opaque, labels, stats, centroids);
        // One row per label: left, top, width, height, area.
        int[] table = CvHelpers.Int32s(stats);
        int columns = stats.Cols;
        int Stat(int label, ConnectedComponentsTypes column) => table[label * columns + (int)column];

        int best = 0, bestArea = 0;
        for (int i = 1; i < count; i++)
        {
            int area = Stat(i, ConnectedComponentsTypes.Area);
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

        var box = new Rectangle(
            Stat(best, ConnectedComponentsTypes.Left),
            Stat(best, ConnectedComponentsTypes.Top),
            Stat(best, ConnectedComponentsTypes.Width),
            Stat(best, ConnectedComponentsTypes.Height));
        Mat cropped;
        using (var patch = new Mat(gray, box))
            cropped = patch.Clone();
        gray.Dispose();
        return cropped;
    }

    private void AddFlippedVariant(string sourceName, string targetName, FlipType mode)
    {
        if (_templates.ContainsKey(targetName) || !_templates.TryGetValue(sourceName, out var source))
            return;

        var flipped = new Mat();
        CvInvoke.Flip(source.Gray, flipped, mode);
        _templates[targetName] = new TemplateImage(targetName, source.Path, flipped);
    }

    private void AddRotatedVariant(string sourceName, string targetName, RotateFlags flags)
    {
        if (_templates.ContainsKey(targetName))
            return;

        if (!_templates.TryGetValue(sourceName, out var source))
            return;

        var rotated = new Mat();
        CvInvoke.Rotate(source.Gray, rotated, flags);
        _templates[targetName] = new TemplateImage(targetName, source.Path, rotated);
    }

    public void Dispose()
    {
        foreach (var template in _templates.Values)
            template.Dispose();
        _templates.Clear();
    }
}
