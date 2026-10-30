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

/// <summary>A confirm dialog: its message line and the button to click (<c>How</c>: "read" or "gold button").</summary>
public sealed record ConfirmDialog(string Message, PixelRect MessageBounds, PixelRect Button, string How);

/// <summary>
/// An open war window (Auto War): which one (<c>Window</c>: "TRIBE STATUS" or "MENU"), its title as read, and its X -
/// null when no close button was seen (no blind click).
/// </summary>
public sealed record WarWindow(string Window, string Title, PixelRect TitleBounds, PixelRect? Close);

/// <summary>
/// The Auto War fight's read of the target box (<c>ScreenAnalyzer.ReadWarTargetAsync</c>): the name when one reads, the
/// HP bar's "N / M" line and its box (null when none), the target bar's chest, and which read decided (<c>Via</c>: "x1.5", "white x2",
/// "red x2", "numbers", "chest" or "none"). <c>Seen</c>: any of the three - a target is in the box.
/// </summary>
public sealed record WarTargetRead(string? Name, (string Text, PixelRect Bounds)? HpLine, TemplateMatch? Chest, string Via)
{
    public bool Seen => Name != null || HpLine != null || Chest != null;
}

/// <summary>
/// A Settings window row: label, value box, value text and number (numeric rows), and the chevrons
/// beside the value.
/// </summary>
/// <summary>
/// The phone's Settings > VISUAL "Interface scale" row: "AUTO  -  1.01  +". <see cref="Value"/> is the number read, 1.00
/// for "(AUTO)" (<see cref="Auto"/>), null when nothing read; <see cref="Minus"/> / <see cref="Plus"/> the buttons beside it.
/// </summary>
public sealed record ScaleRow(PixelRect Label, double? Value, bool Auto, PixelRect? Minus, PixelRect? Plus, string? ValueText);

public sealed record SettingRow(PixelRect Label, PixelRect? Value, int? Percent, PixelRect? Left, PixelRect? Right, string? ValueText = null);

public sealed class ScanReport
{
    public List<OverlayItem> Overlays { get; } = new();
    public List<string> Details { get; } = new();
}
