using System.Diagnostics;
using System.Globalization;
using System.IO;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>One detector's answer on one frame, as the two sides are compared.</summary>
internal sealed record DetectorResult(bool Found, PixelRect? Box, double? Score, string? Text)
{
    public static readonly DetectorResult None = new(false, null, null, null);

    /// <summary>
    /// The template scale a match was found at (the learned scale on the learned-scale path). Not
    /// compared - printed on difference lines so a scale drift is visible.
    /// </summary>
    public double? Scale { get; init; }

    /// <summary>
    /// Compare <see cref="Text"/> as it is (ordinal) instead of after TextMatcher.Normalize: for
    /// line-key lists and counts, where Normalize would drop the separators.
    /// </summary>
    public bool ExactText { get; init; }

    /// <summary>" @x0.780" when a scale is known, else empty.</summary>
    public string ScaleSuffix => Scale is { } scale ? $" @x{scale:0.000}" : string.Empty;

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
        return (parts.Count == 0 ? "found" : string.Join(' ', parts)) + ScaleSuffix;
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
    ulong FrameSignature(PixelRect roi);

    // Auto War / LOD ring distance (main at 28763e1; the legacy side's frozen copy is Legacy/ScreenAnalyzer.War.cs)
    IReadOnlyList<(string Text, PixelRect Bounds)> ToLines(IReadOnlyList<OcrWord> words);
    IReadOnlyList<(string Template, TemplateMatch? Match)> FindSkillBarIcons(IReadOnlyList<string> templates, double minScore);
    Task<(string? Name, TemplateMatch? Chest)> ReadTargetAsync();
    Task<(int Cur, int Max)?> ReadPlayerHpAsync();
    Task<WarCountdown?> ReadWarCountdownAsync(IReadOnlyList<(string Text, PixelRect Bounds)> lines, string rowText);
    WarWindow? FindWarWindowClose(IReadOnlyList<(string Text, PixelRect Bounds)> lines);
    PixelRect? FindCloseBlobRightOf(PixelRect title);
    double DistanceFromRing(PixelRect ring);
}

/// <summary>
/// --parity &lt;dir&gt;: runs the detectors and the OCR reads of the pilot's call sites (each check
/// names the pilot method it mirrors) on both the frozen pre-port vision (WuxAuto.Legacy) and
/// Core's, per frame, with the inputs the pilot passes (a default AppSettings: regions are always
/// code defaults), and compares the answers: same found / not found, box edges within 2 px, scores
/// within 0.02, text equal after TextMatcher.Normalize (ordinal for line-key lists and counts).
/// Three checks cover detectors the pilot does not call: compass / compass-angle (ScreenAnalyzer's
/// compass detector, which only the Test scan button runs) and selectionring (main removed the
/// pilot's calls); they stay in the strict view only.
/// Auto War's detectors and reads (main at 28763e1) are checked too (CheckAutoWarAsync, CheckAutoWarReadsAsync, the
/// warmap rows); the legacy side runs main's OpenCvSharp code for them, frozen in Legacy/ScreenAnalyzer.War.cs.
/// One analyzer pair for the whole run, frames in name order and no cache resets, so the learned
/// template scale (keyed by client height) and the minimap anchor carry across frames as they do
/// in the app. Prints every difference, then the coverage, the pilot-view rules and two summary
/// lines: "pilot-view:" (hit lists compared the way the pilot consumes them, unused detectors
/// excluded) and, last, the strict "parity:" line (every check, hit lists hit by hit).
/// </summary>
internal sealed class ParityProbe
{
    private const int BoxTolerance = 2;
    private const double ScoreTolerance = 0.02;

    /// <summary>Which summary a check counts in.</summary>
    private enum View
    {
        /// <summary>Both: the strict "parity:" line and the "pilot-view:" line.</summary>
        Both,

        /// <summary>The strict line only: a raw hit list the pilot consumes through a pilot-view check, or a detector the pilot does not call.</summary>
        StrictOnly,

        /// <summary>The pilot-view line only: a hit list compared the way the pilot's call site consumes it.</summary>
        PilotOnly,
    }

    /// <summary>The detectors the pilot no longer calls: in the strict view only.</summary>
    private static readonly (string Check, string Why)[] ExcludedFromPilotView =
    [
        ("compass", "ScreenAnalyzer.ReadCompass - the compass detector only the Test scan button runs"),
        ("compass-angle", "the same reading's heading"),
        ("selectionring", "ScreenAnalyzer.FindSelectionRing - main removed the pilot's calls"),
    ];

    /// <summary>Each capped hit list (strict view, hit by hit) and how the pilot view compares it instead.</summary>
    private static readonly (string Check, string Rule)[] PilotViewRules =
    [
        ("runskill-hits", "-> runskill: FindRunSkill takes the best score, near-ties the leftmost"),
        ("shura-hits", "-> shura: FindShura, the same pick"),
        ("teleportscroll-loose", "-> teleportscroll-loose-first: TickFindScroll's failure hint reads loose[0] only"),
        ("teleport-next", "-> teleport-hasnext: TeleportListArrows asks for count > 0"),
        ("teleport-prev", "-> teleport-hasprev: TeleportListArrows asks for count > 0"),
        ("mapopen-rightchevron", "-> mapopen: MapWindowVisibleAsync asks for count >= 1 once 'Spawns' passed"),
        ("checkzone-rightchevron", "-> checkzone-mapopen: TickCheckZoneAsync asks for count >= 2"),
        ("row-focused, row-focused-wide, row-column", "-> rowchevron<N>: FindFocusedChevron's pick on the stage row read on both sides with the map open on both"),
        ("row-fallback", "-> rowfallback<N>: ClickRowChevronFallback's click point (row glyph, else the column's median x, else the name)"),
        ("teleport-rightchevron, teleport-leftchevron", "kept hit by hit in both views: TickFindChevron clicks matches[0] or the hit closest to its anchor, so every hit can be the one"),
        ("warbar-<icon>-hits", "-> warbar-<icon>: FindSkillBarIcons' pick (BestLeftmost) in WarSkillBarReady, TickWarBuffUpAsync, TickWarFindTargetAsync"),
        ("warlog-lines", "-> warlog-arrived: TickWarArriveAsync acts only on a new line that IsArrivedLine"),
        ("itemlist-lines", "-> itemlist-autobuff: TickWarItemListAsync acts on AutoBuffInItemList's verdict"),
    ];

    private readonly IVisionSide _legacy;
    private readonly IVisionSide _core;
    private readonly AppSettings _settings = new();
    private readonly Dictionary<string, int> _ranOn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _foundOn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, View> _viewOf = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ranThisFrame = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foundThisFrame = new(StringComparer.Ordinal);
    private string _frame = string.Empty;
    private int _checks, _verdict, _box, _score, _text;
    private int _pilotVerdict, _pilotBox, _pilotScore, _pilotText;

    private ParityProbe(IVisionSide legacy, IVisionSide core)
    {
        _legacy = legacy;
        _core = core;
    }

    private int Differences => _verdict + _box + _score + _text;

    private int PilotDifferences => _pilotVerdict + _pilotBox + _pilotScore + _pilotText;

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

        int frames = 0, sizeChanges = 0, unreadable = 0;
        (int Width, int Height)? last = null;
        foreach (string path in paths)
        {
            _frame = Path.GetFileName(path);
            _ranThisFrame.Clear();
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
            foreach (string name in _ranThisFrame)
                _ranOn[name] = _ranOn.GetValueOrDefault(name) + 1;
            foreach (string name in _foundThisFrame)
                _foundOn[name] = _foundOn.GetValueOrDefault(name) + 1;
            Console.Error.WriteLine($"[{frames}/{paths.Count}] {_frame} {a.Width}x{a.Height} {frameWatch.Elapsed.TotalSeconds:0.0}s, differences so far {Differences}");
        }

        // found 0 = the check ran but never found anything on either side: it compared verdicts only.
        Console.WriteLine($"coverage (per check: frames it ran on, frames where either side found something; {frames - unreadable} frame(s) checked):");
        foreach (var (name, ran) in _ranOn.OrderBy(p => p.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {name} ran {ran} found {_foundOn.GetValueOrDefault(name)}{Tag(_viewOf.GetValueOrDefault(name))}");
        if (unreadable > 0)
            Console.WriteLine($"{unreadable} frame(s) not decoded by a side, or decoded to different sizes - not checked");
        Console.WriteLine("pilot view (lines tagged [strict-only] count in the parity line only, [pilot-view] in the pilot-view line only):");
        foreach (var (check, rule) in PilotViewRules)
            Console.WriteLine($"  {check} {rule}");
        foreach (var (check, why) in ExcludedFromPilotView)
            Console.WriteLine($"  {check} excluded: {why}");
        Console.WriteLine($"elapsed {watch.Elapsed.TotalMinutes:0.0} min");
        Console.WriteLine($"pilot-view: differences={PilotDifferences} (verdict={_pilotVerdict} box={_pilotBox} score={_pilotScore} text={_pilotText}) " +
                          $"excluded={string.Join(',', ExcludedFromPilotView.Select(e => e.Check))}");
        Console.WriteLine($"parity: frames={frames} checks={_checks} size-changes={sizeChanges} differences={Differences} " +
                          $"(verdict={_verdict} box={_box} score={_score} text={_text})");
        return Differences == 0 ? 0 : 1;
    }

    private static string Tag(View view) => view switch
    {
        View.StrictOnly => " [strict-only]",
        View.PilotOnly => " [pilot-view]",
        _ => string.Empty,
    };

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
            "menubutton", .. WarIcons(),
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

    /// <summary>The checks of one frame. Every call names the pilot method it mirrors.</summary>
    private async Task CheckFrameAsync(int w, int h)
    {
        var s = _settings;
        var r = s.Regions;
        var full = new PixelRect(0, 0, w, h);

        // ---- load hash: FrameSignature.Compute(frame, whole client) in TickFindDestinationAsync (the
        //      hash before the click) and TickWaitForLoad. Exact equality: the pilot compares bit
        //      distances (3 and 5), so any changed bit matters.
        Compare("framesig", side => new DetectorResult(true, null, null, side.FrameSignature(full).ToString("X16")));

        // ---- zone title: AutomationPilot.ReadMinimapTitleAsync (zone guard, TickCheckZoneAsync)
        await CompareAsync("title", async side => TextOf(await MinimapTitleAsync(side, w, h)));

        // ---- minimap coordinates (ScreenAnalyzer.ReadCoordinatesAsync: TickFaceNorthAsync, TickRunToTargetAsync,
        //      TickCheckZoneAsync, LOD), then the searchlight box anchored on them (ScreenAnalyzer.CompassRegion)
        await CompareAsync("coords", async side => await side.ReadCoordinatesAsync() is { } c
            ? new DetectorResult(true, c.Bounds, null, $"x{c.X}y{c.Y}raw{c.Raw}")
            : DetectorResult.None);
        Compare("compass-region", side => Of(side.CompassRegion()));
        // ScreenAnalyzer.ReadCompass: not a pilot call - the compass detector is only run by the Test scan
        // button (ScreenAnalyzer.TestScanAsync). Strict view only.
        var (compassA, compassB) = (_legacy.ReadCompass(), _core.ReadCompass());
        Record("compass", Of(compassA), Of(compassB), View.StrictOnly);
        Record("compass-angle", AngleOf(compassA), AngleOf(compassB), View.StrictOnly);
        // TickFaceNorthAsync: FindIconAnyScale(template, CompassRegion, 0.30) - north, west, south, LOD crop
        foreach (string beam in new[] { s.Dungeon.FlashlightTemplate, s.Dungeon.FlashlightTemplate + "west", s.Dungeon.FlashlightTemplate + "south", s.Lod.FlashlightTemplate })
            Compare($"searchlight-{beam}", side => Of(side.FindIconAnyScale(beam, side.CompassRegion(), 0.30)));

        // ---- skill bar: FindRunSkill (+ RingFraction in TickRunSkill), FindShura (AutomationPilot.Lod.cs)
        var hotbar = r.Hotbar.ToPixels(w, h);
        var runA = _legacy.FindIcons(s.Sequence.RunSkillTemplate, hotbar, s.Sequence.RunSkillMatchScore, 4);
        var runB = _core.FindIcons(s.Sequence.RunSkillTemplate, hotbar, s.Sequence.RunSkillMatchScore, 4);
        RecordHits("runskill-hits", runA, runB, View.StrictOnly);
        var (pickA, pickB) = (PilotBest(runA), PilotBest(runB));
        Record("runskill", Of(pickA), Of(pickB));
        if (pickA != null || pickB != null)
            Record("runskill-ring", RingOf(_legacy, pickA), RingOf(_core, pickB));
        var shuraA = _legacy.FindIcons(s.Lod.ShuraTemplate, hotbar, s.Lod.ShuraMatchScore, 4);
        var shuraB = _core.FindIcons(s.Lod.ShuraTemplate, hotbar, s.Lod.ShuraMatchScore, 4);
        RecordHits("shura-hits", shuraA, shuraB, View.StrictOnly);
        Record("shura", Of(PilotBest(shuraA)), Of(PilotBest(shuraB)));

        // ---- teleport: TickOpenInventory / TickFindScroll (scroll, and the loose look-alikes of its failure
        //      hint), TickFindChevron (list paging), TeleportListArrows (TickFindDestinationAsync)
        var scroll = r.Scroll.ToPixels(w, h);
        Compare("teleportscroll", side => Of(side.FindIcon("teleportscroll", scroll, 0.78)));
        var (looseA, looseB) = (_legacy.FindIcons("teleportscroll", scroll, 0.30, 3), _core.FindIcons("teleportscroll", scroll, 0.30, 3));
        RecordHits("teleportscroll-loose", looseA, looseB, View.StrictOnly);
        Record("teleportscroll-loose-first", Of(looseA.FirstOrDefault()), Of(looseB.FirstOrDefault()), View.PilotOnly);
        var list = r.TeleportList.ToPixels(w, h);
        // TickFindChevron pages with matches[0] (the closest to the last click once there is one; the first page has none)
        foreach (string arrow in new[] { "rightchevron", "leftchevron" })
        {
            var (pageA, pageB) = (
                _legacy.FindIcons(arrow, list, s.Detection.TemplateThreshold, 10),
                _core.FindIcons(arrow, list, s.Detection.TemplateThreshold, 10));
            RecordHits($"teleport-{arrow}", pageA, pageB);
            Record($"teleport-{arrow}-first", Of(pageA.FirstOrDefault()), Of(pageB.FirstOrDefault()));
        }

        foreach (var (check, arrow) in new[] { ("teleport-next", "rightchevron"), ("teleport-prev", "leftchevron") })
        {
            var (arrowA, arrowB) = (_legacy.FindIcons(arrow, list, 0.80, 3), _core.FindIcons(arrow, list, 0.80, 3));
            RecordHits(check, arrowA, arrowB, View.StrictOnly);
            Record(check == "teleport-next" ? "teleport-hasnext" : "teleport-hasprev", Verdict(arrowA.Count > 0), Verdict(arrowB.Count > 0), View.PilotOnly);
        }

        // ---- gate banner: FindGateBannerAsync (template; its OCR path is checked with the x1.5 reads below)
        var banner = r.GateBanner.ToPixels(w, h);
        Compare("gatebanner", side => Of(side.FindIcon(s.Dungeon.GateTemplate, banner, null)));

        // ---- the map: TickClickMapAsync (map button), MapWindowVisibleAsync, TickReadStageAsync (arrows above
        //      'Spawns', then the downchevron template)
        var mapIcon = r.MapIcon.ToPixels(w, h);
        Compare("mapicon", side => Of(side.FindIcon("mapicon", mapIcon, Math.Max(0.50, s.Detection.TemplateThreshold - 0.10))));
        var (openA, openB) = (await MapOpenAsync(_legacy, w, h), await MapOpenAsync(_core, w, h));
        Record("spawns", TextOf(openA.Spawns), TextOf(openB.Spawns));
        RecordHits("mapopen-rightchevron", openA.Chevrons, openB.Chevrons, View.StrictOnly);
        Record("mapopen", Verdict(openA.Open), Verdict(openB.Open));
        var mapScroll = r.MapScroll.ToPixels(w, h);
        var (arrowsA, arrowsB) = (await _legacy.FindMapScrollArrowsAsync(mapScroll), await _core.FindMapScrollArrowsAsync(mapScroll));
        Record("maparrow-up", Of(arrowsA.Up), Of(arrowsB.Up));
        Record("maparrow-down", Of(arrowsA.Down), Of(arrowsB.Down));
        Compare("map-downchevron", side => Of(side.FindIcon("downchevron", mapScroll, 0.75)));

        // ---- the map's boss list: TickCheckZoneAsync's map-open test, FindFocusedChevron, ClickRowChevronFallback
        var bossList = r.MapBossList.ToPixels(w, h);
        var (zoneA, zoneB) = (_legacy.FindIcons("rightchevron", bossList, 0.85, 6), _core.FindIcons("rightchevron", bossList, 0.85, 6));
        RecordHits("checkzone-rightchevron", zoneA, zoneB, View.StrictOnly);
        Record("checkzone-mapopen", Verdict(zoneA.Count >= 2), Verdict(zoneB.Count >= 2), View.PilotOnly);
        var wide = new NormRect(0.45, 0.10, 0.55, 0.85).ToPixels(w, h);
        var (rowsA, rowsB) = (RowListsOf(_legacy, bossList, wide), RowListsOf(_core, bossList, wide));
        RecordHits("row-focused", rowsA.Focused, rowsB.Focused, View.StrictOnly);
        RecordHits("row-focused-wide", rowsA.FocusedWide, rowsB.FocusedWide, View.StrictOnly);
        RecordHits("row-column", rowsA.Column, rowsB.Column, View.StrictOnly);
        RecordHits("row-fallback", rowsA.Fallback, rowsB.Fallback, View.StrictOnly);
        // TickReadStageAsync (FindTextAsync + IsFullName per stage), FindRowGlyph; TickAdvanceStageAsync's re-read
        // of the row is the same read with the map open
        var (stagesA, stagesB) = (await StageNamesAsync(_legacy, bossList), await StageNamesAsync(_core, bossList));
        for (int i = 0; i < s.Dungeon.Stages.Count; i++)
        {
            Record($"stage{i + 1}", TextOf(stagesA.Names[i]), TextOf(stagesB.Names[i]));
            if (stagesA.Names[i] == null && stagesB.Names[i] == null)
                continue;

            PixelRect? glyphA = stagesA.Names[i] is { } na ? RowGlyph(_legacy, na.Bounds, w, h) : null;
            PixelRect? glyphB = stagesB.Names[i] is { } nb ? RowGlyph(_core, nb.Bounds, w, h) : null;
            Record($"rowglyph{i + 1}", Of(glyphA), Of(glyphB));

            // How the pilot consumes the row lists: only for a row it read with the map open (a verdict
            // difference in the name or the map test is already its own line).
            if (openA.Open && openB.Open && stagesA.Names[i] is { } rowA && stagesB.Names[i] is { } rowB)
            {
                Record($"rowchevron{i + 1}", Of(FocusedChevron(rowsA, rowA.Bounds)), Of(FocusedChevron(rowsB, rowB.Bounds)), View.PilotOnly);
                Record($"rowfallback{i + 1}", RowFallback(glyphA, rowsA.Fallback, rowA.Bounds), RowFallback(glyphB, rowsB.Fallback, rowB.Bounds), View.PilotOnly);
            }
        }

        // ---- bosses: FindBossAsync (Tab marker), ReadLodScreenFullAsync (target bar chest),
        //      TickSelectBossAsync / TickAutohuntConfirmAsync (finalboss)
        var monster = r.MonsterIcon.ToPixels(w, h);
        Compare("monstericon", side => Of(side.FindIcon("monstericon", monster, null)));
        var targetBar = new NormRect(0.25, 0, 0.50, 0.16).ToPixels(w, h);
        Compare("targetbar-monstericon", side => Of(side.FindIcon("monstericon", targetBar, 0.80)));
        Compare("finalboss", side => Of(side.FindIconAnyScale("finalboss", full, 0.60)));

        // ---- colour detectors: ScreenAnalyzer.FindSelectionRing (no pilot call any more - main removed them;
        //      strict view only), TickLodApproachAsync boss ring + belt, TickAimGate gate glow bands,
        //      TickReconnectAsync gold buttons
        Compare("selectionring", side => Of(side.FindSelectionRing()), View.StrictOnly);
        var (ringA, ringB) = (_legacy.FindBossRing(), _core.FindBossRing());
        Record("bossring", Of(ringA), Of(ringB));
        // AutomationPilot.Lod.cs FindApproachSpot: how far out the character is, in ring radii (ScreenAnalyzer.DistanceFromRing)
        Record("bossring-away", AwayOf(_legacy, ringA), AwayOf(_core, ringB));
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
        // AutomationPilot.WarTimer.cs TickWarJoinAsync: the same FindConfirmDialogAsync, a war dialog when its message
        // names War.Destination (WarText.IsWarDialogLine)
        Record("wardialog-confirm",
            Verdict(confirmA != null && WarText.IsWarDialogLine(confirmA.Message, s.War.Destination)),
            Verdict(confirmB != null && WarText.IsWarDialogLine(confirmB.Message, s.War.Destination)));

        // ---- regular war (both keys out): AutomationPilot.RegularWar.cs TickRegularWarAsync (Regular War box, AUTO BUFF dialog X)
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

        // ---- Settings window rows: TickFontSizeAsync, ScreenAnalyzer.ReadSettingRowAsync "Font size" and "TAB target"
        foreach (var (label, numeric) in new[] { ("Font size", true), ("TAB target", false) })
        {
            var (rowA, rowB) = (await _legacy.ReadSettingRowAsync(label, numeric), await _core.ReadSettingRowAsync(label, numeric));
            string name = "setting-" + TextMatcher.Normalize(label);
            Record(name, RowOf(rowA), RowOf(rowB));
            Record(name + "-value", Of(rowA?.Value), Of(rowB?.Value));
            Record(name + "-left", Of(rowA?.Left), Of(rowB?.Left));
            Record(name + "-right", Of(rowA?.Right), Of(rowB?.Right));
        }

        await CheckAutoWarAsync(w, h, warA, warB);
        await CheckOcrReadsAsync(w, h, openA.Open || openB.Open);

        // ---- Auto War's map pick: AutomationPilot.cs TickReadStageAsync / TickAdvanceStageAsync with IsWar ->
        //      ReadWarMonsterRowsAsync (War.cs): the MapBossList read at Detection.OcrScale (the stage names' read above),
        //      ScreenAnalyzer.ToLines, WarText.MonsterRows. War.HomeMonster is empty by default (no war map set up), so the
        //      rows are looked up for the Labyrinth's stage names - the monster names the saved map frames show - and for
        //      HomeMonster when one is set; only with the map open on a side, as the pilot reads the list.
        if (openA.Open || openB.Open)
        {
            var (linesA, linesB) = (_legacy.ToLines(stagesA.Words), _core.ToLines(stagesB.Words));
            foreach (string name in s.Dungeon.Stages.Append(s.War.HomeMonster).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct())
                Record($"warmap-{TextMatcher.Normalize(name)}", RowsOf(WarText.MonsterRows(linesA, name)), RowsOf(WarText.MonsterRows(linesB, name)));
        }
    }

    /// <summary>
    /// Auto War's frame checks (main at 28763e1, the legacy side's frozen copy in Legacy/ScreenAnalyzer.War.cs); its OCR
    /// reads that share a region and scale with the other checks are judged in <see cref="CheckOcrReadsAsync"/>.
    /// </summary>
    private async Task CheckAutoWarAsync(int w, int h, WarRead warA, WarRead warB)
    {
        var s = _settings;
        var war = s.War;
        var hotbar = s.Regions.Hotbar.ToPixels(w, h);

        // ---- the skill bar: WarSkillBarReady (War.cs: every buff icon and Shura), TickWarBuffUpAsync (WarFight.cs: one
        //      buff icon at a time), TickWarFindTargetAsync (WarFight.cs: the X buff) - ScreenAnalyzer.FindSkillBarIcons at
        //      War.IconMatchScore = per template FindIcons(Hotbar, score, 4) and BestLeftmost. One call per side with all
        //      templates: the pilot's one-template calls give the same per-template answer.
        var icons = WarIcons();
        var (barA, barB) = (_legacy.FindSkillBarIcons(icons, war.IconMatchScore), _core.FindSkillBarIcons(icons, war.IconMatchScore));
        for (int i = 0; i < icons.Count; i++)
        {
            string icon = icons[i];
            RecordHits($"warbar-{icon}-hits", _legacy.FindIcons(icon, hotbar, war.IconMatchScore, 4), _core.FindIcons(icon, hotbar, war.IconMatchScore, 4), View.StrictOnly);
            Record($"warbar-{icon}", Of(barA[i].Match), Of(barB[i].Match));
            // WarSkillBarReady's "how close it came" for a missing icon (logged only): FindIconAnyScale(Hotbar, 0.0)
            if (barA[i].Match == null && barB[i].Match == null)
                Compare($"warbar-{icon}-anyscale", side => Of(side.FindIconAnyScale(icon, hotbar, 0.0)));
        }

        // ---- the target box: ReadTargetAsync (War.cs TickWarRememberAsync; WarFight.cs TickWarFindTargetAsync, TickWarAttackAsync)
        var (targetA, targetB) = (await _legacy.ReadTargetAsync(), await _core.ReadTargetAsync());
        Record("target-name", ExactOf(targetA.Name), ExactOf(targetB.Name));
        Record("target-chest", Of(targetA.Chest), Of(targetB.Chest));

        // ---- the player's HP: ReadWarHpAsync (WarFight.cs) -> ReadPlayerHpAsync
        var (hpA, hpB) = (await _legacy.ReadPlayerHpAsync(), await _core.ReadPlayerHpAsync());
        Record("player-hp", ExactOf(hpA is { } a ? $"{a.Cur}/{a.Max}" : null), ExactOf(hpB is { } b ? $"{b.Cur}/{b.Max}" : null));

        // ---- the MENU button: TickWarOpenMenu, TickWarCloseAsync (WarTimer.cs) - FindIcon("menubutton", Regions.MenuButton,
        //      MenuButtonScore 0.70, private there)
        var menu = s.Regions.MenuButton.ToPixels(w, h);
        Compare("menubutton", side => Of(side.FindIcon("menubutton", menu, 0.70)));

        // ---- the autohunt settings: ReadHuntSettingsAsync (WarFight.cs TickWarAskAutoBuffAsync, TickWarCloseHuntSettingsAsync)
        //      - the whole-client x2 read (x1 when it reads under 3 words; the Regular War read above), ScreenAnalyzer.ToLines:
        //      the first AUTO BUFF title line (WarText.IsAutoBuffTitle) and whether a Regular War line shows; then
        //      TickWarAskAutoBuffAsync's FindCloseBlobRightOf(title) - a blob or nothing, no guessed point
        var (huntA, huntB) = (HuntSettings(_legacy, warA.Words), HuntSettings(_core, warB.Words));
        Record("huntsettings-autobuff", Of(huntA.Title), Of(huntB.Title));
        Record("huntsettings-regularwar", Verdict(huntA.RegularWar), Verdict(huntB.RegularWar));
        if (huntA.Title != null || huntB.Title != null)
        {
            Record("huntsettings-autobuff-close",
                huntA.Title is { } ta ? Of(_legacy.FindCloseBlobRightOf(ta)) : DetectorResult.None,
                huntB.Title is { } tb ? Of(_core.FindCloseBlobRightOf(tb)) : DetectorResult.None);
        }

        // ---- the item list: TickWarItemListAsync (WarFight.cs) - Regions.ItemList at x1.5, then x2 when no "[Auto buff
        //      scroll]" line read: AutoBuffInItemList's verdict and the line it names (the raw lines: strict view only)
        var (itemsA, itemsB) = (await ItemListAsync(_legacy, w, h), await ItemListAsync(_core, w, h));
        Record("itemlist-lines", KeysOf(itemsA), KeysOf(itemsB), View.StrictOnly);
        Record("itemlist-autobuff", ItemListOf(itemsA), ItemListOf(itemsB));
    }

    /// <summary>
    /// The pilot's x1.5 OCR reads: ScreenAnalyzer.FindTextAsync reads its region at
    /// Detection.OcrScale (cubic upscale, which the port changed by +-1 level) and the pilot judges
    /// the words with TextMatcher and its own rules. One read per region and side; every needle of
    /// every call site that reads the same region at the same scale is judged on it (the pilot's
    /// separate calls would OCR the same pixels again and get the same words).
    /// </summary>
    private async Task CheckOcrReadsAsync(int w, int h, bool mapOpen)
    {
        var s = _settings;
        var r = s.Regions;
        var full = new PixelRect(0, 0, w, h);

        // ---- the teleport list: TickFindDestinationAsync (Regions.Ocr), FindBest + ZoneMatches, for every
        //      destination the pilot looks for (CurrentDestination: Labyrinth, LOD, the home towns of the
        //      return teleport). Without the paging anchor (runtime state; page 1 has none).
        var destinationRoi = r.Ocr.ToPixels(w, h);
        var (destA, destB) = (await ReadAtOcrScaleAsync(_legacy, destinationRoi), await ReadAtOcrScaleAsync(_core, destinationRoi));
        foreach (string destination in Destinations())
            Record($"dest-{TextMatcher.Normalize(destination)}", TextOf(DestinationHit(destA, destination)), TextOf(DestinationHit(destB, destination)));

        // ---- the whole client (Regions.BossName is the whole client too)
        var bossRoi = r.BossName.ToPixels(w, h);
        var (screenA, screenB) = (await ReadAtOcrScaleAsync(_legacy, bossRoi), await ReadAtOcrScaleAsync(_core, bossRoi));
        // FindBossAsync (TickWaitBossAsync, TickWatchBossAsync, TickWaitForGateAsync's look at the previous
        // boss), TickCheckZoneAsync's stage resume (the highest stage whose name passes) and
        // TickRunToTargetAsync's stalled-arrival check (stage 5): StageName(i) = Stages[i], then FinalBoss,
        // each FindBest + IsFullName.
        var names = s.Dungeon.Stages.Append(s.Dungeon.FinalBoss).ToList();
        for (int i = 0; i < names.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(names[i]))
                Record($"bossname-s{i + 1}", TextOf(FullName(screenA, names[i])), TextOf(FullName(screenB, names[i])));
        }

        // AutomationPilot.Lod.cs ReadLodScreenFullAsync (FindLodBossAsync: TickLodRunAsync, the south turn and
        // TickCheckZoneAsync; TickLodAttackAsync; TickLodApproachAsync's death lines):
        // the boss (name line, the FindBest fallback, else the target bar chest), the death-text line
        // count and the combat log's "Attack succeeded" keys (a set: a key not in the set = a new hit).
        var (lodA, lodB) = (LodScreen(_legacy, screenA, w, h), LodScreen(_core, screenB, w, h));
        Record("lod-boss", lodA.Boss, lodB.Boss);
        Record("lod-deathlines", CountOf(lodA.DeathLines), CountOf(lodB.DeathLines));
        Record("lod-hitlines", KeysOf(lodA.HitLines.Distinct().Order(StringComparer.Ordinal)), KeysOf(lodB.HitLines.Distinct().Order(StringComparer.Ordinal)));

        // TickFontSizeAsync's Find() over the whole frame: SETTINGS (window open / closed), VISUAL (clicked),
        // SAVE (clicked, exact word only).
        var (windowA, windowB) = bossRoi == full ? (screenA, screenB)
            : (await ReadAtOcrScaleAsync(_legacy, full), await ReadAtOcrScaleAsync(_core, full));
        foreach (string word in new[] { "SETTINGS", "VISUAL", "SAVE" })
            Record($"settingswin-{TextMatcher.Normalize(word)}", TextOf(WindowWord(windowA, word)), TextOf(WindowWord(windowB, word)));

        // ---- the combat log (Regions.AttackMessage)
        var logRoi = r.AttackMessage.ToPixels(w, h);
        var (logA, logB) = (await ReadAtOcrScaleAsync(_legacy, logRoi), await ReadAtOcrScaleAsync(_core, logRoi));
        // ReadAttackLogAsync (TickSelectBossAsync, TickManualAttackAsync, TickAutohuntConfirmAsync,
        // TickWatchBossAsync's stage 5 watchdog): the matching lines' keys top to bottom, joined - the pilot
        // compares that string with its baseline (a change = a new hit), so the ordered keys are compared.
        Record("attacklog", KeysOf(AttackKeys(logA)), KeysOf(AttackKeys(logB)));
        // ReadAutohuntLinesAsync (EnsureAutohuntOffAsync: TickAutohuntOffAsync in town, TickWaitBossAsync at
        // the final boss): the autohunt keys top to bottom (diffed against the baseline taken before the key) ...
        var (huntA, huntB) = (AutohuntKeys(logA), AutohuntKeys(logB));
        Record("autohunt-lines", KeysOf(huntA), KeysOf(huntB));
        // ... and, at the final boss (trustVisibleLine), the state the newest visible line announces.
        Record("autohunt-visible", VisibleAutohunt(huntA), VisibleAutohunt(huntB));

        // ---- the gate banner text: FindGateBannerAsync's OCR path (Regions.GateBanner) - FindBest + IsFullName
        //      (WaitForGate), and CountTextLinesAsync's line count (WatchBoss: more lines than its baseline = open)
        var gateRoi = r.GateBanner.ToPixels(w, h);
        var (gateA, gateB) = (await ReadAtOcrScaleAsync(_legacy, gateRoi), await ReadAtOcrScaleAsync(_core, gateRoi));
        Record("gatetext", TextOf(FullName(gateA, s.Dungeon.GateText)), TextOf(FullName(gateB, s.Dungeon.GateText)));
        Record("gatetext-lines", CountOf(TextLines(gateA, s.Dungeon.GateText).Count), CountOf(TextLines(gateB, s.Dungeon.GateText).Count));

        await CheckAutoWarReadsAsync(w, h, (destA, destB), (windowA, windowB), (logA, logB));

        // ---- TickReadStageAsync's wider second look (0.32, 0.08, 0.68, 0.86) when the list region missed the
        //      name: the pilot reads stages only with the map open, so only on frames where a side sees it open.
        if (mapOpen)
        {
            var wideRoi = new NormRect(0.32, 0.08, 0.68, 0.86).ToPixels(w, h);
            var (wideA, wideB) = (await ReadAtOcrScaleAsync(_legacy, wideRoi), await ReadAtOcrScaleAsync(_core, wideRoi));
            for (int i = 0; i < s.Dungeon.Stages.Count; i++)
                Record($"stagewide{i + 1}", TextOf(FullName(wideA, s.Dungeon.Stages[i])), TextOf(FullName(wideB, s.Dungeon.Stages[i])));
        }
    }

    /// <summary>
    /// Auto War's OCR reads (main at 28763e1). A read at the scale and region another check already read is judged on
    /// those words: the teleport list's x1.5 read is the destinations' (Regions.Ocr at Detection.OcrScale 1.5), the
    /// whole-client reads at Detection.OcrScale are FindTextAsync's, the combat log's x1.5 is the attack log's.
    /// </summary>
    private async Task CheckAutoWarReadsAsync(int w, int h,
        (IReadOnlyList<OcrWord> A, IReadOnlyList<OcrWord> B) teleportList,
        (IReadOnlyList<OcrWord> A, IReadOnlyList<OcrWord> B) client,
        (IReadOnlyList<OcrWord> A, IReadOnlyList<OcrWord> B) combatLog)
    {
        var s = _settings;
        var war = s.War;
        var full = new PixelRect(0, 0, w, h);

        // ---- the teleport list: ReadTeleportListAsync (War.cs; ReadWarListPickAsync reads at TeleportListScale 1.5, 2, 1
        //      as the reads go on, TickWarListCloseAsync at the scale that read the list) - Regions.Ocr, ScreenAnalyzer.ToLines,
        //      WarText.FindWindowTitle("TELEPORT SCROLL") and the entries under it (TeleportListEntries; the picked entry's
        //      box is clicked); then TryClickListClose's FindCloseBlobRightOf(title) on the first scale that read a title.
        //      War.cs TickWarInventoryClose's FindIcon("teleportscroll", Regions.Scroll, 0.78) is the teleportscroll check.
        var listRoi = s.Regions.Ocr.ToPixels(w, h);
        PixelRect? listTitleA = null, listTitleB = null;
        foreach (double scale in new[] { 1.5, 2.0, 1.0 })
        {
            var (wordsA, wordsB) = scale == s.Detection.OcrScale
                ? (teleportList.A, teleportList.B)
                : (await _legacy.RecognizeAsync(listRoi, scale), await _core.RecognizeAsync(listRoi, scale));
            var (listA, listB) = (TeleportListOf(_legacy, wordsA), TeleportListOf(_core, wordsB));
            string tag = $"warlist-x{scale.ToString("0.0", CultureInfo.InvariantCulture)}";
            Record($"{tag}-title", OfLine(listA.Title), OfLine(listB.Title));
            for (int i = 0; i < Math.Max(listA.Entries.Count, listB.Entries.Count); i++)
            {
                Record($"{tag}-entry#{i + 1}",
                    i < listA.Entries.Count ? OfLine(listA.Entries[i]) : DetectorResult.None,
                    i < listB.Entries.Count ? OfLine(listB.Entries[i]) : DetectorResult.None);
            }

            listTitleA ??= listA.Title?.Bounds;
            listTitleB ??= listB.Title?.Bounds;
        }

        if (listTitleA != null || listTitleB != null)
        {
            Record("warlist-close",
                listTitleA is { } ta ? Of(_legacy.FindCloseBlobRightOf(ta)) : DetectorResult.None,
                listTitleB is { } tb ? Of(_core.FindCloseBlobRightOf(tb)) : DetectorResult.None);
        }

        // ---- the whole client at Detection.OcrScale (FindTextAsync's read):
        // TickWarFindTargetAsync (WarFight.cs, before the war started): War.GateText, the hit's own text covering most of it
        Record("wargate", TextOf(Covered(client.A, war.GateText)), TextOf(Covered(client.B, war.GateText)));
        // TickWarTribeTileAsync (WarTimer.cs): the War.MenuTileText tile, the hit's own text covering most of it
        Record("menutile", TextOf(Covered(client.A, war.MenuTileText)), TextOf(Covered(client.B, war.MenuTileText)));
        // TickWarCloseAsync (WarTimer.cs): that read's lines -> FindWarWindowClose (TRIBE STATUS / MENU title and its X);
        // no title but the tile text -> WarText.IsMenuTileHit (menu up -> the menu button; else left open)
        var (linesA, linesB) = (_legacy.ToLines(client.A), _core.ToLines(client.B));
        var (windowA, windowB) = (_legacy.FindWarWindowClose(linesA), _core.FindWarWindowClose(linesB));
        Record("warwindow", WarWindowOf(windowA), WarWindowOf(windowB));
        Record("warwindow-close", Of(windowA?.Close), Of(windowB?.Close));
        Record("warwindow-tile", WindowTileOf(windowA, client.A, linesA), WindowTileOf(windowB, client.B, linesB));

        // ---- the whole client at x1: TickWarReadTimerAsync (WarTimer.cs: ScreenAnalyzer.ToLines -> ReadWarCountdownAsync
        //      for War.TribeRowText - with the gapped-band x2 re-read when the whole read decides nothing - and
        //      WarText.TribeStatusShows) and TickAsync's 5 s whole-screen read (AutomationPilot.cs: a line that is the war
        //      dialog, WarText.IsWarDialogLine(War.Destination), while waiting for the war)
        var (x1A, x1B) = (await _legacy.RecognizeAsync(full, 1.0), await _core.RecognizeAsync(full, 1.0));
        var (l1A, l1B) = (_legacy.ToLines(x1A), _core.ToLines(x1B));
        Record("warcountdown", CountdownOf(await _legacy.ReadWarCountdownAsync(l1A, war.TribeRowText)),
            CountdownOf(await _core.ReadWarCountdownAsync(l1B, war.TribeRowText)));
        Record("tribestatus-shows", Verdict(WarText.TribeStatusShows(l1A, war.TribeRowText)), Verdict(WarText.TribeStatusShows(l1B, war.TribeRowText)));
        Record("wardialog-line",
            OfLine(l1A.Where(l => WarText.IsWarDialogLine(l.Text, war.Destination)).Select(l => ((string, PixelRect)?)l).FirstOrDefault()),
            OfLine(l1B.Where(l => WarText.IsWarDialogLine(l.Text, war.Destination)).Select(l => ((string, PixelRect)?)l).FirstOrDefault()));

        // ---- the combat log: ReadCombatLogAsync (War.cs, Regions.AttackMessage x1.5) -> WarBackInCaveAsync's baseline and
        //      TickWarArriveAsync: the normalised lines (WarText.NewLines against the baseline; strict view only) and the
        //      "Arrived" lines among them (WarText.IsArrivedLine, War.ArrivedText / HomeMonster)
        var logRoi = s.Regions.AttackMessage.ToPixels(w, h);
        var (logA, logB) = s.Detection.OcrScale == 1.5
            ? (combatLog.A, combatLog.B)
            : (await _legacy.RecognizeAsync(logRoi, 1.5), await _core.RecognizeAsync(logRoi, 1.5));
        var (warLogA, warLogB) = (WarLog(_legacy, logA), WarLog(_core, logB));
        Record("warlog-lines", KeysOf(warLogA), KeysOf(warLogB), View.StrictOnly);
        Record("warlog-arrived",
            KeysOf(warLogA.Where(l => WarText.IsArrivedLine(l, war.ArrivedText, war.HomeMonster))),
            KeysOf(warLogB.Where(l => WarText.IsArrivedLine(l, war.ArrivedText, war.HomeMonster))));
    }

    /// <summary>War.cs ReadTeleportListAsync on words already read: the TELEPORT SCROLL title and the entries under it.</summary>
    private static ((string Text, PixelRect Bounds)? Title, IReadOnlyList<(string Text, PixelRect Bounds)> Entries) TeleportListOf(
        IVisionSide side, IReadOnlyList<OcrWord> words)
    {
        var lines = side.ToLines(words);
        var title = WarText.FindWindowTitle(lines, WarText.TeleportScrollTitle);
        return (title, title == null ? [] : WarText.TeleportListEntries(lines, title.Value.Bounds));
    }

    /// <summary>War.cs ReadCombatLogAsync + Normalized: the combat log's lines top to bottom, each TextMatcher.Normalize'd.</summary>
    private static List<string> WarLog(IVisionSide side, IReadOnlyList<OcrWord> words)
        => side.ToLines(words).Select(l => TextMatcher.Normalize(l.Text)).ToList();

    /// <summary>A FindTextAsync hit whose own text covers most of <paramref name="text"/> (the war's tile and gate reads).</summary>
    private TextHit? Covered(IReadOnlyList<OcrWord> words, string text)
        => Best(words, text) is { } hit && TextMatcher.CoversMost(hit.Text, text) ? hit : null;

    /// <summary>
    /// WarTimer.cs TickWarCloseAsync without a window title: the tile text seen (the hit's own text covering most of it)
    /// -> "menu" (WarText.IsMenuTileHit: the menu is up) or "title-like" (left open); none otherwise.
    /// </summary>
    private DetectorResult WindowTileOf(WarWindow? open, IReadOnlyList<OcrWord> words, IReadOnlyList<(string Text, PixelRect Bounds)> lines)
    {
        string tile = _settings.War.MenuTileText;
        var hit = Best(words, tile);
        if (open != null || hit == null || !TextMatcher.CoversMost(hit.Text, tile))
            return DetectorResult.None;

        return new DetectorResult(true, hit.Bounds, null, WarText.IsMenuTileHit(hit.Text, hit.Bounds, lines, tile) ? "menu" : "title-like") { ExactText = true };
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

    private sealed record WarRead(IReadOnlyList<OcrWord> Words, IReadOnlyList<(PixelRect Bounds, string Text)> Lines,
        (PixelRect Bounds, string Text)? Buff, PixelRect? RegularLabel);

    /// <summary>
    /// AutomationPilot.RegularWar.cs TickRegularWarAsync: the whole-screen read (x2, x1 under 3 words), the Regular War
    /// label and the AUTO BUFF title. The words are also WarFight.cs ReadHuntSettingsAsync's (the same read).
    /// </summary>
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
        return new WarRead(words, lines, buff.Text == null ? null : buff, label);
    }

    /// <summary>
    /// AutomationPilot.WarFight.cs ReadHuntSettingsAsync on its whole-client read: ScreenAnalyzer.ToLines, the first
    /// line that is the AUTO BUFF title (WarText.IsAutoBuffTitle) and whether any line is the Regular War box's label.
    /// </summary>
    private static (PixelRect? Title, bool RegularWar) HuntSettings(IVisionSide side, IReadOnlyList<OcrWord> words)
    {
        var lines = side.ToLines(words);
        var title = lines.Where(l => WarText.IsAutoBuffTitle(l.Text)).Select(l => (PixelRect?)l.Bounds).FirstOrDefault();
        return (title, lines.Any(l => WarText.IsRegularWarLine(l.Text)));
    }

    /// <summary>AutomationPilot.WarFight.cs TickWarItemListAsync's read: Regions.ItemList at x1.5, then x2 unless an autobuff scroll line read.</summary>
    private async Task<List<string>> ItemListAsync(IVisionSide side, int w, int h)
    {
        var roi = _settings.Regions.ItemList.ToPixels(w, h);
        var lines = new List<string>();
        foreach (double scale in new[] { 1.5, 2.0 })
        {
            lines.AddRange(side.ToLines(await side.RecognizeAsync(roi, scale)).Select(l => l.Text));
            if (lines.Any(WarText.IsAutoBuffScrollLine))
                break;
        }

        return lines;
    }

    /// <summary>
    /// The war's skill bar templates: War.BuffIcons, Shura (WarSkillBarReady checks them all) and War.XBuffIcon
    /// (TickWarFindTargetAsync), blank ones dropped.
    /// </summary>
    private List<string> WarIcons()
        => _settings.War.BuffIcons.Append(_settings.Lod.ShuraTemplate).Append(_settings.War.XBuffIcon)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
    private async Task<(TextHit?[] Names, IReadOnlyList<OcrWord> Words)> StageNamesAsync(IVisionSide side, PixelRect bossList)
    {
        var words = await ReadAtOcrScaleAsync(side, bossList);
        return (_settings.Dungeon.Stages.Select(stage => FullName(words, stage)).ToArray(), words);
    }

    /// <summary>The read of ScreenAnalyzer.FindTextAsync: the region OCR'd at Detection.OcrScale.</summary>
    private Task<IReadOnlyList<OcrWord>> ReadAtOcrScaleAsync(IVisionSide side, PixelRect roi)
        => side.RecognizeAsync(roi, _settings.Detection.OcrScale);

    /// <summary>The hit of ScreenAnalyzer.FindTextAsync (TextMatcher.FindBest, no anchor) on words already read.</summary>
    private TextHit? Best(IReadOnlyList<OcrWord> words, string text)
        => TextMatcher.FindBest(words, text, _settings.Detection.OcrMinSimilarity);

    /// <summary>FindTextAsync + AutomationPilot.IsFullName: a hit only when most of the name was read.</summary>
    private TextHit? FullName(IReadOnlyList<OcrWord> words, string expected)
        => Best(words, expected) is { } hit && IsFullName(hit, expected) ? hit : null;

    /// <summary>A copy of AutomationPilot.IsFullName (private there).</summary>
    private static bool IsFullName(TextHit hit, string expected)
        => hit.Similarity >= 0.90 || TextMatcher.CoversMost(hit.Text, expected);

    /// <summary>
    /// The destinations TickFindDestinationAsync looks for (CurrentDestination): the Labyrinth, the
    /// LOD map, and the home town of the return teleport (any clan's, else ReturnDestination).
    /// </summary>
    private IEnumerable<string> Destinations()
        => new[] { _settings.Sequence.DestinationText, _settings.Lod.Destination }
            .Concat(_settings.Dungeon.HomeTowns)
            .Append(_settings.Dungeon.ReturnDestination)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .DistinctBy(TextMatcher.Normalize);

    /// <summary>TickFindDestinationAsync: the FindTextAsync hit, dropped unless its line or text names the start of the destination (ZoneMatches).</summary>
    private TextHit? DestinationHit(IReadOnlyList<OcrWord> words, string destination)
        => Best(words, destination) is { } hit && (ZoneMatches(hit.LineText, destination) || ZoneMatches(hit.Text, destination)) ? hit : null;

    /// <summary>TickFontSizeAsync's Find(): the FindTextAsync hit when it covers most of the word; SAVE is clicked only when it reads exactly "save".</summary>
    private TextHit? WindowWord(IReadOnlyList<OcrWord> words, string text)
    {
        var hit = Best(words, text);
        if (hit == null || !TextMatcher.CoversMost(hit.Text, text))
            return null;
        return text == "SAVE" && TextMatcher.Normalize(hit.Text) != "save" ? null : hit;
    }

    /// <summary>AutomationPilot.ReadTextLinesAsync: the distinct OCR lines that read like <paramref name="text"/>, top to bottom.</summary>
    private List<(PixelRect Bounds, string Text)> TextLines(IReadOnlyList<OcrWord> words, string text)
        => words
            .GroupBy(word => word.LineBounds)
            .Where(line => TextMatcher.Similarity(line.First().LineText, text) >= _settings.Detection.OcrMinSimilarity
                           && TextMatcher.CoversMost(line.First().LineText, text))
            .Select(line => (Bounds: line.Key, Text: line.First().LineText))
            .OrderBy(line => line.Bounds.Y)
            .ToList();

    /// <summary>AutomationPilot.ReadAttackLogAsync before the join: the "Attack succeeded" lines' keys, top to bottom.</summary>
    private List<string> AttackKeys(IReadOnlyList<OcrWord> words)
    {
        string text = _settings.Dungeon.AttackConfirmText;
        return TextLines(words, text).Select(line => AttackLineKey(line.Text, text)).ToList();
    }

    /// <summary>A copy of AutomationPilot.AttackLineKey (private there): "attacksucceeded...damage(N)".</summary>
    private static string AttackLineKey(string line, string text)
    {
        string start = TextMatcher.Normalize(text);
        string value = TextMatcher.Normalize(line);
        int from = value.IndexOf(start, StringComparison.Ordinal);
        if (from > 0)
            value = value[from..];
        int tail = value.IndexOf("attrib", StringComparison.Ordinal);
        return tail > 0 ? value[..tail] : value;
    }

    /// <summary>AutomationPilot.ReadAutohuntLinesAsync: the combat log's autohunt keys, top to bottom.</summary>
    private List<string> AutohuntKeys(IReadOnlyList<OcrWord> words)
    {
        var seq = _settings.Sequence;
        string? prefix = AutohuntLog.Prefix(seq.AutohuntStartedText, seq.AutohuntEndedText);
        if (prefix == null)
            return [];

        var keys = new List<string>();
        foreach (var line in words.GroupBy(word => word.LineBounds).OrderBy(g => g.Key.Y))
        {
            if (AutohuntLog.Key(line.First().LineText, prefix) is { } key)
                keys.Add(key);
        }

        return keys;
    }

    /// <summary>EnsureAutohuntOffAsync with trustVisibleLine (final boss): the state the newest visible autohunt line announces.</summary>
    private DetectorResult VisibleAutohunt(IReadOnlyList<string> keys)
    {
        var seq = _settings.Sequence;
        bool? on = AutohuntLog.NewState([], keys, seq.AutohuntStartedText, seq.AutohuntEndedText, out string? line, seq.AutohuntStoppedText);
        return on is { } state ? new DetectorResult(true, null, null, $"{(state ? "on" : "off")} {line}") { ExactText = true } : DetectorResult.None;
    }

    private sealed record LodRead(DetectorResult Boss, int DeathLines, IReadOnlyList<string> HitLines);

    /// <summary>
    /// AutomationPilot.Lod.cs ReadLodScreenFullAsync on the whole-client x1.5 read: the boss (a short
    /// line naming it, else the FindBest hit if it is a whole name on a short line, else the target bar's
    /// treasure chest), the death-text line count, and the "Attack succeeded" keys of the lines inside
    /// Regions.AttackMessage.
    /// </summary>
    private LodRead LodScreen(IVisionSide side, IReadOnlyList<OcrWord> words, int w, int h)
    {
        var lod = _settings.Lod;
        if (string.IsNullOrWhiteSpace(lod.BossName))
            return new LodRead(DetectorResult.None, 0, []);

        var hit = Best(words, lod.BossName);
        var lines = words.GroupBy(word => word.LineBounds).Select(g => (Bounds: g.Key, Text: g.First().LineText)).ToList();

        int deathLines = string.IsNullOrWhiteSpace(lod.DeathText) ? 0 : lines
            .Count(line => TextMatcher.Normalize(line.Text).Contains(TextMatcher.Normalize(lod.DeathText), StringComparison.Ordinal)
                           || TextMatcher.Similarity(line.Text, lod.DeathText) >= 0.85);

        string name = TextMatcher.Normalize(lod.BossName);
        int maxLetters = name.Length + 10;
        TextHit? boss = lines
            .Where(line => TextMatcher.Normalize(line.Text).Length <= maxLetters)
            .Where(line => TextMatcher.Normalize(line.Text).Contains(name, StringComparison.Ordinal)
                           || TextMatcher.Similarity(line.Text, lod.BossName) >= 0.85)
            .Select(line => new TextHit(line.Text, 1.0, line.Bounds, line.Text, line.Bounds, 0))
            .FirstOrDefault();
        if (boss == null && hit != null && TextMatcher.Normalize(hit.LineText).Length <= maxLetters
            && (IsFullName(hit, lod.BossName) || TextMatcher.CoversMost(hit.LineText, lod.BossName)))
            boss = hit;

        var bossResult = TextOf(boss);
        if (boss == null)
        {
            var bar = new NormRect(0.25, 0, 0.50, 0.16).ToPixels(w, h);
            if (side.FindIcon("monstericon", bar, 0.80) is { } chest)
                bossResult = new DetectorResult(true, chest.Bounds, chest.Score, "treasure icon") { Scale = chest.Scale };
        }

        string hitText = _settings.Dungeon.AttackConfirmText;
        var log = _settings.Regions.AttackMessage.ToPixels(w, h);
        var hitLines = string.IsNullOrWhiteSpace(hitText) ? [] : lines
            .Where(line => log.Contains(line.Bounds.CenterX, line.Bounds.CenterY))
            .Where(line => TextMatcher.Similarity(line.Text, hitText) >= _settings.Detection.OcrMinSimilarity
                           && TextMatcher.CoversMost(line.Text, hitText))
            .Select(line => AttackLineKey(line.Text, hitText))
            .ToList();

        return new LodRead(bossResult, deathLines, hitLines);
    }

    private sealed record RowLists(
        IReadOnlyList<TemplateMatch> Focused,
        IReadOnlyList<TemplateMatch> FocusedWide,
        IReadOnlyList<TemplateMatch> Column,
        IReadOnlyList<TemplateMatch> Fallback);

    /// <summary>
    /// The map list's '>' searches of FindFocusedChevron (the highlighted row's '>' in MapBossList, then
    /// in a wider band; the plain '>' column) and ClickRowChevronFallback (the plain '>' at 0.50).
    /// </summary>
    private RowLists RowListsOf(IVisionSide side, PixelRect bossList, PixelRect wide)
    {
        string focused = _settings.Dungeon.RowChevronTemplate;
        double threshold = _settings.Dungeon.RowChevronThreshold;
        return new RowLists(
            side.FindIcons(focused, bossList, threshold, 8),
            side.FindIcons(focused, wide, threshold, 8),
            side.FindIcons("rightchevron", bossList, 0.55, 24),
            side.FindIcons("rightchevron", bossList, 0.50, 24));
    }

    /// <summary>
    /// AutomationPilot.FindFocusedChevron: the highlighted '>' (wide band when the list has none), kept
    /// to the '>' column (median x of 3+ plain hits), the one nearest the row inside half the row
    /// spacing; none on the row -> the column at the row's height (score 0).
    /// </summary>
    private TemplateMatch? FocusedChevron(RowLists rows, PixelRect rowBounds)
    {
        var hits = rows.Focused.Count > 0 ? rows.Focused : rows.FocusedWide;
        if (hits.Count == 0)
            return null;

        var columnHits = rows.Column;
        int? columnX = null;
        if (columnHits.Count >= 3)
        {
            var xs = columnHits.Select(m => m.Bounds.CenterX).OrderBy(v => v).ToArray();
            columnX = xs[xs.Length / 2];
            int slack = Math.Max(12, (int)(columnX.Value * 0.02));
            var nearColumn = hits.Where(m => Math.Abs(m.Bounds.CenterX - columnX.Value) <= slack).ToList();
            if (nearColumn.Count > 0)
                hits = nearColumn;
        }

        int band = Math.Max(12, rowBounds.Height * 2);
        var ys = columnHits.Select(m => m.Bounds.CenterY).OrderBy(v => v).ToArray();
        var gaps = ys.Zip(ys.Skip(1), (a, b) => b - a).Where(d => d > 5).OrderBy(d => d).ToArray();
        if (gaps.Length > 0)
            band = Math.Max(6, gaps[gaps.Length / 2] * 45 / 100);

        var onRow = hits.Where(m => Math.Abs(m.Bounds.CenterY - rowBounds.CenterY) <= band).ToList();
        if (onRow.Count > 0)
            return onRow.OrderBy(m => Math.Abs(m.Bounds.CenterY - rowBounds.CenterY)).ThenByDescending(m => m.Score).First();

        if (columnX == null)
            return null;

        var like = hits[0].Bounds;
        var bounds = new PixelRect(columnX.Value - like.Width / 2, rowBounds.CenterY - like.Height / 2, like.Width, like.Height);
        return new TemplateMatch(_settings.Dungeon.RowChevronTemplate, 0, bounds, hits[0].Scale);
    }

    /// <summary>
    /// AutomationPilot.ClickRowChevronFallback's click point: the row glyph's centre, else the median x
    /// of the plain '>' hits right of the name at the row's height, else the name's centre. The box is
    /// the 1x1 click point (2 px tolerance); the text says which one.
    /// </summary>
    private static DetectorResult RowFallback(PixelRect? glyph, IReadOnlyList<TemplateMatch> fallback, PixelRect nameBounds)
    {
        int rowY = nameBounds.CenterY;
        if (glyph is { } g)
            return ClickAt(g.CenterX, g.CenterY, "glyph");

        var hits = fallback.Where(m => m.Bounds.CenterX > nameBounds.CenterX + 4).ToList();
        if (hits.Count > 0)
        {
            var xs = hits.Select(m => m.Bounds.CenterX).OrderBy(x => x).ToArray();
            return ClickAt(xs[xs.Length / 2], rowY, "column");
        }

        return ClickAt(nameBounds.CenterX, rowY, "name");

        static DetectorResult ClickAt(int x, int y, string how) => new(true, new PixelRect(x, y, 1, 1), null, how);
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

    private static DetectorResult Of(TemplateMatch? match) => match == null ? DetectorResult.None
        : new(true, match.Bounds, match.Score, null) { Scale = match.Scale };

    private static DetectorResult Of(PixelRect? box) => box is { } b ? new(true, b, null, null) : DetectorResult.None;

    private static DetectorResult TextOf(TextHit? hit) => hit == null ? DetectorResult.None : new(true, hit.Bounds, hit.Similarity, hit.Text);

    private static DetectorResult TextOf(string? text) => text == null ? DetectorResult.None : new(true, null, null, text);

    private static DetectorResult OfLine((PixelRect Bounds, string Text)? line) => line is { } l ? new(true, l.Bounds, null, l.Text) : DetectorResult.None;

    private static DetectorResult OfLine((string Text, PixelRect Bounds)? line) => line is { } l ? new(true, l.Bounds, null, l.Text) : DetectorResult.None;

    /// <summary>A string compared exactly (ordinal): none = not found.</summary>
    private static DetectorResult ExactOf(string? text) => text == null ? DetectorResult.None : new(true, null, null, text) { ExactText = true };

    /// <summary>ScreenAnalyzer.DistanceFromRing of a side's own ring, compared like a score (0.02 ring radii).</summary>
    private static DetectorResult AwayOf(IVisionSide side, PixelRect? ring) => ring is { } r ? new(true, null, side.DistanceFromRing(r), null) : DetectorResult.None;

    /// <summary>WarText.MonsterRows: none = not on the list; else the first row's box and similarity, the count and the names.</summary>
    private static DetectorResult RowsOf(IReadOnlyList<WarText.MonsterRow> rows) => rows.Count == 0 ? DetectorResult.None
        : new(true, rows[0].Bounds, rows[0].Similarity, $"n{rows.Count} {string.Join("|", rows.Select(r => r.Name))}") { ExactText = true };

    /// <summary>AutomationPilot.AutoBuffInItemList's verdict (Unread = none) and the autobuff scroll line it names.</summary>
    private static DetectorResult ItemListOf(IReadOnlyList<string> lines)
    {
        var verdict = AutomationPilot.AutoBuffInItemList(lines);
        if (verdict == AutomationPilot.WarItemList.Unread)
            return DetectorResult.None;

        string? line = lines.FirstOrDefault(WarText.IsAutoBuffScrollLine);
        return new(true, null, null, line == null ? $"{verdict}" : $"{verdict}: {line}") { ExactText = true };
    }

    /// <summary>ReadWarCountdownAsync: "in progress" or the time left, exact; none = cannot tell.</summary>
    private static DetectorResult CountdownOf(WarCountdown? countdown) => countdown switch
    {
        { InProgress: true } => ExactOf("in progress"),
        { Remaining: { } left } => ExactOf(left.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)),
        _ => DetectorResult.None,
    };

    /// <summary>FindWarWindowClose: the window's title box and "window: title as read" (its X is the -close check).</summary>
    private static DetectorResult WarWindowOf(WarWindow? window) => window == null ? DetectorResult.None
        : new(true, window.TitleBounds, null, $"{window.Window}: {window.Title}");

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

    /// <summary>A line count the pilot compares with a baseline: none = not found, else the number (exact).</summary>
    private static DetectorResult CountOf(int count) => count == 0 ? DetectorResult.None
        : new(true, null, null, count.ToString(CultureInfo.InvariantCulture)) { ExactText = true };

    /// <summary>Line keys joined with '|' (compared exactly): none = not found, as the pilot's empty log string.</summary>
    private static DetectorResult KeysOf(IEnumerable<string> keys)
    {
        string joined = string.Join("|", keys);
        return joined.Length == 0 ? DetectorResult.None : new(true, null, null, joined) { ExactText = true };
    }

    // ------------------------------------------------------------------ comparison

    private void Compare(string check, Func<IVisionSide, DetectorResult> probe, View view = View.Both) => Record(check, probe(_legacy), probe(_core), view);

    private async Task CompareAsync(string check, Func<IVisionSide, Task<DetectorResult>> probe)
    {
        var a = await probe(_legacy);
        var b = await probe(_core);
        Record(check, a, b);
    }

    /// <summary>
    /// A hit list is compared hit by hit, paired by position (centres within half the template, as
    /// TemplateMatcher.SameSpot merges hits) rather than by rank: near-equal scores (every map row's
    /// '>' scores ~0.99) may come back in either order. A hit without a partner is a found / not
    /// found difference. Nothing on both sides is one matching check.
    /// </summary>
    private void RecordHits(string check, IReadOnlyList<TemplateMatch> a, IReadOnlyList<TemplateMatch> b, View view = View.Both)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            Record(check, DetectorResult.None, DetectorResult.None, view);
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
            Record($"{check}#{i + 1}", Of(hit), Of(partner), view);
        }

        for (int j = 0; j < unpaired.Count; j++)
            Record($"{check}#new{j + 1}", DetectorResult.None, Of(unpaired[j]), view);

        static bool SameSpot(PixelRect x, PixelRect y)
            => Math.Abs(x.CenterX - y.CenterX) <= Math.Max(5, Math.Min(x.Width, y.Width) / 2) &&
               Math.Abs(x.CenterY - y.CenterY) <= Math.Max(5, Math.Min(x.Height, y.Height) / 2);
    }

    /// <summary>
    /// One check: found / not found first (a verdict difference makes the rest meaningless), then
    /// every box edge, the score and the text (normalised, or exact for <see cref="DetectorResult.ExactText"/>).
    /// Each difference is printed and counted in the summaries <paramref name="view"/> names; a line
    /// that counts in one summary only is tagged.
    /// </summary>
    private void Record(string check, DetectorResult legacy, DetectorResult current, View view = View.Both)
    {
        bool strict = view != View.PilotOnly;
        bool pilot = view != View.StrictOnly;
        if (strict)
            _checks++;
        string family = check.Split('#')[0];
        _viewOf[family] = view;
        _ranThisFrame.Add(family);
        if (legacy.Found || current.Found)
            _foundThisFrame.Add(family);

        if (legacy.Found != current.Found)
        {
            Count(ref _verdict, ref _pilotVerdict);
            Print(check, legacy.ToString(), current.ToString(), view);
            return;
        }

        if (!legacy.Found)
            return;

        if (legacy.Box.HasValue != current.Box.HasValue ||
            (legacy.Box is { } a && current.Box is { } b &&
             (Math.Abs(a.X - b.X) > BoxTolerance || Math.Abs(a.Y - b.Y) > BoxTolerance ||
              Math.Abs(a.Right - b.Right) > BoxTolerance || Math.Abs(a.Bottom - b.Bottom) > BoxTolerance)))
        {
            Count(ref _box, ref _pilotBox);
            Print(check, (legacy.Box?.ToString() ?? "no box") + legacy.ScaleSuffix, (current.Box?.ToString() ?? "no box") + current.ScaleSuffix, view);
        }

        if (legacy.Score.HasValue != current.Score.HasValue ||
            (legacy.Score is { } sa && current.Score is { } sb && Math.Abs(sa - sb) > ScoreTolerance))
        {
            Count(ref _score, ref _pilotScore);
            Print(check, (legacy.Score?.ToString("0.0000") ?? "no score") + legacy.ScaleSuffix,
                (current.Score?.ToString("0.0000") ?? "no score") + current.ScaleSuffix, view);
        }

        bool exact = legacy.ExactText || current.ExactText;
        if ((legacy.Text == null) != (current.Text == null) ||
            (exact
                ? !string.Equals(legacy.Text, current.Text, StringComparison.Ordinal)
                : TextMatcher.Normalize(legacy.Text ?? string.Empty) != TextMatcher.Normalize(current.Text ?? string.Empty)))
        {
            Count(ref _text, ref _pilotText);
            Print(check, legacy.Text == null ? "no text" : $"'{legacy.Text}'", current.Text == null ? "no text" : $"'{current.Text}'", view);
        }

        void Count(ref int strictCount, ref int pilotCount)
        {
            if (strict)
                strictCount++;
            if (pilot)
                pilotCount++;
        }
    }

    private void Print(string check, string legacy, string current, View view)
        => Console.WriteLine($"{_frame} {check} legacy={legacy} new={current}{Tag(view)}");
}
