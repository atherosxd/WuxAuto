using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Phone;

/// <summary>
/// Finds the phone game's on-screen buttons with the <c>phone_*</c> templates (src/WuxAuto.Mobile/Assets/phone,
/// cropped at 1920x1080) and the analyzer's learned-scale search. <see cref="Refresh"/> searches everything on the first
/// frame, on a new frame size and after <see cref="Invalidate()"/>; otherwise only the shortcuts invalidated one by one
/// (a press that missed) and those found absent at least <see cref="AbsentRetryMs"/> ago. A shortcut found keeps its
/// position until it is invalidated; one without a template file is never searched. Loop thread only.
/// </summary>
/// <param name="clock">Milliseconds, for the absent cool-down (default <see cref="Environment.TickCount64"/>; tests pass their own).</param>
public sealed class ShortcutMap(ScreenAnalyzer analyzer, double threshold = ShortcutMap.DefaultThreshold, Func<long>? clock = null)
    : IShortcutSource
{
    /// <summary>A shortcut not on screen is looked for again this long after the last search (a window may have covered it).</summary>
    public const int AbsentRetryMs = 10_000;

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
        ["attack1"] = "phone_attack1",
        ["attack2"] = "phone_attack2",
        ["attack3"] = "phone_attack3",
        ["attack4"] = "phone_attack4",
        ["attack5"] = "phone_attack5",
        ["attack6"] = "phone_attack6",
        ["attack7"] = "phone_attack7",
    };

    /// <summary>
    /// The attack skill icons (the user's crops, 2026-10-29), in order of preference: a skill press uses the first one
    /// found on the hotbar or the skill wheel (<see cref="AttackSkill"/>). Also in <see cref="SkillTemplates"/> as
    /// "attack1".."attack7".
    /// </summary>
    public static IReadOnlyList<string> AttackTemplates { get; } =
        ["phone_attack1", "phone_attack2", "phone_attack3", "phone_attack4", "phone_attack5", "phone_attack6", "phone_attack7"];

    // The search zones (see ZonesFor), measured at 1920x1080.
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) Ring = (0, 540, 560, 540, PhoneAnchor.Left);
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) RightCluster = (1280, 480, 640, 600, PhoneAnchor.Right);
    // The hotbar grows with the game's interface scale: at 1.10 (the Settings visit's default) its first slot starts at
    // x 625, y 983 (MuMu 2026-10-30) - the 1.00 box (630, 975, 660, 105) cut 5 px off the icon, and the attack icon
    // scored 0.83 instead of 0.99, under the 0.85 gate: the held skill never went down. Room for 1.00-1.20.
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) Hotbar = (560, 935, 800, 145, PhoneAnchor.Centre);
    // The ☰ keeps its distance from the right edge, not from the middle: 734 px from the right at 1920x1080 (MuMu) and
    // 815 px = 734 x 1200/1080 at 2608x1200 (Xiaomi 17 Pro Max, 2026-10-30) - centred it would be at x 1555 there, it is at 1794.
    private static readonly (int X, int Y, int W, int H, PhoneAnchor Anchor) TopStrip = (600, 0, 900, 110, PhoneAnchor.Right);

    private static readonly string[] AllTemplates =
        ActionTemplates.Values.Concat(SkillTemplates.Values).Append(JoystickTemplate).Append(MenuTemplate).Append(AutohuntOnTemplate).ToArray();

    /// <summary>
    /// HUNT while the autohunt runs: the yellow swords turn into a "+" in a yellow glow (user, 2026-10-30: "when autohunt
    /// button (yellow blades) it changes to a cross button"; cropped from MuMu at 1920x1080, interface scale 1.10). The
    /// same button: a tap turns the autohunt off.
    /// </summary>
    public const string AutohuntOnTemplate = "phone_autohunt_on";

    /// <summary>The HUNT button's two pictures are looked at again this often, so <see cref="AutohuntOn"/> follows the game.</summary>
    public const int AutohuntRecheckMs = 1000;

    /// <summary>
    /// After a tap of HUNT the state is unknown until a search this much later: the captured frame lags the game, and a
    /// search of a frame from before the tap would read the old state (and a second tap would undo the first).
    /// </summary>
    public const int AutohuntSettleMs = 1200;

    /// <summary>
    /// Of the ring round a "+" hit (1.6-2.6 half-widths from its centre; the glow sits at ~2.2), at least this share must be
    /// yellow: the autohunt-on glow. Measured 2026-10-30 on 13 MuMu frames (1920x1080): 0.23-0.37 round the "+" while the
    /// autohunt ran, 0.00-0.04 round the yellow swords (off) and the white AUTO BUFF swords.
    /// </summary>
    public const double OnGlowMin = 0.12;

    /// <summary>
    /// HUNT and AUTO BUFF are the same crossed swords, HUNT drawn yellow, AUTO BUFF white (user, 2026-10-30); in grey the
    /// HUNT crop scores 0.90-0.94 on a white AUTO BUFF. Of the bright pixels in the middle of a hit, the yellow ones: HUNT
    /// 0.34 (MuMu) / 0.89 (Xiaomi), AUTO BUFF 0.00 on both (ring copy and the big button). A HUNT hit needs at least
    /// this much yellow, an AUTO BUFF hit less.
    /// </summary>
    public const double YellowSwordsMin = 0.15;

    public const string AutohuntTemplate = "phone_autohunt";
    public const string AutoBuffTemplate = "phone_autobuff";

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Dictionary<string, TemplateMatch> _found = new(StringComparer.OrdinalIgnoreCase);

    // Searched on this frame size and not there: since when (clock ms). Cleared with the positions.
    private readonly Dictionary<string, long> _absentSince = new(StringComparer.OrdinalIgnoreCase);

    // Invalidated one by one since the last search.
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);
    private bool _allStale = true;

    // The HUNT button's pictures: when both were last searched, and when it was last tapped (clock ms; null = never).
    private long? _autohuntSearchedAt;
    private long? _autohuntPressedAt;

    public (int Width, int Height) FrameSize { get; private set; }

    /// <summary>A copy of the best hit of every template found so far (for logs and the probe).</summary>
    public IReadOnlyDictionary<string, TemplateMatch> Matches =>
        new Dictionary<string, TemplateMatch>(_found, StringComparer.OrdinalIgnoreCase);

    /// <summary>How many template searches have run (diagnostics; each one is a full scale search), without HUNT's state re-reads.</summary>
    public int SearchCount { get; private set; }

    /// <summary>How many times HUNT's two pictures were looked at again for <see cref="AutohuntOn"/> (every <see cref="AutohuntRecheckMs"/>).</summary>
    public int HuntRereads { get; private set; }

    /// <summary>
    /// Searches everything on a new frame size or after <see cref="Invalidate()"/>; otherwise only the shortcuts
    /// invalidated one by one and those absent for <see cref="AbsentRetryMs"/>. Shortcuts found keep their positions.
    /// </summary>
    public void Refresh(Mat frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        if (_allStale || FrameSize != (frame.Width, frame.Height))
        {
            FrameSize = (frame.Width, frame.Height);
            _found.Clear();
            _absentSince.Clear();
            _stale.Clear();
            _allStale = false;
            _autohuntSearchedAt = _clock();
            foreach (string template in AllTemplates)
                Search(frame, template);
            return;
        }

        long now = _clock();
        // HUNT's two pictures follow the game's state (AutohuntOn): every AutohuntRecheckMs, and once more as soon as a tap
        // has settled.
        bool huntDue = _autohuntSearchedAt is not long searched
                       || now - searched >= AutohuntRecheckMs
                       || (_autohuntPressedAt is long pressed && searched < pressed + AutohuntSettleMs && now >= pressed + AutohuntSettleMs);
        if (huntDue)
            _autohuntSearchedAt = now;
        foreach (string template in AllTemplates)
        {
            // An absent attack icon is not looked for on the cool-down by itself: only a skill press that found none
            // asks again (MissedAttackSkill) - seven full searches every 10 s would cost the loop for nothing.
            // HUNT's pictures come back on their own schedule (huntDue), not on the absent cool-down.
            bool due = _stale.Contains(template)
                       || (!IsAttack(template) && !IsHuntPicture(template) && _absentSince.TryGetValue(template, out long since) && now - since >= AbsentRetryMs);
            if (due)
                Search(frame, template);
            else if (huntDue && IsHuntPicture(template))
                Search(frame, template, reread: true);
        }

        _stale.Clear();
    }

    /// <summary>
    /// Whether the autohunt runs, from the HUNT button (the phone's only way to tell; the desktop's J is a blind toggle):
    /// the yellow swords = off, the "+" in a yellow glow = on. Null = cannot tell: neither (or both) on the last search,
    /// not searched yet, or a tap of it not settled yet (<see cref="AutohuntSettleMs"/>).
    /// </summary>
    public bool? AutohuntOn
    {
        get
        {
            if (_autohuntSearchedAt is not long searched)
                return null;
            if (_autohuntPressedAt is long pressed && searched < pressed + AutohuntSettleMs)
                return null;
            bool off = _found.ContainsKey(AutohuntTemplate);
            bool on = _found.ContainsKey(AutohuntOnTemplate);
            return on == off ? null : on;
        }
    }

    /// <summary>HUNT was tapped: its state is unknown until a search <see cref="AutohuntSettleMs"/> later.</summary>
    public void AutohuntPressed() => _autohuntPressedAt = _clock();

    private static bool IsHuntPicture(string template) =>
        template.Equals(AutohuntTemplate, StringComparison.OrdinalIgnoreCase) || template.Equals(AutohuntOnTemplate, StringComparison.OrdinalIgnoreCase);

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
            JoystickTemplate or "phone_teleportscroll" or "phone_pickup" or AutohuntTemplate or AutohuntOnTemplate => [Ring],
            "phone_autobuff" => [RightCluster, Ring],
            "phone_runskill" => [RightCluster, Hotbar],
            "phone_shura" => [Hotbar, RightCluster],
            var t when IsAttack(t) => [Hotbar, RightCluster], // a hotbar slot or a round button of the skill wheel
            _ => [RightCluster], // the sword, the skill wheel
        };
        return zones.Select(z => PhoneLayout.ToPixels(z.X, z.Y, z.W, z.H, z.Anchor, width, height)).ToList();
    }

    /// <summary>The shortcut ring round the joystick (bottom left) on a <paramref name="width"/> x <paramref name="height"/> frame.</summary>
    public static PixelRect RingZone(int width, int height) =>
        PhoneLayout.ToPixels(Ring.X, Ring.Y, Ring.W, Ring.H, Ring.Anchor, width, height);

    /// <summary>The action's button; HUNT in either picture (yellow swords or the "+" while the autohunt runs).</summary>
    public PixelRect? Find(GameAction action) =>
        action == GameAction.ToggleAutohunt
            ? Box(AutohuntTemplate) ?? Box(AutohuntOnTemplate)
            : ActionTemplates.TryGetValue(action, out string? template) ? Box(template) : null;

    public PixelRect? Skill(string skill) =>
        SkillTemplates.TryGetValue(skill, out string? template) ? Box(template) : null;

    /// <summary>The first attack icon (<see cref="AttackTemplates"/> order) found; the skill name is ignored.</summary>
    public PixelRect? AttackSkill(string skill)
    {
        foreach (string template in AttackTemplates)
            if (Box(template) is { } box)
                return box;
        return null;
    }

    /// <summary>
    /// A skill press found no attack icon: every attack icon searched less than <see cref="AbsentRetryMs"/> ago is left
    /// alone, the others (never searched, or longer ago) are looked for on the next <see cref="Refresh"/> - once, since
    /// that search records them absent again.
    /// </summary>
    public void MissedAttackSkill(string skill)
    {
        long now = _clock();
        foreach (string template in AttackTemplates)
        {
            if (_found.ContainsKey(template))
                continue;
            if (_absentSince.TryGetValue(template, out long since) && now - since < AbsentRetryMs)
                continue;
            Forget(template);
        }
    }

    public (int Cx, int Cy, int Radius)? Joystick =>
        _found.TryGetValue(JoystickTemplate, out var m)
            ? (m.Bounds.CenterX, m.Bounds.CenterY, (int)Math.Round(m.Bounds.Width * JoystickRadiusPerWidth))
            : null;

    public PixelRect? Menu => Box(MenuTemplate);

    public void Invalidate()
    {
        _found.Clear();
        _allStale = true;
    }

    public void Invalidate(GameAction action)
    {
        if (ActionTemplates.TryGetValue(action, out string? template))
            Forget(template);
        if (action == GameAction.ToggleAutohunt)
            Forget(AutohuntOnTemplate);
    }

    public void InvalidateSkill(string skill)
    {
        if (SkillTemplates.TryGetValue(skill, out string? template))
            Forget(template);
    }

    /// <summary>
    /// A press found no shortcut: searched on this frame size and not there = left to the <see cref="AbsentRetryMs"/>
    /// cool-down (an LOD skill spam on an absent button would otherwise search on every press); otherwise (never searched,
    /// or forgotten since) the same as <see cref="Invalidate(GameAction)"/>.
    /// </summary>
    public void Missed(GameAction action)
    {
        if (ActionTemplates.TryGetValue(action, out string? template))
            ForgetUnlessAbsent(template);
        if (action == GameAction.ToggleAutohunt)
            ForgetUnlessAbsent(AutohuntOnTemplate);
    }

    /// <inheritdoc cref="Missed(GameAction)"/>
    public void MissedSkill(string skill)
    {
        if (SkillTemplates.TryGetValue(skill, out string? template))
            ForgetUnlessAbsent(template);
    }

    public void InvalidateJoystick() => Forget(JoystickTemplate);

    public void InvalidateMenu() => Forget(MenuTemplate);

    private void Forget(string template)
    {
        _found.Remove(template);
        _stale.Add(template);
    }

    private void ForgetUnlessAbsent(string template)
    {
        if (!_absentSince.ContainsKey(template))
            Forget(template);
    }

    private static bool NeedsColour(string template) =>
        IsHuntPicture(template) || template.Equals(AutoBuffTemplate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// HUNT must be yellow and AUTO BUFF must not (<see cref="YellowSwordsMin"/>); every other template passes. A hit
    /// whose colour cannot be told (<see cref="YellowFraction"/> null: too dark, nothing bright in its middle) is neither:
    /// in grey the two are the same swords, so a guess could tap HUNT for a buff.
    /// </summary>
    private static bool ColourFits(Mat frame, string template, PixelRect box)
    {
        // The "+" of a running autohunt sits in a yellow glow; a plain "+" (an empty slot, the ring editor) does not.
        if (template.Equals(AutohuntOnTemplate, StringComparison.OrdinalIgnoreCase))
            return GlowFraction(frame, box) >= OnGlowMin;

        bool hunt = template.Equals(AutohuntTemplate, StringComparison.OrdinalIgnoreCase);
        if (!hunt && !template.Equals(AutoBuffTemplate, StringComparison.OrdinalIgnoreCase))
            return true;
        if (YellowFraction(frame, box) is not { } yellow)
            return false;
        return hunt ? yellow >= YellowSwordsMin : yellow < YellowSwordsMin;
    }

    /// <summary>Of the middle of a hit, at least this share must be bright for its colour to count.</summary>
    public const double BrightShareMin = 0.05;

    /// <summary>
    /// Of the bright pixels in the middle 60 % of <paramref name="box"/>, the share that is yellow / gold (H 8-40, S &gt;= 90
    /// on OpenCV's 0-180 hue). Bright = V &gt;= 130, lowered to 0.6 of the middle's brightest pixel on a dimmed screen (a
    /// dialog or the ☰ panel over it; never below 60). Null = cannot tell: the middle's brightest pixel is under 60, or
    /// fewer than <see cref="BrightShareMin"/> of its pixels are bright. The middle only: the slot round the icon is
    /// see-through.
    /// </summary>
    public static double? YellowFraction(Mat frame, PixelRect box)
    {
        int insetX = box.Width / 5, insetY = box.Height / 5;
        var r = new PixelRect(box.X + insetX, box.Y + insetY, box.Width - 2 * insetX, box.Height - 2 * insetY).Clamp(frame.Width, frame.Height);
        if (r.Width < 2 || r.Height < 2)
            return null;

        using var crop = new Mat(frame, new Rectangle(r.X, r.Y, r.Width, r.Height));
        using var bgr = new Mat();
        if (crop.NumberOfChannels == 4)
            CvInvoke.CvtColor(crop, bgr, ColorConversion.Bgra2Bgr);
        else
            crop.CopyTo(bgr);
        using var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        using var value = new Mat();
        CvInvoke.ExtractChannel(hsv, value, 2);
        var (maxValue, _) = CvHelpers.MaxLoc(value);
        if (maxValue < 60)
            return null;

        double cut = Math.Min(130, Math.Max(60, maxValue * 0.6));
        using var bright = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(0, 0, cut), new MCvScalar(180, 255, 255), bright);
        int brightCount = CvInvoke.CountNonZero(bright);
        if (brightCount < BrightShareMin * r.Width * r.Height)
            return null;

        using var yellow = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(8, 90, cut), new MCvScalar(40, 255, 255), yellow);
        return CvInvoke.CountNonZero(yellow) / (double)brightCount;
    }

    /// <summary>
    /// The share of yellow / gold pixels (H 8-40, S &gt;= 90, V &gt;= 130 on OpenCV's 0-180 hue) in the ring 1.6-2.6 half-widths
    /// from <paramref name="box"/>'s centre, inside the frame: the glow round HUNT's "+" while the autohunt runs. 0 when
    /// no pixel of the ring is inside the frame.
    /// </summary>
    public static double GlowFraction(Mat frame, PixelRect box)
    {
        double half = Math.Max(box.Width, box.Height) / 2.0;
        int cx = box.CenterX, cy = box.CenterY;
        int reach = (int)Math.Ceiling(half * 2.6);
        var area = PixelRect.FromBounds(cx - reach, cy - reach, cx + reach + 1, cy + reach + 1).Clamp(frame.Width, frame.Height);
        if (area.Width < 2 || area.Height < 2)
            return 0;

        using var crop = new Mat(frame, new Rectangle(area.X, area.Y, area.Width, area.Height));
        using var bgr = new Mat();
        if (crop.NumberOfChannels == 4)
            CvInvoke.CvtColor(crop, bgr, ColorConversion.Bgra2Bgr);
        else
            crop.CopyTo(bgr);
        using var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        using var yellow = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(8, 90, 130), new MCvScalar(40, 255, 255), yellow);
        byte[] mask = CvHelpers.Bytes(yellow);

        double inner = half * 1.6, outer = half * 2.6;
        int ring = 0, gold = 0;
        for (int y = 0; y < area.Height; y++)
        {
            for (int x = 0; x < area.Width; x++)
            {
                double dx = area.X + x - cx, dy = area.Y + y - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < inner || d > outer)
                    continue;
                ring++;
                if (mask[y * area.Width + x] != 0)
                    gold++;
            }
        }

        return ring == 0 ? 0 : gold / (double)ring;
    }

    private static bool IsAttack(string template) => template.StartsWith("phone_attack", StringComparison.OrdinalIgnoreCase);

    private PixelRect? Box(string template) => _found.TryGetValue(template, out var m) ? m.Bounds : null;

    private void Search(Mat frame, string template, bool reread = false)
    {
        _found.Remove(template);
        _absentSince.Remove(template);
        if (analyzer.Library.Get(template) == null)
            return; // no crop yet: never found, never searched

        if (reread)
            HuntRereads++;
        else
            SearchCount++;
        TemplateMatch? best = null;
        var zones = ZonesFor(template, frame.Width, frame.Height);
        for (int i = 0; i < zones.Count; i++)
        {
            var roi = zones[i];
            // An attack icon on the skill wheel (its second zone) is drawn bigger and round: its wheel variant is matched there.
            string name = IsAttack(template) && i == 1 && analyzer.Library.Get(template + TemplateLibrary.WheelSuffix) != null
                ? template + TemplateLibrary.WheelSuffix
                : template;
            var hits = analyzer.FindIcons(frame, name, roi, threshold, maxResults: NeedsColour(template) ? 4 : 1)
                .Where(h => ColourFits(frame, template, h.Bounds))
                .ToList();
            if (hits.Count > 0 && (best == null || hits[0].Score > best.Score))
                best = hits[0];
        }

        if (best != null)
            _found[template] = best;
        else
            _absentSince[template] = _clock();
    }
}
