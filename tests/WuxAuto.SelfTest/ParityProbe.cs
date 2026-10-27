using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>One detector's answer on one frame, as the two sides are compared.</summary>
internal sealed record DetectorResult(bool Found, PixelRect? Box, double? Score, string? Text)
{
    public static readonly DetectorResult None = new(false, null, null, null);

    public override string ToString()
    {
        if (!Found)
            return "-";

        var parts = new List<string>();
        if (Box is { } box)
            parts.Add(box.ToString());
        if (Score is { } score)
            parts.Add(score.ToString("0.0000"));
        if (Text != null)
            parts.Add($"'{Text}'");
        return parts.Count == 0 ? "found" : string.Join(' ', parts);
    }
}

/// <summary>A decoded frame as the checks see it: size and a pixel hash. The pixels stay in the side.</summary>
internal sealed record FrameInfo(int Width, int Height, string PixelHash);

/// <summary>The minimap "x , y" read (each side has its own GameCoordinates type).</summary>
internal sealed record CoordinateRead(int X, int Y, PixelRect Bounds, string Raw);

/// <summary>
/// One vision stack behind the parity probe. Each side decodes the PNG itself and keeps the frame;
/// only plain values cross this interface (rectangles, scores, strings, bools and the shared
/// records), never an image type, so the checks do not depend on the imaging library.
/// </summary>
internal interface IVisionSide : IDisposable
{
    /// <summary>Decodes <paramref name="path"/> to BGRA, as the capture delivers frames; null when unreadable.</summary>
    FrameInfo? Load(string path);

    bool HasTemplate(string name);
    TemplateMatch? FindIcon(string template, PixelRect roi, double? threshold);
    IReadOnlyList<TemplateMatch> FindIcons(string template, PixelRect roi, double? threshold, int maxResults);
    TemplateMatch? FindIconAnyScale(string template, PixelRect roi, double threshold);
    Task<TextHit?> FindTextAsync(PixelRect roi, string text);
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(PixelRect roi, double upscale);
    Task<CoordinateRead?> ReadCoordinatesAsync();
    CompassReading? ReadCompass();
    PixelRect CompassRegion();
    PixelRect? FindSelectionRing();
    PixelRect? FindBossRing();
    PixelRect? FindBossBelt();
    double RingFraction(PixelRect icon);
    IReadOnlyList<PixelRect> FindGoldButtons();
    PixelRect? FindRedOrangeGlow(PixelRect roi, int minArea);
    Task<(PixelRect? Up, PixelRect? Down)> FindMapScrollArrowsAsync(PixelRect roi);
    Task<ConfirmDialog?> FindConfirmDialogAsync(string text, string button);
    Task<SettingRow?> ReadSettingRowAsync(string label, bool numeric);
    PixelRect FindCheckboxLeftOf(PixelRect label);
    bool IsCheckboxTicked(PixelRect box);
    PixelRect FindCloseButtonRightOf(PixelRect title);
    IReadOnlyList<PixelRect> FindBrightBlobs(PixelRect roi);
}

/// <summary>
/// --parity &lt;dir&gt;: runs every detector the pilot uses on both the frozen pre-port vision
/// (WuxAuto.Legacy) and Core's, per frame, with the inputs the pilot passes (a default
/// AppSettings: regions are always code defaults), and compares the answers: same found / not
/// found, box edges within 2 px, scores within 0.02, text equal after TextMatcher.Normalize.
/// One analyzer pair for the whole run, frames in name order and no cache resets, so the learned
/// template scale (keyed by client height) and the minimap anchor carry across frames as they do
/// in the app. Prints every difference, then the coverage and the summary line.
/// </summary>
internal sealed class ParityProbe
{
    private const int BoxTolerance = 2;
    private const double ScoreTolerance = 0.02;

    private readonly IVisionSide _legacy;
    private readonly IVisionSide _core;
    private readonly AppSettings _settings = new();
    private readonly Dictionary<string, int> _foundOn = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foundThisFrame = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private string _frame = string.Empty;
    private int _checks, _verdict, _box, _score, _text;

    private ParityProbe(IVisionSide legacy, IVisionSide core)
    {
        _legacy = legacy;
        _core = core;
    }

    private int Differences => _verdict + _box + _score + _text;

    public static async Task<int> RunAsync(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"no such folder: {dir}");
            return 1;
        }

        var paths = Directory.GetFiles(dir, "*.png")
            .Where(p => !p.EndsWith(".scan.png", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".crop.png", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            Console.WriteLine($"no frames in {dir}");
            return 1;
        }

        using var legacy = new LegacySide();
        using var core = new CoreSide();
        return await new ParityProbe(legacy, core).RunAllAsync(paths);
    }

    private async Task<int> RunAllAsync(IReadOnlyList<string> paths)
    {
        var watch = Stopwatch.StartNew();
        if (!CheckTemplates())
            return 1;
        _seen.Clear(); // coverage is per frame check, not the template list

        int frames = 0, sizeChanges = 0, unreadable = 0;
        (int Width, int Height)? last = null;
        foreach (string path in paths)
        {
            _frame = Path.GetFileName(path);
            _foundThisFrame.Clear();
            frames++;

            var a = _legacy.Load(path);
            var b = _core.Load(path);
            Record("decode", Decoded(a), Decoded(b));
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height)
            {
                unreadable++;
                continue;
            }

            if (last is { } previous && (previous.Width != a.Width || previous.Height != a.Height))
                sizeChanges++;
            last = (a.Width, a.Height);

            var frameWatch = Stopwatch.StartNew();
            await CheckFrameAsync(a.Width, a.Height);
            foreach (string name in _foundThisFrame)
                _foundOn[name] = _foundOn.GetValueOrDefault(name) + 1;
            Console.Error.WriteLine($"[{frames}/{paths.Count}] {_frame} {a.Width}x{a.Height} {frameWatch.Elapsed.TotalSeconds:0.0}s, differences so far {Differences}");
        }

        // Zero = the check ran but never found anything on either side: it compared verdicts only.
        Console.WriteLine($"coverage (frames where either side found something, of {frames - unreadable}):");
        foreach (string name in _seen.Order(StringComparer.Ordinal))
            Console.WriteLine($"  {name} {_foundOn.GetValueOrDefault(name)}");
        if (unreadable > 0)
            Console.WriteLine($"{unreadable} frame(s) not decoded by a side, or decoded to different sizes - not checked");
        Console.WriteLine($"elapsed {watch.Elapsed.TotalMinutes:0.0} min");
        Console.WriteLine($"parity: frames={frames} checks={_checks} size-changes={sizeChanges} differences={Differences} " +
                          $"(verdict={_verdict} box={_box} score={_score} text={_text})");
        return Differences == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every template the checks use must exist on the legacy side (else the check could never
    /// find anything, on either side); one missing on the new side is a difference.
    /// </summary>
    private bool CheckTemplates()
    {
        var s = _settings;
        string[] names =
        [
            "teleportscroll", "rightchevron", "leftchevron", "downchevron", "rightchevronfoc", "mapicon",
            s.Dungeon.GateTemplate, "monstericon", "finalboss", s.Sequence.RunSkillTemplate, s.Lod.ShuraTemplate,
            "flashlight", s.Dungeon.FlashlightTemplate, s.Dungeon.FlashlightTemplate + "west",
            s.Dungeon.FlashlightTemplate + "south", s.Lod.FlashlightTemplate,
        ];

        _frame = "(templates)";
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool legacy = _legacy.HasTemplate(name);
            if (!legacy)
            {
                Console.WriteLine($"probe error: the legacy side has no '{name}' template - its checks could never find anything");
                return false;
            }

            Record($"template-{name}", Present(legacy), Present(_core.HasTemplate(name)));
        }

        return true;

        static DetectorResult Present(bool present) => present ? new DetectorResult(true, null, null, null) : DetectorResult.None;
    }

    /// <summary>The checks of one frame. Every call names the pilot code it mirrors.</summary>
    private async Task CheckFrameAsync(int w, int h)
    {
        var s = _settings;
        var r = s.Regions;
        var full = new PixelRect(0, 0, w, h);

        // ---- zone title: AutomationPilot.ReadMinimapTitleAsync (zone guard, CheckZone)
        await CompareAsync("title", async side => TextOf(await MinimapTitleAsync(side, w, h)));

        // ---- minimap coordinates, then the searchlight box anchored on them (FaceNorth, RunToTarget, LOD)
        await CompareAsync("coords", async side => await side.ReadCoordinatesAsync() is { } c
            ? new DetectorResult(true, c.Bounds, null, $"x{c.X}y{c.Y}raw{c.Raw}")
            : DetectorResult.None);
        Compare("compass-region", side => Of(side.CompassRegion()));
        var (compassA, compassB) = (_legacy.ReadCompass(), _core.ReadCompass());
        Record("compass", Of(compassA), Of(compassB));
        Record("compass-angle", AngleOf(compassA), AngleOf(compassB));
        // AutomationPilot.cs:1744 FindIconAnyScale(template, CompassRegion, 0.30): north, west, south, LOD crop
        foreach (string beam in new[] { s.Dungeon.FlashlightTemplate, s.Dungeon.FlashlightTemplate + "west", s.Dungeon.FlashlightTemplate + "south", s.Lod.FlashlightTemplate })
            Compare($"searchlight-{beam}", side => Of(side.FindIconAnyScale(beam, side.CompassRegion(), 0.30)));

        // ---- skill bar: AutomationPilot.cs:1142 FindRunSkill (+ :1091 RingFraction), Lod.cs:188 FindShura
        var hotbar = r.Hotbar.ToPixels(w, h);
        var runA = _legacy.FindIcons(s.Sequence.RunSkillTemplate, hotbar, s.Sequence.RunSkillMatchScore, 4);
        var runB = _core.FindIcons(s.Sequence.RunSkillTemplate, hotbar, s.Sequence.RunSkillMatchScore, 4);
        RecordHits("runskill-hits", runA, runB);
        var (pickA, pickB) = (PilotBest(runA), PilotBest(runB));
        Record("runskill", Of(pickA), Of(pickB));
        if (pickA != null || pickB != null)
            Record("runskill-ring", RingOf(_legacy, pickA), RingOf(_core, pickB));
        var shuraA = _legacy.FindIcons(s.Lod.ShuraTemplate, hotbar, s.Lod.ShuraMatchScore, 4);
        var shuraB = _core.FindIcons(s.Lod.ShuraTemplate, hotbar, s.Lod.ShuraMatchScore, 4);
        RecordHits("shura-hits", shuraA, shuraB);
        Record("shura", Of(PilotBest(shuraA)), Of(PilotBest(shuraB)));

        // ---- teleport: AutomationPilot.cs:1462 (= :1485) scroll, :1494 loose scroll, :1525 list paging, :4730/4731 list arrows
        var scroll = r.Scroll.ToPixels(w, h);
        Compare("teleportscroll", side => Of(side.FindIcon("teleportscroll", scroll, 0.78)));
        CompareHits("teleportscroll-loose", side => side.FindIcons("teleportscroll", scroll, 0.30, 3));
        var list = r.TeleportList.ToPixels(w, h);
        CompareHits("teleport-rightchevron", side => side.FindIcons("rightchevron", list, s.Detection.TemplateThreshold, 10));
        CompareHits("teleport-leftchevron", side => side.FindIcons("leftchevron", list, s.Detection.TemplateThreshold, 10));
        CompareHits("teleport-next", side => side.FindIcons("rightchevron", list, 0.80, 3));
        CompareHits("teleport-prev", side => side.FindIcons("leftchevron", list, 0.80, 3));

        // ---- gate banner: AutomationPilot.cs:2403 FindGateBannerAsync
        var banner = r.GateBanner.ToPixels(w, h);
        Compare("gatebanner", side => Of(side.FindIcon(s.Dungeon.GateTemplate, banner, null)));

        // ---- the map: AutomationPilot.cs:2501 map button, :2465 MapWindowVisibleAsync, :2615 arrows, :2623 down chevron
        var mapIcon = r.MapIcon.ToPixels(w, h);
        Compare("mapicon", side => Of(side.FindIcon("mapicon", mapIcon, Math.Max(0.50, s.Detection.TemplateThreshold - 0.10))));
        var (openA, openB) = (await MapOpenAsync(_legacy, w, h), await MapOpenAsync(_core, w, h));
        Record("spawns", TextOf(openA.Spawns), TextOf(openB.Spawns));
        RecordHits("mapopen-rightchevron", openA.Chevrons, openB.Chevrons);
        Record("mapopen", Verdict(openA.Open), Verdict(openB.Open));
        var mapScroll = r.MapScroll.ToPixels(w, h);
        var (arrowsA, arrowsB) = (await _legacy.FindMapScrollArrowsAsync(mapScroll), await _core.FindMapScrollArrowsAsync(mapScroll));
        Record("maparrow-up", Of(arrowsA.Up), Of(arrowsB.Up));
        Record("maparrow-down", Of(arrowsA.Down), Of(arrowsB.Down));
        Compare("map-downchevron", side => Of(side.FindIcon("downchevron", mapScroll, 0.75)));

        // ---- the map's boss list: AutomationPilot.cs:822 CheckZone map-open test, :2744/2750/2758 FindFocusedChevron, :2813 row fallback
        var bossList = r.MapBossList.ToPixels(w, h);
        CompareHits("checkzone-rightchevron", side => side.FindIcons("rightchevron", bossList, 0.85, 6));
        string focused = s.Dungeon.RowChevronTemplate;
        CompareHits("row-focused", side => side.FindIcons(focused, bossList, s.Dungeon.RowChevronThreshold, 8));
        var wide = new NormRect(0.45, 0.10, 0.55, 0.85).ToPixels(w, h);
        CompareHits("row-focused-wide", side => side.FindIcons(focused, wide, s.Dungeon.RowChevronThreshold, 8));
        CompareHits("row-column", side => side.FindIcons("rightchevron", bossList, 0.55, 24));
        CompareHits("row-fallback", side => side.FindIcons("rightchevron", bossList, 0.50, 24));
        // AutomationPilot.cs:2570 ReadStage (FindTextAsync + IsFullName per stage) and :2807 FindRowGlyph
        var (stagesA, stagesB) = (await StageNamesAsync(_legacy, bossList), await StageNamesAsync(_core, bossList));
        for (int i = 0; i < s.Dungeon.Stages.Count; i++)
        {
            Record($"stage{i + 1}", TextOf(stagesA[i]), TextOf(stagesB[i]));
            if (stagesA[i] != null || stagesB[i] != null)
                Record($"rowglyph{i + 1}",
                    stagesA[i] is { } na ? Of(RowGlyph(_legacy, na.Bounds, w, h)) : DetectorResult.None,
                    stagesB[i] is { } nb ? Of(RowGlyph(_core, nb.Bounds, w, h)) : DetectorResult.None);
        }

        // ---- bosses: AutomationPilot.cs:4010 FindBossAsync, :3607 (= Lod.cs:338) target bar, :3366 (= :3699) finalboss
        var monster = r.MonsterIcon.ToPixels(w, h);
        Compare("monstericon", side => Of(side.FindIcon("monstericon", monster, null)));
        var targetBar = new NormRect(0.25, 0, 0.50, 0.16).ToPixels(w, h);
        Compare("targetbar-monstericon", side => Of(side.FindIcon("monstericon", targetBar, 0.80)));
        Compare("finalboss", side => Of(side.FindIconAnyScale("finalboss", full, 0.60)));

        // ---- colour detectors: AutomationPilot.cs:3240/3512 selection ring, Lod.cs:732/734 boss ring + belt,
        //      :1997/1998 (= :2028) gate glow bands, :4825 gold buttons
        Compare("selectionring", side => Of(side.FindSelectionRing()));
        Compare("bossring", side => Of(side.FindBossRing()));
        Compare("bossbelt", side => Of(side.FindBossBelt()));
        int glowArea = Math.Max(150, w * h / 4000);
        var upper = new NormRect(0.25, 0.07, 0.31, 0.15).ToPixels(w, h);
        var lower = new NormRect(0.25, 0.22, 0.50, 0.20).ToPixels(w, h);
        Compare("glow-upper", side => Of(side.FindRedOrangeGlow(upper, glowArea)));
        Compare("glow-lower", side => Of(side.FindRedOrangeGlow(lower, glowArea)));
        Compare("goldbuttons", side => side.FindGoldButtons() is { Count: > 0 } gold
            ? new DetectorResult(true, gold[0], null, $"n{gold.Count}")
            : DetectorResult.None);

        // ---- auto confirm: AutoConfirm (desktop) -> FindConfirmDialogAsync(AutoConfirmText, AutoConfirmButton)
        var (confirmA, confirmB) = (
            await _legacy.FindConfirmDialogAsync(s.Sequence.AutoConfirmText, s.Sequence.AutoConfirmButton),
            await _core.FindConfirmDialogAsync(s.Sequence.AutoConfirmText, s.Sequence.AutoConfirmButton));
        Record("confirm", ConfirmOf(confirmA), ConfirmOf(confirmB));
        Record("confirm-message", confirmA == null ? DetectorResult.None : Of(confirmA.MessageBounds),
            confirmB == null ? DetectorResult.None : Of(confirmB.MessageBounds));

        // ---- auto war: AutomationPilot.War.cs:46-95 (Regular War box, AUTO BUFF dialog X)
        var (warA, warB) = (await ReadWarAsync(_legacy, w, h), await ReadWarAsync(_core, w, h));
        Record("autobuff-title", OfLine(warA.Buff), OfLine(warB.Buff));
        Record("regularwar-label", Of(warA.RegularLabel), Of(warB.RegularLabel));
        if (warA.Buff != null || warB.Buff != null)
        {
            Record("autobuff-close",
                warA.Buff is { } ta ? Of(_legacy.FindCloseButtonRightOf(ta.Bounds)) : DetectorResult.None,
                warB.Buff is { } tb ? Of(_core.FindCloseButtonRightOf(tb.Bounds)) : DetectorResult.None);
        }

        var (labelsA, labelsB) = (CheckboxLabels(warA), CheckboxLabels(warB));
        for (int i = 0; i < Math.Max(labelsA.Count, labelsB.Count); i++)
        {
            var (boxA, tickA) = CheckboxOf(_legacy, i < labelsA.Count ? labelsA[i] : null);
            var (boxB, tickB) = CheckboxOf(_core, i < labelsB.Count ? labelsB[i] : null);
            Record($"checkbox#{i + 1}", boxA, boxB);
            Record($"checkbox-ticked#{i + 1}", tickA, tickB);
        }

        // ---- Settings window rows: AutomationPilot.cs:1306 "Font size", :1367 "TAB target"
        foreach (var (label, numeric) in new[] { ("Font size", true), ("TAB target", false) })
        {
            var (rowA, rowB) = (await _legacy.ReadSettingRowAsync(label, numeric), await _core.ReadSettingRowAsync(label, numeric));
            string name = "setting-" + TextMatcher.Normalize(label);
            Record(name, RowOf(rowA), RowOf(rowB));
            Record(name + "-value", Of(rowA?.Value), Of(rowB?.Value));
            Record(name + "-left", Of(rowA?.Left), Of(rowB?.Left));
            Record(name + "-right", Of(rowA?.Right), Of(rowB?.Right));
        }
    }

    // ------------------------------------------------------------------ pilot compositions (one copy, over the adapter)

    /// <summary>AutomationPilot.ReadMinimapTitleAsync with the default (Labyrinth) destination.</summary>
    private async Task<string?> MinimapTitleAsync(IVisionSide side, int w, int h)
    {
        var band = _settings.Regions.Minimap.ToPixels(w, h);
        var words = await side.RecognizeAsync(band, 2.0);
        var lines = words.Select(word => word.LineText.Trim()).Distinct().ToList();
        string destination = _settings.Sequence.Mode == RunMode.AutoLod ? _settings.Lod.Destination : _settings.Sequence.DestinationText;
        return lines.FirstOrDefault(l => ZoneMatches(l, destination))
               ?? lines.FirstOrDefault(l => l.StartsWith('[') && !l.Any(char.IsDigit) && TextMatcher.Normalize(l).Length >= 4);
    }

    /// <summary>A copy of AutomationPilot.ZoneMatches (private there).</summary>
    private static bool ZoneMatches(string? title, string destination)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        string d = TextMatcher.Normalize(destination);
        string t = TextMatcher.Normalize(title);
        if (d.Length == 0 || t.Length == 0)
            return false;
        if (t.Contains(d, StringComparison.Ordinal))
            return true;

        int need = Math.Min(d.Length, 10);
        for (int i = 0; i + need <= t.Length; i++)
        {
            int n = 0;
            while (i + n < t.Length && n < d.Length && t[i + n] == d[n])
                n++;
            if (n >= need)
                return true;
        }

        return false;
    }

    private sealed record MapOpen(TextHit? Spawns, IReadOnlyList<TemplateMatch> Chevrons, bool Open);

    /// <summary>
    /// AutomationPilot.MapWindowVisibleAsync: "Spawns" read in MapScroll at 0.90+ and a rightchevron
    /// at 0.55 in MapBossList. The chevrons are searched on every frame (the pilot only does when
    /// "Spawns" passed), so the template is compared on shut-map frames too.
    /// </summary>
    private async Task<MapOpen> MapOpenAsync(IVisionSide side, int w, int h)
    {
        var spawns = await side.FindTextAsync(_settings.Regions.MapScroll.ToPixels(w, h), "Spawns");
        var chevrons = side.FindIcons("rightchevron", _settings.Regions.MapBossList.ToPixels(w, h), 0.55, 3);
        return new MapOpen(spawns, chevrons, spawns != null && spawns.Similarity >= 0.90 && chevrons.Count >= 1);
    }

    private sealed record WarRead(IReadOnlyList<(PixelRect Bounds, string Text)> Lines, (PixelRect Bounds, string Text)? Buff, PixelRect? RegularLabel);

    /// <summary>AutomationPilot.War.cs TickAutoWarAsync: the whole-screen read, the Regular War label and the AUTO BUFF title.</summary>
    private static async Task<WarRead> ReadWarAsync(IVisionSide side, int w, int h)
    {
        var full = new PixelRect(0, 0, w, h);
        var words = await side.RecognizeAsync(full, 2.0);
        if (words.Count < 3)
            words = await side.RecognizeAsync(full, 1.0);
        var lines = words
            .GroupBy(word => word.LineBounds)
            .Select(g => (Bounds: g.Key, Text: TextMatcher.Normalize(g.First().LineText)))
            .ToList();

        PixelRect? label = null;
        var regular = lines.FirstOrDefault(l => l.Text.Length <= 14 &&
            (l.Text.StartsWith("regular", StringComparison.Ordinal) || TextMatcher.Levenshtein(l.Text, "regularwar") <= 2));
        if (regular.Text != null)
        {
            var word = words.FirstOrDefault(x => x.LineBounds == regular.Bounds &&
                                                 TextMatcher.Normalize(x.Text).StartsWith("regul", StringComparison.Ordinal));
            label = word == null ? regular.Bounds
                : new PixelRect(word.Bounds.X, regular.Bounds.Y, regular.Bounds.Right - word.Bounds.X, regular.Bounds.Height);
        }

        var buff = lines.FirstOrDefault(l => l.Text == "autobuff" || TextMatcher.Levenshtein(l.Text, "autobuff") <= 1);
        return new WarRead(lines, buff.Text == null ? null : buff, label);
    }

    /// <summary>
    /// The labels whose checkbox is looked at: the pilot's Regular War label wherever it reads (that
    /// is where the pilot looks); else, on a frame with the AUTO BUFF title (the Regular War row is
    /// under that dialog), every short row of the autohunt / auto buff windows - the 2-3 letter rows
    /// and "Smart attack" the --frame probe uses. None on other frames.
    /// </summary>
    private static List<PixelRect> CheckboxLabels(WarRead war)
    {
        if (war.RegularLabel is { } regular)
            return [regular];
        if (war.Buff == null)
            return [];

        return war.Lines
            .Where(l => l.Text.Length is >= 2 and <= 3 || l.Text == "smartattack")
            .Select(l => l.Bounds)
            .OrderBy(b => b.Y).ThenBy(b => b.X)
            .ToList();
    }

    private static (DetectorResult Box, DetectorResult Ticked) CheckboxOf(IVisionSide side, PixelRect? label)
    {
        if (label is not { } l)
            return (DetectorResult.None, DetectorResult.None);

        var box = side.FindCheckboxLeftOf(l);
        bool ticked = side.IsCheckboxTicked(box);
        return (Of(box), new DetectorResult(true, null, null, ticked ? "ticked" : "empty"));
    }

    /// <summary>AutomationPilot.FindRunSkill / FindShura: best score, near-equal scores -> the leftmost.</summary>
    private static TemplateMatch? PilotBest(IReadOnlyList<TemplateMatch> hits)
    {
        TemplateMatch? best = null;
        foreach (var hit in hits)
        {
            if (best == null || hit.Score > best.Score + 0.01 ||
                (Math.Abs(hit.Score - best.Score) <= 0.01 && hit.Bounds.CenterX < best.Bounds.CenterX))
                best = hit;
        }

        return best;
    }

    /// <summary>
    /// AutomationPilot.TickReadStageAsync: every stage name looked for in MapBossList, a whole name
    /// only (IsFullName). One OCR of the region per side - FindTextAsync is that read plus
    /// TextMatcher.FindBest, so this is the pilot's FindTextAsync per stage without re-reading.
    /// </summary>
    private async Task<TextHit?[]> StageNamesAsync(IVisionSide side, PixelRect bossList)
    {
        var words = await side.RecognizeAsync(bossList, _settings.Detection.OcrScale);
        return _settings.Dungeon.Stages
            .Select(stage => TextMatcher.FindBest(words, stage, _settings.Detection.OcrMinSimilarity) is { } hit
                             && (hit.Similarity >= 0.90 || TextMatcher.CoversMost(hit.Text, stage)) ? hit : null)
            .ToArray();
    }

    /// <summary>AutomationPilot.FindRowGlyph: the right-most text-sized bright blob right of the row's name.</summary>
    private static PixelRect? RowGlyph(IVisionSide side, PixelRect nameBounds, int w, int h)
    {
        int textH = Math.Max(10, nameBounds.Height);
        int left = Math.Min(w - 4, Math.Max(0, nameBounds.Right + 2));
        int top = Math.Max(0, nameBounds.CenterY - textH);
        int bottom = Math.Min(h, nameBounds.CenterY + textH);
        var roi = new PixelRect(left, top, Math.Max(4, w - left), Math.Max(4, bottom - top));

        PixelRect? best = null;
        foreach (var blob in side.FindBrightBlobs(roi))
        {
            if (blob.Height < textH * 0.5 || blob.Height > textH * 2.2)
                continue;
            if (blob.Width > blob.Height * 1.8)
                continue;
            if (blob.Right > roi.Right - 3)
                continue;
            if (best == null || blob.Right > best.Value.Right)
                best = blob;
        }

        return best;
    }

    // ------------------------------------------------------------------ results

    private static DetectorResult Of(TemplateMatch? match) => match == null ? DetectorResult.None : new(true, match.Bounds, match.Score, null);

    private static DetectorResult Of(PixelRect? box) => box is { } b ? new(true, b, null, null) : DetectorResult.None;

    private static DetectorResult TextOf(TextHit? hit) => hit == null ? DetectorResult.None : new(true, hit.Bounds, hit.Similarity, hit.Text);

    private static DetectorResult TextOf(string? text) => text == null ? DetectorResult.None : new(true, null, null, text);

    private static DetectorResult OfLine((PixelRect Bounds, string Text)? line) => line is { } l ? new(true, l.Bounds, null, l.Text) : DetectorResult.None;

    private static DetectorResult Verdict(bool found) => found ? new(true, null, null, null) : DetectorResult.None;

    private static DetectorResult Decoded(FrameInfo? frame) => frame == null ? DetectorResult.None
        : new(true, new PixelRect(0, 0, frame.Width, frame.Height), null, frame.PixelHash);

    private static DetectorResult Of(CompassReading? reading) => reading == null ? DetectorResult.None
        : new(true, reading.Bounds, reading.Score, reading.Method);

    /// <summary>The heading in degrees, compared like a score (0.02 degrees).</summary>
    private static DetectorResult AngleOf(CompassReading? reading) => reading == null ? DetectorResult.None
        : new(true, null, reading.AngleFromNorth, null);

    private static DetectorResult RingOf(IVisionSide side, TemplateMatch? icon) => icon == null ? DetectorResult.None
        : new(true, null, side.RingFraction(icon.Bounds), null);

    private static DetectorResult ConfirmOf(ConfirmDialog? dialog) => dialog == null ? DetectorResult.None
        : new(true, dialog.Button, null, $"{dialog.Message} via {dialog.How}");

    private static DetectorResult RowOf(SettingRow? row) => row == null ? DetectorResult.None
        : new(true, row.Label, null, $"value {row.ValueText} percent {row.Percent}");

    // ------------------------------------------------------------------ comparison

    private void Compare(string check, Func<IVisionSide, DetectorResult> probe) => Record(check, probe(_legacy), probe(_core));

    private async Task CompareAsync(string check, Func<IVisionSide, Task<DetectorResult>> probe)
    {
        var a = await probe(_legacy);
        var b = await probe(_core);
        Record(check, a, b);
    }

    private void CompareHits(string check, Func<IVisionSide, IReadOnlyList<TemplateMatch>> probe) => RecordHits(check, probe(_legacy), probe(_core));

    /// <summary>
    /// A hit list is compared hit by hit, paired by position (centres within half the template, as
    /// TemplateMatcher.SameSpot merges hits) rather than by rank: near-equal scores (every map row's
    /// '>' scores ~0.99) may come back in either order. A hit without a partner is a found / not
    /// found difference. Nothing on both sides is one matching check.
    /// </summary>
    private void RecordHits(string check, IReadOnlyList<TemplateMatch> a, IReadOnlyList<TemplateMatch> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            Record(check, DetectorResult.None, DetectorResult.None);
            return;
        }

        var unpaired = b.ToList();
        for (int i = 0; i < a.Count; i++)
        {
            var hit = a[i];
            var partner = unpaired
                .Where(m => SameSpot(hit.Bounds, m.Bounds))
                .OrderBy(m => Math.Abs(m.Bounds.CenterX - hit.Bounds.CenterX) + Math.Abs(m.Bounds.CenterY - hit.Bounds.CenterY))
                .FirstOrDefault();
            if (partner != null)
                unpaired.Remove(partner);
            Record($"{check}#{i + 1}", Of(hit), Of(partner));
        }

        for (int j = 0; j < unpaired.Count; j++)
            Record($"{check}#new{j + 1}", DetectorResult.None, Of(unpaired[j]));

        static bool SameSpot(PixelRect x, PixelRect y)
            => Math.Abs(x.CenterX - y.CenterX) <= Math.Max(5, Math.Min(x.Width, y.Width) / 2) &&
               Math.Abs(x.CenterY - y.CenterY) <= Math.Max(5, Math.Min(x.Height, y.Height) / 2);
    }

    /// <summary>
    /// One check: found / not found first (a verdict difference makes the rest meaningless), then
    /// every box edge, the score and the normalised text. Each difference is printed and counted.
    /// </summary>
    private void Record(string check, DetectorResult legacy, DetectorResult current)
    {
        _checks++;
        string family = check.Split('#')[0];
        _seen.Add(family);
        if (legacy.Found || current.Found)
            _foundThisFrame.Add(family);

        if (legacy.Found != current.Found)
        {
            _verdict++;
            Print(check, legacy.ToString(), current.ToString());
            return;
        }

        if (!legacy.Found)
            return;

        if (legacy.Box.HasValue != current.Box.HasValue ||
            (legacy.Box is { } a && current.Box is { } b &&
             (Math.Abs(a.X - b.X) > BoxTolerance || Math.Abs(a.Y - b.Y) > BoxTolerance ||
              Math.Abs(a.Right - b.Right) > BoxTolerance || Math.Abs(a.Bottom - b.Bottom) > BoxTolerance)))
        {
            _box++;
            Print(check, legacy.Box?.ToString() ?? "no box", current.Box?.ToString() ?? "no box");
        }

        if (legacy.Score.HasValue != current.Score.HasValue ||
            (legacy.Score is { } sa && current.Score is { } sb && Math.Abs(sa - sb) > ScoreTolerance))
        {
            _score++;
            Print(check, legacy.Score?.ToString("0.0000") ?? "no score", current.Score?.ToString("0.0000") ?? "no score");
        }

        if ((legacy.Text == null) != (current.Text == null) ||
            TextMatcher.Normalize(legacy.Text ?? string.Empty) != TextMatcher.Normalize(current.Text ?? string.Empty))
        {
            _text++;
            Print(check, legacy.Text == null ? "no text" : $"'{legacy.Text}'", current.Text == null ? "no text" : $"'{current.Text}'");
        }
    }

    private void Print(string check, string legacy, string current) => Console.WriteLine($"{_frame} {check} legacy={legacy} new={current}");
}

/// <summary>The frozen pre-port vision (tests/WuxAuto.SelfTest/Legacy, OpenCvSharp).</summary>
internal sealed class LegacySide : IVisionSide
{
    private readonly WuxAuto.Legacy.ScreenAnalyzer _analyzer = new(new AppSettings());
    private Mat? _frame;

    private Mat Frame => _frame ?? throw new InvalidOperationException("no frame loaded");

    public FrameInfo? Load(string path)
    {
        _frame?.Dispose();
        _frame = null;
        using var source = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (source.Empty())
            return null;

        var frame = new Mat();
        switch (source.Channels())
        {
            case 4:
                source.CopyTo(frame);
                break;
            case 3:
                Cv2.CvtColor(source, frame, ColorConversionCodes.BGR2BGRA);
                break;
            case 1:
                Cv2.CvtColor(source, frame, ColorConversionCodes.GRAY2BGRA);
                break;
            default:
                frame.Dispose();
                return null;
        }

        _frame = frame;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int height = frame.Height;
        var row = new byte[frame.Width * frame.ElemSize()];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(frame.Ptr(y), row, 0, row.Length);
            hash.AppendData(row);
        }

        return new FrameInfo(frame.Width, frame.Height, Convert.ToHexString(hash.GetHashAndReset())[..16]);
    }

    public bool HasTemplate(string name) => _analyzer.Library.TryGet(name, out _);
    public TemplateMatch? FindIcon(string template, PixelRect roi, double? threshold) => _analyzer.FindIcon(Frame, template, roi, threshold);
    public IReadOnlyList<TemplateMatch> FindIcons(string template, PixelRect roi, double? threshold, int maxResults) => _analyzer.FindIcons(Frame, template, roi, threshold, maxResults);
    public TemplateMatch? FindIconAnyScale(string template, PixelRect roi, double threshold) => _analyzer.FindIconAnyScale(Frame, template, roi, threshold).Match;
    public async Task<TextHit?> FindTextAsync(PixelRect roi, string text) => (await _analyzer.FindTextAsync(Frame, roi, text)).Hit;
    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(PixelRect roi, double upscale) => _analyzer.Ocr.RecognizeAsync(Frame, roi, upscale);

    public async Task<CoordinateRead?> ReadCoordinatesAsync()
        => await _analyzer.ReadCoordinatesAsync(Frame) is { } c ? new CoordinateRead(c.X, c.Y, c.Bounds, c.Raw) : null;

    public CompassReading? ReadCompass() => _analyzer.ReadCompass(Frame);
    public PixelRect CompassRegion() => _analyzer.CompassRegion(Frame.Width, Frame.Height);
    public PixelRect? FindSelectionRing() => _analyzer.FindSelectionRing(Frame);
    public PixelRect? FindBossRing() => _analyzer.FindBossRing(Frame);
    public PixelRect? FindBossBelt() => _analyzer.FindBossBelt(Frame);
    public double RingFraction(PixelRect icon) => _analyzer.RingFraction(Frame, icon);
    public IReadOnlyList<PixelRect> FindGoldButtons() => _analyzer.FindGoldButtons(Frame);
    public PixelRect? FindRedOrangeGlow(PixelRect roi, int minArea) => _analyzer.FindRedOrangeGlow(Frame, roi, minArea);
    public Task<(PixelRect? Up, PixelRect? Down)> FindMapScrollArrowsAsync(PixelRect roi) => _analyzer.FindMapScrollArrowsAsync(Frame, roi);
    public Task<ConfirmDialog?> FindConfirmDialogAsync(string text, string button) => _analyzer.FindConfirmDialogAsync(Frame, text, button);
    public Task<SettingRow?> ReadSettingRowAsync(string label, bool numeric) => _analyzer.ReadSettingRowAsync(Frame, label, numeric);
    public PixelRect FindCheckboxLeftOf(PixelRect label) => _analyzer.FindCheckboxLeftOf(Frame, label);
    public bool IsCheckboxTicked(PixelRect box) => _analyzer.IsCheckboxTicked(Frame, box);
    public PixelRect FindCloseButtonRightOf(PixelRect title) => _analyzer.FindCloseButtonRightOf(Frame, title);
    public IReadOnlyList<PixelRect> FindBrightBlobs(PixelRect roi) => _analyzer.FindBrightBlobs(Frame, roi);

    public void Dispose()
    {
        _frame?.Dispose();
        _analyzer.Dispose();
    }
}

/// <summary>Core's vision (src/WuxAuto.Core) with the desktop OCR engine, as the app builds it.</summary>
internal sealed class CoreSide : IVisionSide
{
    private readonly ScreenAnalyzer _analyzer = new(new AppSettings(), new OcrService());
    private Mat? _frame;

    private Mat Frame => _frame ?? throw new InvalidOperationException("no frame loaded");

    public FrameInfo? Load(string path)
    {
        _frame?.Dispose();
        _frame = null;
        using var source = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (source.Empty())
            return null;

        var frame = new Mat();
        switch (source.Channels())
        {
            case 4:
                source.CopyTo(frame);
                break;
            case 3:
                Cv2.CvtColor(source, frame, ColorConversionCodes.BGR2BGRA);
                break;
            case 1:
                Cv2.CvtColor(source, frame, ColorConversionCodes.GRAY2BGRA);
                break;
            default:
                frame.Dispose();
                return null;
        }

        _frame = frame;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int height = frame.Height;
        var row = new byte[frame.Width * frame.ElemSize()];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(frame.Ptr(y), row, 0, row.Length);
            hash.AppendData(row);
        }

        return new FrameInfo(frame.Width, frame.Height, Convert.ToHexString(hash.GetHashAndReset())[..16]);
    }

    public bool HasTemplate(string name) => _analyzer.Library.TryGet(name, out _);
    public TemplateMatch? FindIcon(string template, PixelRect roi, double? threshold) => _analyzer.FindIcon(Frame, template, roi, threshold);
    public IReadOnlyList<TemplateMatch> FindIcons(string template, PixelRect roi, double? threshold, int maxResults) => _analyzer.FindIcons(Frame, template, roi, threshold, maxResults);
    public TemplateMatch? FindIconAnyScale(string template, PixelRect roi, double threshold) => _analyzer.FindIconAnyScale(Frame, template, roi, threshold).Match;
    public async Task<TextHit?> FindTextAsync(PixelRect roi, string text) => (await _analyzer.FindTextAsync(Frame, roi, text)).Hit;
    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(PixelRect roi, double upscale) => _analyzer.Ocr.RecognizeAsync(Frame, roi, upscale);

    public async Task<CoordinateRead?> ReadCoordinatesAsync()
        => await _analyzer.ReadCoordinatesAsync(Frame) is { } c ? new CoordinateRead(c.X, c.Y, c.Bounds, c.Raw) : null;

    public CompassReading? ReadCompass() => _analyzer.ReadCompass(Frame);
    public PixelRect CompassRegion() => _analyzer.CompassRegion(Frame.Width, Frame.Height);
    public PixelRect? FindSelectionRing() => _analyzer.FindSelectionRing(Frame);
    public PixelRect? FindBossRing() => _analyzer.FindBossRing(Frame);
    public PixelRect? FindBossBelt() => _analyzer.FindBossBelt(Frame);
    public double RingFraction(PixelRect icon) => _analyzer.RingFraction(Frame, icon);
    public IReadOnlyList<PixelRect> FindGoldButtons() => _analyzer.FindGoldButtons(Frame);
    public PixelRect? FindRedOrangeGlow(PixelRect roi, int minArea) => _analyzer.FindRedOrangeGlow(Frame, roi, minArea);
    public Task<(PixelRect? Up, PixelRect? Down)> FindMapScrollArrowsAsync(PixelRect roi) => _analyzer.FindMapScrollArrowsAsync(Frame, roi);
    public Task<ConfirmDialog?> FindConfirmDialogAsync(string text, string button) => _analyzer.FindConfirmDialogAsync(Frame, text, button);
    public Task<SettingRow?> ReadSettingRowAsync(string label, bool numeric) => _analyzer.ReadSettingRowAsync(Frame, label, numeric);
    public PixelRect FindCheckboxLeftOf(PixelRect label) => _analyzer.FindCheckboxLeftOf(Frame, label);
    public bool IsCheckboxTicked(PixelRect box) => _analyzer.IsCheckboxTicked(Frame, box);
    public PixelRect FindCloseButtonRightOf(PixelRect title) => _analyzer.FindCloseButtonRightOf(Frame, title);
    public IReadOnlyList<PixelRect> FindBrightBlobs(PixelRect roi) => _analyzer.FindBrightBlobs(Frame, roi);

    public void Dispose()
    {
        _frame?.Dispose();
        _analyzer.Dispose();
    }
}
