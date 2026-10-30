using WuxAuto.Core;

namespace WuxAuto.Phone;

/// <summary>
/// Where the phone app's floating button may sit. It rests only inside a <see cref="ClearBands"/> strip, where nothing
/// the pilot reads or taps lies (review ruling I-2): a drop anywhere else moves it to the nearest spot in a band. Its spot
/// is saved as the button centre's fractions of the screen (<see cref="PhoneSettings.FloatingX"/> /
/// <see cref="PhoneSettings.FloatingY"/>); a saved spot outside every band (an older version, another screen size) is
/// moved in the same way when it is laid out.
/// </summary>
public static class FloatingSpot
{
    public const int MinSize = 40;
    public const int MaxSize = 72;

    /// <summary>
    /// The strips measured clear at 1920x1080 (docs/superpowers/notes/2026-10-28-phone-checks.md, "Floating control
    /// spot"): the top strip x 640-1110, y 0-55, between the level / HP block and the buff icons (ending at x 592) and ☰
    /// (from x 1158); system lines start at y 105. Centre-anchored like the rest of the top strip. The note's second
    /// choice (left edge x 0-40) is narrower than the button, so it is not a band. Not checked on the Xiaomi's 2608x1200
    /// yet (Task 8).
    /// </summary>
    public static IReadOnlyList<(int X, int Y, int W, int H, PhoneAnchor Anchor)> ClearBands { get; } =
    [
        (640, 0, 470, 55, PhoneAnchor.Centre),
    ];

    /// <summary>The button's diameter: 5 % of the screen's short side (54 px at 1080, within the band's 55), 40-72 px.</summary>
    public static int SizeFor(int screenWidth, int screenHeight) =>
        Math.Clamp((int)Math.Round(Math.Min(screenWidth, screenHeight) * 0.05), MinSize, MaxSize);

    /// <summary>The top-left pixel of a <paramref name="size"/> button for the saved centre fractions, moved into a band.</summary>
    public static (int Left, int Top) TopLeft(double fx, double fy, int screenWidth, int screenHeight, int size)
    {
        var (cx, cy) = Nearest(fx * screenWidth, fy * screenHeight, screenWidth, screenHeight, size);
        int left = (int)Math.Round(cx - size / 2.0);
        int top = (int)Math.Round(cy - size / 2.0);
        return (Math.Clamp(left, 0, Math.Max(0, screenWidth - size)), Math.Clamp(top, 0, Math.Max(0, screenHeight - size)));
    }

    /// <summary>
    /// The spot a button dropped with its centre at (<paramref name="centreX"/>, <paramref name="centreY"/>) rests at:
    /// the nearest centre that keeps the whole button inside a clear band. Returned as centre fractions.
    /// </summary>
    public static (double X, double Y) Snap(int centreX, int centreY, int screenWidth, int screenHeight, int size)
    {
        var (cx, cy) = Nearest(centreX, centreY, screenWidth, screenHeight, size);
        return (cx / screenWidth, cy / screenHeight);
    }

    /// <summary>The band rectangles in pixels on this screen.</summary>
    public static IReadOnlyList<PixelRect> BandsFor(int screenWidth, int screenHeight) =>
        ClearBands.Select(b => PhoneLayout.ToPixels(b.X, b.Y, b.W, b.H, b.Anchor, screenWidth, screenHeight)).ToList();

    private static (double X, double Y) Nearest(double x, double y, int screenWidth, int screenHeight, int size)
    {
        double half = size / 2.0;
        (double X, double Y) best = (x, y);
        double bestDistance = double.MaxValue;
        foreach (var band in BandsFor(screenWidth, screenHeight))
        {
            // The centre range that keeps the button inside the band (centred on the band when the band is smaller).
            double minX = band.X + half, maxX = band.Right - half;
            double minY = band.Y + half, maxY = band.Bottom - half;
            if (minX > maxX)
                minX = maxX = band.X + band.Width / 2.0;
            if (minY > maxY)
                minY = maxY = band.Y + band.Height / 2.0;
            double cx = Math.Clamp(x, minX, maxX), cy = Math.Clamp(y, minY, maxY);
            double distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (cx, cy);
            }
        }

        return best;
    }
}
