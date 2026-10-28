using System.Diagnostics;
using System.Drawing;
using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>
/// Auto War screen readers over a folder of saved frames, one line per PNG:
/// --hp (player HP), --target (target name + chest), --warbar (buff, weapon and item buff icons + Shura on the skill bar),
/// --menu (the top-centre MENU button, the "Tribe Status" tile), --tribe (the war row's countdown).
/// </summary>
internal static class WarProbe
{
    /// <summary>The probes' frames: every PNG in a folder except the --frame probe's .scan / .crop copies.</summary>
    private static IEnumerable<string> Frames(string dir)
        => Directory.GetFiles(dir, "*.png").Where(p => !p.EndsWith(".scan.png") && !p.EndsWith(".crop.png")).Order();

    /// <summary>A saved frame as BGRA (what the capture hands the pilot); null when it cannot be read.</summary>
    private static Mat? Load(string path)
    {
        using var source = ImageFile.Read(path, ImreadModes.ColorBgr);
        if (source.IsEmpty)
            return null;
        var frame = new Mat();
        CvInvoke.CvtColor(source, frame, ColorConversion.Bgr2Bgra);
        return frame;
    }

    private static string Box(PixelRect r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

    /// <summary>
    /// A synthetic BGRA image: <paramref name="colour"/>, white text drawn by <paramref name="draw"/>, opaque. The text
    /// goes on a BGR canvas first: Emgu CV 5's OpenCV 5.0 putText (its own fonts, not 4.x's Hershey strokes) blends
    /// the alpha channel of a BGRA image, so anti-aliased text drawn there straight is nearly transparent and Windows
    /// OCR reads garbage (2026-10-28: "Bohai Outpost (Adept)" read as "OBchEi OL8]l").
    /// </summary>
    private static Mat Synthetic(int rows, int cols, MCvScalar colour, Action<Mat> draw)
    {
        using var canvas = new Mat(rows, cols, DepthType.Cv8U, 3);
        canvas.SetTo(colour);
        draw(canvas);
        var image = new Mat();
        CvInvoke.CvtColor(canvas, image, ColorConversion.Bgr2Bgra);
        return image;
    }

    /// <summary>
    /// --hp &lt;dir&gt;: the player's HP as ScreenAnalyzer.ReadPlayerHpAsync reads it and which read gave it (the
    /// white text of Regions.PlayerHp at x2, or the plain x2 fallback), plus the white read's lines with a '/'
    /// - every line when nothing was read. The summary counts frames whose region holds a "0 / N" line in the
    /// white read (the "0/3000" bar must stay outside it).
    /// </summary>
    public static async Task<int> HpAsync(string dir)
    {
        var settings = SettingsStore.Load(persistMigration: false);
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        int read = 0, byPlain = 0, full = 0, zeroLines = 0, total = 0;
        foreach (string path in Frames(dir))
        {
            using var frame = Load(path);
            if (frame == null)
                continue;

            total++;
            var watch = Stopwatch.StartNew();
            var hp = await analyzer.ReadPlayerHpAsync(frame);
            long ms = watch.ElapsedMilliseconds;
            if (hp != null)
                read++;
            if (hp != null && hp.Value.Cur == hp.Value.Max)
                full++;

            var roi = settings.Regions.PlayerHp.ToPixels(frame.Width, frame.Height);
            var lines = await analyzer.OcrWhiteTextAsync(frame, roi, 2.0);
            if (lines.Any(l => WarText.ParseHp([l]) is { Cur: 0 }))
                zeroLines++;
            string via = "";
            if (hp != null)
            {
                bool white = WarText.ParseHp(lines) == hp;
                if (!white)
                    byPlain++;
                via = white ? " (white x2)" : " (plain x2)";
            }

            var shown = lines
                .Where(l => hp == null || l.Text.Contains('/'))
                .Select(l => $"'{l.Text}' {Box(l.Bounds)}");
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | hp {(hp == null ? "not found" : $"{hp.Value.Cur} / {hp.Value.Max}{via}")} ({ms} ms) | {(hp == null ? "lines" : "white slash lines")}: {string.Join(" ; ", shown)}");
        }

        Console.WriteLine($"{read}/{total} frames with an HP read ({read - byPlain} by the white read, {byPlain} by the plain fallback; {full} with cur = max); {zeroLines} with a '0 / N' line in the region");
        return 0;
    }

    /// <summary>
    /// --target &lt;dir&gt;: the Auto War fight's read of the target box (ScreenAnalyzer.ReadWarTargetAsync) and which read
    /// decided - x1.5, white x2, red x2 (a name), numbers (the HP bar's "N / M" line alone), chest, none - next to the
    /// remember step's ScreenAnalyzer.ReadTargetAsync (name, unchanged). A frame without a name or the chest also prints
    /// every "N / M" line the three reads see in the target box, those outside its centre band (WarText.TargetHpLine)
    /// marked: one inside the band on a frame without a target would be a target that is not one. A named target is
    /// checked the other way: would its numbers line alone (any read) have seen it - if not, every '/' line of the reads.
    /// </summary>
    public static async Task<int> TargetAsync(string dir)
    {
        var settings = SettingsStore.Load(persistMigration: false);
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        int named = 0, namedWithNumbers = 0, namedNumbersAlone = 0, numbersOnly = 0, chests = 0, chestOnly = 0, outsideBand = 0, oldNamed = 0, total = 0;
        var byVia = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in Frames(dir))
        {
            using var frame = Load(path);
            if (frame == null)
                continue;

            total++;
            var watch = Stopwatch.StartNew();
            var target = await analyzer.ReadWarTargetAsync(frame);
            long ms = watch.ElapsedMilliseconds;
            var (oldName, _) = await analyzer.ReadTargetAsync(frame);
            if (oldName != null)
                oldNamed++;
            if (target.Chest != null)
                chests++;
            if (target.Name != null)
                named++;
            else if (target.HpLine != null)
                numbersOnly++;
            else if (target.Chest != null)
                chestOnly++;
            if (target.Name != null && target.HpLine != null)
                namedWithNumbers++;
            byVia[target.Via] = byVia.GetValueOrDefault(target.Via) + 1;

            string numbers = "";
            var roi = settings.Regions.TargetBox.ToPixels(frame.Width, frame.Height);
            var reads = new (string Read, IReadOnlyList<(string Text, PixelRect Bounds)> Lines)[]
            {
                ("x1.5", ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, roi, 1.5))),
                ("white x2", await analyzer.OcrWhiteTextAsync(frame, roi, 2.0)),
                ("red x2", await analyzer.OcrRedTextAsync(frame, roi, 2.0)),
            };
            if (target.Name != null)
            {
                // A named target: would its numbers line alone (any of the three reads) have done? If not, its '/' lines.
                if (reads.Any(r => WarText.TargetHpLine(r.Lines, roi) != null))
                {
                    namedNumbersAlone++;
                }
                else
                {
                    var slash = reads.SelectMany(r => r.Lines.Where(l => l.Text.Contains('/')).Select(l => $"{r.Read} '{l.Text}' {Box(l.Bounds)}")).ToList();
                    numbers = $" | numbers alone would miss it; '/' lines: {(slash.Count == 0 ? "none" : string.Join(" ; ", slash))}";
                }
            }

            if (target.Name == null && target.Chest == null)
            {
                var whole = new PixelRect(0, 0, frame.Width, frame.Height);
                var found = reads
                    .SelectMany(r => r.Lines.Where(l => WarText.TargetHpLine([l], whole) != null)
                        .Select(l => $"{r.Read} '{l.Text}' {Box(l.Bounds)}{(WarText.TargetHpLine([l], roi) == null ? " (outside the band)" : "")}"))
                    .ToList();
                if (found.Count > 0)
                    numbers = $" | numbers lines: {string.Join(" ; ", found)}";
                if (target.HpLine == null && found.Count > 0)
                    outsideBand++;
            }

            string name = target.Name == null ? "not found" : $"'{target.Name}'";
            string hp = target.HpLine is { } line ? $"'{line.Text}' {Box(line.Bounds)}" : "-";
            string chest = target.Chest == null ? "-" : $"{target.Chest.Score:0.000} at {Box(target.Chest.Bounds)}";
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | {(target.Seen ? "TARGET" : "none")} by {target.Via} | name {name} | hp {hp} | chest {chest} | old name {(oldName == null ? "-" : $"'{oldName}'")} ({ms} ms){numbers}");
        }

        Console.WriteLine($"{total} frames: {named} named ({namedWithNumbers} with the numbers line in the band in the deciding reads, {namedNumbersAlone} in any of the three reads), {numbersOnly} by the numbers alone, "
                          + $"{chestOnly} by the chest alone, {chests} with the chest; {outsideBand} without a target but with a numbers line outside the band; "
                          + $"old ReadTargetAsync named {oldNamed}; decided by: {string.Join(", ", byVia.Select(kv => $"{kv.Key} {kv.Value}"))}");
        return 0;
    }

    /// <summary>
    /// --warui &lt;dir&gt;: where the UI text lies, per client size, from whole-frame OCR x1 lines sorted by their words - the
    /// combat log (attack / damage / autohunt / arrived lines, left half), the chat tabs, the item list (time(s) / min(s) /
    /// dates, right half), the event notices ("Event", "Exp x") and the minimap title / coordinates (top right): the union
    /// box of each, in pixels and as fractions. What the walk's UI rectangles (AutomationPilot.WarWalkBands) are measured from.
    /// </summary>
    public static async Task<int> UiAsync(string dir)
    {
        var settings = SettingsStore.Load(persistMigration: false);
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        var kinds = new (string Name, Func<string, PixelRect, int, int, bool> Is)[]
        {
            ("log", (t, b, w, h) => b.CenterX < w / 2 && System.Text.RegularExpressions.Regex.IsMatch(t, @"attack|damage|auto ?hunt|arrived|succeeded", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
            ("chat tabs", (t, b, w, h) => b.CenterX < w / 2 && System.Text.RegularExpressions.Regex.IsMatch(t, @"GENERAL|WHISPER|PARTY")),
            ("items", (t, b, w, h) => b.CenterX > w / 2 && System.Text.RegularExpressions.Regex.IsMatch(t, @"\(time|\(min|\d{2}\.\d{2}\.\d{4}|Hide item")),
            ("notices", (t, b, w, h) => b.CenterX > w / 2 && System.Text.RegularExpressions.Regex.IsMatch(t, @"Event|Exp x")),
            ("minimap", (t, b, w, h) => b.CenterX > w / 2 && b.Y < h * 0.45 && (WarText.IsZoneTitleLine(t) || System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{3,6}\s*[,.]\s*\d{3,6}$"))),
        };
        var boxes = new SortedDictionary<string, Dictionary<string, (int L, int T, int R, int B, int N)>>(StringComparer.Ordinal);
        foreach (string path in Frames(dir))
        {
            using var frame = Load(path);
            if (frame == null)
                continue;

            int w = frame.Width, h = frame.Height;
            string size = $"{w}x{h}";
            if (!boxes.TryGetValue(size, out var bySize))
                boxes[size] = bySize = [];
            foreach (var (text, b) in ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, new PixelRect(0, 0, w, h), 1.0)))
            {
                foreach (var (name, isKind) in kinds)
                {
                    if (!isKind(text, b, w, h))
                        continue;
                    bySize[name] = bySize.TryGetValue(name, out var u)
                        ? (Math.Min(u.L, b.X), Math.Min(u.T, b.Y), Math.Max(u.R, b.Right), Math.Max(u.B, b.Bottom), u.N + 1)
                        : (b.X, b.Y, b.Right, b.Bottom, 1);
                }
            }
        }

        foreach (var (size, bySize) in boxes)
        {
            var wh = size.Split('x').Select(int.Parse).ToArray();
            foreach (var (name, u) in bySize.OrderBy(kv => kv.Key))
                Console.WriteLine($"{size} {name,-9} x {u.L}-{u.R} y {u.T}-{u.B} ({u.L / (double)wh[0]:0.00}-{u.R / (double)wh[0]:0.00} w, {u.T / (double)wh[1]:0.00}-{u.B / (double)wh[1]:0.00} h) {u.N} lines");
        }

        return 0;
    }

    /// <summary>
    /// --warwalk &lt;dir&gt;: the Auto War walk out of the respawn point on each frame, as the pilot decides it (whole-frame
    /// OCR x1 -> AutomationPilot.WarWalkGoal outside WarWalkBands, our own name tag dropped -> WarWalkClick): the goal tag,
    /// its centre and the Ctrl+click point, or "no players in view"; plus every line that passed the text rule but was
    /// dropped and why (band / own name).
    /// </summary>
    public static async Task<int> WalkAsync(string dir)
    {
        var settings = SettingsStore.Load(persistMigration: false);
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        int total = 0, goals = 0;
        foreach (string path in Frames(dir))
        {
            using var frame = Load(path);
            if (frame == null)
                continue;

            total++;
            int w = frame.Width, h = frame.Height;
            var lines = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, new PixelRect(0, 0, w, h), 1.0));
            var bands = AutomationPilot.WarWalkBands(w, h);
            var goal = AutomationPilot.WarWalkGoal(lines, w, h, bands);
            var dropped = lines
                .Where(l => WarText.IsPlayerTagText(l.Text) && (goal == null || l.Bounds != goal.Value.Bounds))
                .Select(l =>
                {
                    bool inBand = bands.Any(b => l.Bounds.CenterX >= b.X && l.Bounds.CenterX < b.Right && l.Bounds.CenterY >= b.Y && l.Bounds.CenterY < b.Bottom);
                    return inBand ? null : AutomationPilot.WarWalkGoal([l], w, h, bands) == null ? $"'{l.Text}' {Box(l.Bounds)} (own name)" : $"'{l.Text}' {Box(l.Bounds)} (farther)";
                })
                .Where(t => t != null)
                .ToList();
            string what;
            if (goal is { } tag)
            {
                goals++;
                var (x, y) = AutomationPilot.WarWalkClick(tag.Bounds, w, h);
                what = $"goal '{tag.Text}' at {tag.Bounds.CenterX},{tag.Bounds.CenterY} -> Ctrl+click {x},{y}";
            }
            else
            {
                what = "no players in view";
            }

            Console.WriteLine($"{Path.GetFileName(path)} {w}x{h} | {what}{(dropped.Count > 0 ? $" | also in the world: {string.Join(" ; ", dropped)}" : "")}");
        }

        Console.WriteLine($"{total} frames, a walk goal on {goals}");
        return 0;
    }

    /// <summary>
    /// --warbar &lt;dir&gt;: each buff icon (War.BuffIcons, War.WeaponBuffIcons, then War.ItemBuffIcons - ticked off or not)
    /// and Shura on the skill bar - the best score at any scale (0.30 floor, a hit at War.IconMatchScore or more marked HIT),
    /// then what the pilot's ScreenAnalyzer.FindSkillBarIcons call at that score returns.
    /// </summary>
    public static int WarBar(string dir)
    {
        var settings = SettingsStore.Load(persistMigration: false);
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        var war = settings.War;
        var templates = war.BuffIcons.Concat(war.WeaponBuffIcons).Concat(war.ItemBuffIcons).Append(settings.Lod.ShuraTemplate)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        double minScore = settings.War.IconMatchScore;
        var hitFrames = templates.ToDictionary(t => t, _ => 0);
        var bestScores = templates.ToDictionary(t => t, _ => 0.0);
        int total = 0;
        foreach (string path in Frames(dir))
        {
            using var frame = Load(path);
            if (frame == null)
                continue;

            total++;
            var roi = settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
            var parts = new List<string>();
            foreach (string template in templates)
            {
                var (best, _) = analyzer.FindIconAnyScale(frame, template, roi, 0.30);
                if (best != null)
                    bestScores[template] = Math.Max(bestScores[template], best.Score);
                parts.Add(best == null ? $"{template} -"
                    : $"{template} {best.Score:0.000} at {best.Bounds.CenterX},{best.Bounds.CenterY} {best.Bounds.Width}x{best.Bounds.Height}{(best.Score >= minScore ? " HIT" : "")}");
            }

            var watch = Stopwatch.StartNew();
            var pilot = analyzer.FindSkillBarIcons(frame, templates, minScore);
            long ms = watch.ElapsedMilliseconds;
            var found = pilot.Where(p => p.Match != null).ToList();
            foreach (var (template, _) in found)
                hitFrames[template]++;

            string pilotText = found.Count == 0 ? "none"
                : string.Join(", ", found.Select(p => $"{p.Template} {p.Match!.Score:0.000} at {p.Match.Bounds.CenterX},{p.Match.Bounds.CenterY}"));
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | {string.Join(" | ", parts)} | pilot >= {minScore:0.00}: {pilotText} ({ms} ms)");
        }

        Console.WriteLine($"{total} frames; pilot hits per template: {string.Join(", ", hitFrames.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Console.WriteLine($"best score per template: {string.Join(", ", bestScores.Select(kv => $"{kv.Key} {kv.Value:0.000}"))}");
        return 0;
    }

    /// <summary>
    /// --menu &lt;dir&gt;: the MENU button (menubutton, any scale, 0.30 floor) in Regions.MenuButton and the best
    /// whole-frame OCR hit for War.MenuTileText ("Tribe Status"). The hit is marked TILE when its own text
    /// covers most of the tile's (TextMatcher.CoversMost of the hit, which is the whole line when the line
    /// matched best), and "line only" when just its line does: on every in-game frame the best hit is the
    /// word "status" of the "Hide item apply status" check box, whose line passes CoversMost. Last, what the
    /// pilot's close step would do (<see cref="CloseStepAsync"/>): on frames without the MENU or TRIBE STATUS
    /// window it must find no window title and neither click nor warn.
    /// </summary>
    public static async Task<int> MenuAsync(string dir)
    {
        var settings = SettingsStore.Load(persistMigration: false);
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        string tileText = settings.War.MenuTileText;
        int total = 0, buttons = 0, tiles = 0, lineOnly = 0, titles = 0, acts = 0;
        foreach (string path in Frames(dir))
        {
            using var frame = Load(path);
            if (frame == null)
                continue;

            total++;
            var roi = settings.Regions.MenuButton.ToPixels(frame.Width, frame.Height);
            var (button, ms) = analyzer.FindIconAnyScale(frame, "menubutton", roi, 0.30);
            if (button != null)
                buttons++;
            var full = new PixelRect(0, 0, frame.Width, frame.Height);
            var (tile, _, _) = await analyzer.FindTextAsync(frame, full, tileText);
            string verdict = "-";
            if (tile != null)
            {
                bool hit = TextMatcher.CoversMost(tile.Text, tileText);
                bool line = TextMatcher.CoversMost(tile.LineText, tileText);
                verdict = hit ? "TILE" : line ? "line only" : "neither";
                if (hit)
                    tiles++;
                else if (line)
                    lineOnly++;
            }

            var close = await CloseStepAsync(analyzer, settings, frame);
            if (close.Title)
                titles++;
            if (close.Acts)
                acts++;

            string buttonText = button == null ? "not found"
                : $"{button.Score:0.000} at {button.Bounds.CenterX},{button.Bounds.CenterY} {button.Bounds.Width}x{button.Bounds.Height} scale {button.Scale:0.00}";
            string hitText = tile == null ? "-"
                : $"{verdict} '{tile.Text}' {tile.Similarity:0.00} in line '{tile.LineText}' at {Box(tile.Bounds)}";
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | menubutton {buttonText} ({ms:0} ms) | tile {hitText} | close {close.Text}");
        }

        Console.WriteLine($"{total} frames, menubutton seen (>= 0.30) on {buttons}; '{tileText}': TILE {tiles}, line only {lineOnly}; close step: a window title on {titles}, would click or warn on {acts}");
        return 0;
    }

    /// <summary>
    /// --tribe &lt;file or dir&gt;: the War.TribeRowText row of the Tribe Status window and its countdown, as the
    /// pilot reads it (whole-frame OCR x1 -> ScreenAnalyzer.ReadWarCountdownAsync: WarText.FindWarRow, then the
    /// band right of each candidate row line with a gap after each colon, OCR x2 - "pilot"; "window shows" =
    /// WarText.TribeStatusShows, which decides whether the pilot reads again), next to plain whole-frame reads at
    /// x1, x1.5 and x2; then what the pilot's close step would click (<see cref="CloseStepAsync"/>).
    /// A file may be any image OpenCV reads (the user's screenshot is a .webp); a folder means its PNGs.
    /// The first output lines show Windows OCR dropping "h:mm:ss" on synthetic text, and the pilot's read of a
    /// synthetic row (it must say 00:04:25).
    /// </summary>
    public static async Task<int> TribeAsync(string path)
    {
        var settings = SettingsStore.Load(persistMigration: false);
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        string rowText = settings.War.TribeRowText;

        var synthetic = new List<string>();
        foreach (string sample in new[] { "00:04:25", "00: 04: 25" })
        {
            using var image = Synthetic(60, 300, new MCvScalar(25, 25, 25, 255),
                canvas => CvInvoke.PutText(canvas, sample, new Point(10, 40), HersheyFonts.Simplex, 1.0, new MCvScalar(240, 240, 240, 255), 2, LineType.AntiAlias));
            var read = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(image, new PixelRect(0, 0, image.Width, image.Height), 2.0));
            synthetic.Add($"'{sample}' -> '{string.Join(" | ", read.Select(l => l.Text))}'");
        }

        Console.WriteLine($"synthetic OCR x2: {string.Join(", ", synthetic)}");

        // A synthetic Tribe Status row under a notice naming the war and a stray bare row line with no time
        // (both tried before it, neither decides), and another war's row below it.
        var white = new MCvScalar(240, 240, 240, 255);
        // The time column: 8 text heights right of the row name's end, as in the screenshot (~5.6 heights there) and
        // inside the 20-height band the pilot reads; measured, so OpenCV's font metrics cannot push it out of the band.
        int baseline = 0;
        var rowSize = CvInvoke.GetTextSize(rowText, HersheyFonts.Simplex, 0.6, 1, ref baseline);
        int timeX = 10 + rowSize.Width + 8 * rowSize.Height;
        using (var image = Synthetic(240, 700, new MCvScalar(25, 25, 25, 255), canvas =>
               {
                   CvInvoke.PutText(canvas, $"[{rowText}] [10] minute(s) to opening", new Point(10, 40), HersheyFonts.Simplex, 0.6, white, 1, LineType.AntiAlias);
                   CvInvoke.PutText(canvas, rowText, new Point(10, 90), HersheyFonts.Simplex, 0.6, white, 1, LineType.AntiAlias);
                   CvInvoke.PutText(canvas, rowText, new Point(10, 140), HersheyFonts.Simplex, 0.6, white, 1, LineType.AntiAlias);
                   CvInvoke.PutText(canvas, "00:04:25", new Point(timeX, 140), HersheyFonts.Simplex, 0.6, white, 1, LineType.AntiAlias);
                   CvInvoke.PutText(canvas, "Dragonfall Outpost", new Point(10, 190), HersheyFonts.Simplex, 0.6, white, 1, LineType.AntiAlias);
                   CvInvoke.PutText(canvas, "In progress", new Point(timeX, 190), HersheyFonts.Simplex, 0.6, white, 1, LineType.AntiAlias);
               }))
        {
            var trace = new List<string>();
            var lines = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(image, new PixelRect(0, 0, image.Width, image.Height), 1.0));
            var row = await analyzer.ReadWarCountdownAsync(image, lines, rowText, trace);
            Console.WriteLine($"synthetic row (expect 00:04:25): pilot {Describe(row)} | {string.Join(" ; ", trace)}");
        }

        var files = Directory.Exists(path) ? Frames(path).ToList() : [path];
        foreach (string file in files)
        {
            using var frame = Load(file);
            if (frame == null)
            {
                Console.WriteLine($"{Path.GetFileName(file)} | could not read");
                continue;
            }

            var full = new PixelRect(0, 0, frame.Width, frame.Height);
            var pilotWatch = Stopwatch.StartNew();
            var pilotTrace = new List<string>();
            var pilotLines = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, full, 1.0));
            var pilot = await analyzer.ReadWarCountdownAsync(frame, pilotLines, rowText, pilotTrace);
            long pilotMs = pilotWatch.ElapsedMilliseconds;
            bool shows = WarText.TribeStatusShows(pilotLines, rowText);

            var parts = new List<string>();
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                var watch = Stopwatch.StartNew();
                var lines = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, full, scale));
                var row = WarText.FindWarRow(lines, rowText);
                parts.Add($"x{scale:0.0}: {Describe(row)} ({watch.ElapsedMilliseconds} ms)");
            }

            var close = await CloseStepAsync(analyzer, settings, frame);
            Console.WriteLine($"{Path.GetFileName(file)} {frame.Width}x{frame.Height} | '{rowText}' pilot: {Describe(pilot)} ({pilotMs} ms; window shows: {shows}; {string.Join(" ; ", pilotTrace)}) | whole frame {string.Join(" | ", parts)} | close: {close.Text}");
        }

        return 0;
    }

    /// <summary>
    /// The pilot's close step (AutomationPilot.TickWarCloseAsync) on one frame: one whole-frame read for
    /// War.MenuTileText at the OCR scale. A TRIBE STATUS or MENU title in capitals (ScreenAnalyzer.FindWarWindowClose)
    /// -> its X, or no X seen (TRIBE STATUS: left open; MENU: the MENU button); no title but the menu's tile
    /// (WarText.IsMenuTileHit) -> the MENU button; a hit with the tile's text that reads like the TRIBE STATUS title
    /// -> left open; else nothing to close. <c>Title</c>: a window title was found; <c>Acts</c>: the step would
    /// click or warn.
    /// </summary>
    private static async Task<(string Text, bool Title, bool Acts)> CloseStepAsync(ScreenAnalyzer analyzer, AppSettings settings, Mat frame)
    {
        string tileText = settings.War.MenuTileText;
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var (hit, _, words) = await analyzer.FindTextAsync(frame, full, tileText);
        var lines = ScreenAnalyzer.ToLines(words);
        if (analyzer.FindWarWindowClose(frame, lines) is { } w)
        {
            string title = $"title '{w.Title}' {Box(w.TitleBounds)}";
            if (w.Close is { } x)
                return ($"{title}, X at {x.CenterX},{x.CenterY} ({Box(x)})", true, true);
            return w.Window == WarText.MenuTitle
                ? ($"{title}, no X seen -> the menu button", true, true)
                : ($"{title}, no X seen -> left open (warning)", true, true);
        }

        if (hit == null || !TextMatcher.CoversMost(hit.Text, tileText))
            return ("nothing open", false, false);
        return WarText.IsMenuTileHit(hit.Text, hit.Bounds, lines, tileText)
            ? ($"menu up without its title ('{hit.Text}' at {Box(hit.Bounds)}) -> the menu button", false, true)
            : ($"'{hit.Text}' at {Box(hit.Bounds)} reads like the {WarText.TribeStatusTitle} title -> left open (warning)", false, true);
    }

    private static string Describe(WarCountdown? row) => row switch
    {
        { InProgress: true } => "In progress",
        { Remaining: { } left } => $"{left:hh\\:mm\\:ss} ({left.TotalSeconds:0} s)",
        _ => "not found",
    };
}
