using WuxAuto.Core;

namespace WuxAuto.Phone;

/// <summary>Which screen edge a phone HUD element keeps its distance from when the screen is wider or narrower.</summary>
public enum PhoneAnchor
{
    Left,
    Centre,
    Right,
}

/// <summary>
/// Turns a box measured on the 1920x1080 MuMu screen into pixels on another screen. Assumption (not yet checked on a
/// second screen size): the game's HUD scales with the screen height, and each element keeps its scaled distance from
/// the edge it belongs to - left-hand items from the left edge, right-hand ones from the right edge, centred ones from
/// the middle. On a 16:9 screen this is the same as plain fractions.
/// </summary>
public static class PhoneLayout
{
    /// <summary>The screen every phone measurement was taken on (MuMu, Plan 2 Task 1).</summary>
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    /// <summary>The box (<paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/>, <paramref name="h"/> at 1920x1080) on a <paramref name="frameWidth"/> x <paramref name="frameHeight"/> screen.</summary>
    public static PixelRect ToPixels(int x, int y, int w, int h, PhoneAnchor anchor, int frameWidth, int frameHeight)
    {
        double s = frameHeight / (double)ReferenceHeight;
        double left = anchor switch
        {
            PhoneAnchor.Left => x * s,
            PhoneAnchor.Right => frameWidth - (ReferenceWidth - x) * s,
            _ => frameWidth / 2.0 + (x - ReferenceWidth / 2.0) * s,
        };
        return new PixelRect((int)Math.Round(left), (int)Math.Round(y * s), (int)Math.Round(w * s), (int)Math.Round(h * s));
    }

    /// <summary>The same box as fractions of the screen, clamped to it; an unknown size (0) means the 1920x1080 screen.</summary>
    public static NormRect ToNorm(int x, int y, int w, int h, PhoneAnchor anchor, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            (frameWidth, frameHeight) = (ReferenceWidth, ReferenceHeight);

        var px = ToPixels(x, y, w, h, anchor, frameWidth, frameHeight);
        double left = Math.Clamp(px.X / (double)frameWidth, 0, 1);
        double top = Math.Clamp(px.Y / (double)frameHeight, 0, 1);
        double right = Math.Clamp(px.Right / (double)frameWidth, 0, 1);
        double bottom = Math.Clamp(px.Bottom / (double)frameHeight, 0, 1);
        return new NormRect(left, top, right - left, bottom - top);
    }
}

/// <summary>
/// The screen regions the LOD flow reads, for the phone game (docs/superpowers/notes/2026-10-28-phone-checks.md, MuMu
/// 1920x1080). Applied by <see cref="AppSettings.ResetPositions(PlatformProfile?, int, int)"/> with the phone profile;
/// every region not set here keeps its desktop default.
/// </summary>
public static class PhoneRegions
{
    /// <summary>Sets the phone regions for a <paramref name="width"/> x <paramref name="height"/> screen (0 = not known yet: 1920x1080).</summary>
    public static void Apply(AppSettings settings, int width, int height)
    {
        var r = settings.Regions;

        // The zone title (1730,77) and "x , y" (1730,103) lines at the top of the minimap panel (1568-1897 x 60-390):
        // M0's crop 1560,55 340x70 exactly. Not the whole panel, and not wider: with the map (or a few more pixels of it)
        // in the crop, Windows OCR merged the title with the close X and dropped a word on 25 of the 28 frames that show
        // the minimap ("[Piercing X", also with 1555,48 350x84); this crop read "[Piercing City" and the coordinates on
        // all 28 (Task 4 probe).
        r.Minimap = PhoneLayout.ToNorm(1560, 55, 340, 70, PhoneAnchor.Right, width, height);

        // System / combat lines at the top left, 20-540 x 225-470 (M0 saw "Attack succeeded" there; the desktop's
        // log is on the left half too, but the phone's sits higher and narrower).
        r.AttackMessage = PhoneLayout.ToNorm(0, 200, 600, 320, PhoneAnchor.Left, width, height);

        // The 8-slot hotbar, slot centres x 680-1236 at y 1022 (slots ~72 px).
        r.Hotbar = PhoneLayout.ToNorm(630, 975, 660, 95, PhoneAnchor.Centre, width, height);

        // The target / pet panel left of the minimap, 1282-1550 x 60-175 (the dungeon target bar is not measured yet).
        r.TargetBox = PhoneLayout.ToNorm(1270, 50, 290, 135, PhoneAnchor.Right, width, height);

        // Boss name tags can be anywhere in the world view, as on the desktop.
        r.BossName = new NormRect(0, 0, 1, 1);

        // The Teleport Scroll window 760-1160 x 155-960 (rows centred on x 960, pager at y 257).
        r.TeleportList = PhoneLayout.ToNorm(740, 140, 440, 840, PhoneAnchor.Centre, width, height);
        r.Ocr = PhoneLayout.ToNorm(740, 140, 440, 840, PhoneAnchor.Centre, width, height);
    }
}
