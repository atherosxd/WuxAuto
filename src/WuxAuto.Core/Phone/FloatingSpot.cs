namespace WuxAuto.Phone;

/// <summary>
/// Where the phone app's floating button sits. Its spot is saved as the button centre's fractions of the screen
/// (<see cref="Core.PhoneSettings.FloatingX"/> / <see cref="Core.PhoneSettings.FloatingY"/>), so it survives a rotation
/// or another screen size; after a drag it snaps to the nearest screen edge. The default (0.40, 0.03) is the top strip
/// the 2026-10-28 phone checks found clear of everything the pilot reads or taps at 1920x1080.
/// </summary>
public static class FloatingSpot
{
    public const int MinSize = 40;
    public const int MaxSize = 72;

    /// <summary>Gap between a snapped button and the screen edge.</summary>
    public const int EdgeMargin = 4;

    /// <summary>The button's diameter: 5 % of the screen's short side (54 px at 1080, within the note's 56), 40-72 px.</summary>
    public static int SizeFor(int screenWidth, int screenHeight) =>
        Math.Clamp((int)Math.Round(Math.Min(screenWidth, screenHeight) * 0.05), MinSize, MaxSize);

    /// <summary>The top-left pixel of a <paramref name="size"/> button centred on the fractions, kept on screen.</summary>
    public static (int Left, int Top) TopLeft(double fx, double fy, int screenWidth, int screenHeight, int size)
    {
        int left = (int)Math.Round(fx * screenWidth - size / 2.0);
        int top = (int)Math.Round(fy * screenHeight - size / 2.0);
        return (Math.Clamp(left, 0, Math.Max(0, screenWidth - size)), Math.Clamp(top, 0, Math.Max(0, screenHeight - size)));
    }

    /// <summary>
    /// The spot a button dropped with its centre at (<paramref name="centreX"/>, <paramref name="centreY"/>) snaps to:
    /// the nearest edge (left, right, top or bottom), <see cref="EdgeMargin"/> in, the other axis kept on screen.
    /// Returned as centre fractions.
    /// </summary>
    public static (double X, double Y) Snap(int centreX, int centreY, int screenWidth, int screenHeight, int size)
    {
        double half = size / 2.0;
        double minX = EdgeMargin + half, maxX = screenWidth - EdgeMargin - half;
        double minY = EdgeMargin + half, maxY = screenHeight - EdgeMargin - half;
        double x = Math.Clamp(centreX, minX, Math.Max(minX, maxX));
        double y = Math.Clamp(centreY, minY, Math.Max(minY, maxY));

        double toLeft = centreX, toRight = screenWidth - centreX, toTop = centreY, toBottom = screenHeight - centreY;
        double nearest = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
        if (nearest == toTop)
            y = minY;
        else if (nearest == toBottom)
            y = maxY;
        else if (nearest == toLeft)
            x = minX;
        else
            x = maxX;

        return (x / screenWidth, y / screenHeight);
    }
}
