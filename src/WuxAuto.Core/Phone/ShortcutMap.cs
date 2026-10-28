using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Phone;

/// <summary>
/// Finds the phone game's on-screen buttons with the <c>phone_*</c> templates (src/WuxAuto.Mobile/Assets/phone,
/// cropped at 1920x1080) and the analyzer's learned-scale search. The search runs on the first frame, on every frame
/// of a new size and on the first frame after <see cref="Invalidate"/>; any other <see cref="Refresh"/> is a no-op,
/// so the positions found stay until the screen changes or a touch misses. Loop thread only.
/// </summary>
public sealed class ShortcutMap(ScreenAnalyzer analyzer, double threshold = ShortcutMap.DefaultThreshold) : IShortcutSource
{
    /// <summary>
    /// Minimum template score. On the 30 MuMu frames (Task 4 probe) a shortcut that shows scores 0.92-1.00 (0.887: the
    /// sword seen through the ☰ panel) and the best look-alike on a frame without it 0.80 at most; 0.845 is the joystick
    /// in its own place under the map window. The desktop's loose 0.70 would let those through.
    /// </summary>
    public const double DefaultThreshold = 0.85;

    public const string MenuTemplate = "phone_menu";
    public const string JoystickTemplate = "phone_joystick";

    /// <summary>The joystick's travel (106 px at 1080, Task 1) per pixel of the 150 px wide joystick template.</summary>
    public const double JoystickRadiusPerWidth = 106.0 / 150.0;

    /// <summary>The template of each action's shortcut. A template that does not exist (no crop yet) is simply never found.</summary>
    public static IReadOnlyDictionary<GameAction, string> ActionTemplates { get; } = new Dictionary<GameAction, string>
    {
        [GameAction.TeleportScroll] = "phone_teleportscroll",
        [GameAction.Loot] = "phone_pickup",
        [GameAction.Buff] = "phone_autobuff",
        [GameAction.TargetNearest] = "phone_sword",
        [GameAction.ToggleAutohunt] = "phone_autohunt",
        [GameAction.RunSkillKey] = "phone_runskill",
        [GameAction.ShuraKey] = "phone_shura",
    };

    /// <summary>
    /// The skill wheel's buttons, by the skill's icon: the round buttons round the sword are the wheel's slots (Task 1
    /// read them as potion orbs - they held potions then); the "1 / 2 / 3" segments bottom right are the wheel's page
    /// tabs (phone_wheelpage1-3, not used here). "1" = the button nearest the sword's upper left (1495,648 at 1920x1080),
    /// where the user put a skill - which slot the game calls "1" is not confirmed. No crop yet for "2" and "3".
    /// </summary>
    public static IReadOnlyDictionary<string, string> SkillTemplates { get; } = new Dictionary<string, string>
    {
        ["1"] = "phone_skill1",
        ["2"] = "phone_skill2",
        ["3"] = "phone_skill3",
    };

    // The search zones (see ZonesFor), measured at 1920x1080.
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) Ring = (0, 540, 560, 540, PhoneAnchor.Left);
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) RightCluster = (1280, 480, 640, 600, PhoneAnchor.Right);
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) Hotbar = (630, 975, 660, 105, PhoneAnchor.Centre);
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) TopStrip = (600, 0, 900, 110, PhoneAnchor.Centre);

    private readonly Dictionary<string, TemplateMatch> _found = new(StringComparer.OrdinalIgnoreCase);
    private bool _stale = true;

    public (int Width, int Height) FrameSize { get; private set; }

    /// <summary>The best hit of every template found by the last search (for logs and the probe).</summary>
    public IReadOnlyDictionary<string, TemplateMatch> Matches => _found;

    /// <summary>Searches every shortcut again when the frame size changed or <see cref="Invalidate"/> was called; otherwise does nothing.</summary>
    public void Refresh(Mat frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return;
        if (!_stale && FrameSize == (frame.Width, frame.Height))
            return;

        FrameSize = (frame.Width, frame.Height);
        _found.Clear();
        _stale = false;

        foreach (string template in ActionTemplates.Values.Concat(SkillTemplates.Values).Append(JoystickTemplate).Append(MenuTemplate))
            Search(frame, template);
    }

    /// <summary>
    /// Where a template is looked for on a <paramref name="width"/> x <paramref name="height"/> frame. The picker's
    /// shortcuts (Teleport Scroll, Pick up, HUNT) only go on the ring round the joystick (bottom left); AUTO BUFF is a
    /// fixed button in the bottom-right cluster and can be on the ring too; the sword and the skill wheel are in the
    /// cluster; the Run skill is an orb in the cluster or a hotbar slot, Shura a hotbar slot (or an orb); ☰ is in the
    /// top strip. Every zone costs a full scale search, so a template is only looked for where it can be.
    /// </summary>
    public static IReadOnlyList<PixelRect> ZonesFor(string template, int width, int height)
    {
        (int X, int Y, int W, int H, PhoneAnchor Anchor)[] zones = template.ToLowerInvariant() switch
        {
            MenuTemplate => [TopStrip],
            JoystickTemplate or "phone_teleportscroll" or "phone_pickup" or "phone_autohunt" => [Ring],
            "phone_autobuff" => [RightCluster, Ring],
            "phone_runskill" => [RightCluster, Hotbar],
            "phone_shura" => [Hotbar, RightCluster],
            _ => [RightCluster], // the sword, the skill wheel
        };
        return zones.Select(z => PhoneLayout.ToPixels(z.X, z.Y, z.W, z.H, z.Anchor, width, height)).ToList();
    }

    public PixelRect? Find(GameAction action) =>
        ActionTemplates.TryGetValue(action, out string? template) ? Box(template) : null;

    public PixelRect? Skill(string skill) =>
        SkillTemplates.TryGetValue(skill, out string? template) ? Box(template) : null;

    public (int Cx, int Cy, int Radius)? Joystick =>
        _found.TryGetValue(JoystickTemplate, out var m)
            ? (m.Bounds.CenterX, m.Bounds.CenterY, (int)Math.Round(m.Bounds.Width * JoystickRadiusPerWidth))
            : null;

    public PixelRect? Menu => Box(MenuTemplate);

    public void Invalidate()
    {
        _found.Clear();
        _stale = true;
    }

    private PixelRect? Box(string template) => _found.TryGetValue(template, out var m) ? m.Bounds : null;

    private void Search(Mat frame, string template)
    {
        TemplateMatch? best = null;
        foreach (var roi in ZonesFor(template, frame.Width, frame.Height))
        {
            var hits = analyzer.FindIcons(frame, template, roi, threshold, maxResults: 1);
            if (hits.Count > 0 && (best == null || hits[0].Score > best.Score))
                best = hits[0];
        }

        if (best != null)
            _found[template] = best;
    }
}
