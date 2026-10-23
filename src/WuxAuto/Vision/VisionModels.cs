using WuxAuto.Core;

namespace WuxAuto.Vision;

public enum OverlayColor
{
    Green,
    Orange,
    Blue,
    Red,
    Yellow,
    None,
}

public sealed record OverlayItem(PixelRect Bounds, string Label, OverlayColor Color, (int X, int Y)? LineTo = null);

public sealed record TemplateMatch(string Template, double Score, PixelRect Bounds, double Scale)
{
    public override string ToString() => $"{Template} {Score:0.000} @ {Bounds} (x{Scale:0.00})";
}

public sealed record TextHit(string Text, double Similarity, PixelRect Bounds, string LineText, PixelRect LineBounds, double OcrMs)
{
    public override string ToString() => $"'{Text}' {Similarity:0.00} @ {Bounds} (line '{LineText}')";
}

public sealed record CompassReading(double AngleFromNorth, double Score, PixelRect Bounds, string Method, double Ms)
{
    public override string ToString() => $"{AngleFromNorth:0.0}° score {Score:0.00} via {Method} in {Ms:0.0}ms";
}

public sealed record OcrWord(string Text, PixelRect Bounds, string LineText, PixelRect LineBounds);

/// <summary>A Settings window row: label, value box and number, and the chevrons beside the value.</summary>
public sealed record SettingRow(PixelRect Label, PixelRect? Value, int? Percent, PixelRect? Left, PixelRect? Right);

public sealed class ScanReport
{
    public List<OverlayItem> Overlays { get; } = new();
    public List<string> Details { get; } = new();
}
