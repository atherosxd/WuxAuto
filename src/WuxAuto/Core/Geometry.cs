namespace WuxAuto.Core;

/// <summary>Integer rectangle in client (game) pixel space.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    public static PixelRect FromBounds(int left, int top, int right, int bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public PixelRect Inflate(int amount)
        => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public PixelRect Clamp(int width, int height)
    {
        int x = Math.Clamp(X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(Y, 0, Math.Max(0, height - 1));
        int right = Math.Clamp(Right, x + 1, width);
        int bottom = Math.Clamp(Bottom, y + 1, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    public double DistanceTo(int px, int py)
    {
        double dx = CenterX - px;
        double dy = CenterY - py;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}

/// <summary>Rectangle expressed as fractions (0..1) of the client area.</summary>
public sealed class NormRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 1;
    public double H { get; set; } = 1;

    public NormRect() { }

    public NormRect(double x, double y, double w, double h)
    {
        X = x; Y = y; W = w; H = h;
    }

    public PixelRect ToPixels(int clientWidth, int clientHeight)
    {
        int x = (int)Math.Round(X * clientWidth);
        int y = (int)Math.Round(Y * clientHeight);
        int w = (int)Math.Round(W * clientWidth);
        int h = (int)Math.Round(H * clientHeight);
        return new PixelRect(x, y, w, h).Clamp(clientWidth, clientHeight);
    }

    public NormRect Clone() => new(X, Y, W, H);

    public override string ToString() => $"{X:0.###},{Y:0.###},{W:0.###},{H:0.###}";
}
