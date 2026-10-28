namespace WuxAuto.Phone;

public static class JoystickMath
{
    /// <summary>Where the character stands on the frame, as fractions of its size.</summary>
    public const double CharacterX = 0.5;
    public const double CharacterY = 0.54;

    /// <summary>
    /// The joystick offset that walks towards a screen point: the vector from the character (0.5 w, 0.54 h) to
    /// (<paramref name="x"/>, <paramref name="y"/>), scaled to length <paramref name="radius"/>; (0, 0) for the
    /// character's own spot.
    /// </summary>
    public static (int Dx, int Dy) ToStick(int frameW, int frameH, int x, int y, int radius)
    {
        double dx = x - frameW * CharacterX;
        double dy = y - frameH * CharacterY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.5)
            return (0, 0);

        return ((int)Math.Round(dx / length * radius), (int)Math.Round(dy / length * radius));
    }
}
