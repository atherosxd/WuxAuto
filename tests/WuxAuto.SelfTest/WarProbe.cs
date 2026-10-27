using System.Diagnostics;
using System.IO;
using OpenCvSharp;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>
/// Auto War screen readers over a folder of saved frames, one line per PNG:
/// --hp (player HP), --target (target name + chest), --warbar (buff icons + Shura on the skill bar),
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
        using var source = Cv2.ImRead(path, ImreadModes.Color);
        if (source.Empty())
            return null;
        var frame = new Mat();
        Cv2.CvtColor(source, frame, ColorConversionCodes.BGR2BGRA);
        return frame;
    }

    private static string Box(PixelRect r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

    /// <summary>
    /// --hp &lt;dir&gt;: the player's HP as ScreenAnalyzer.ReadPlayerHpAsync reads it, plus the lines of its read
    /// (the white text of Regions.PlayerHp at x2) with a '/' - every line when nothing was read. The summary
    /// counts frames whose region holds a "0 / N" line at all (the "0/3000" bar must stay outside it).
    /// </summary>
    public static async Task<int> HpAsync(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        int read = 0, full = 0, zeroLines = 0, total = 0;
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
            var shown = lines
                .Where(l => hp == null || l.Text.Contains('/'))
                .Select(l => $"'{l.Text}' {Box(l.Bounds)}");
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | hp {(hp == null ? "not found" : $"{hp.Value.Cur} / {hp.Value.Max}")} ({ms} ms) | {(hp == null ? "lines" : "slash lines")}: {string.Join(" ; ", shown)}");
        }

        Console.WriteLine($"{read}/{total} frames with an HP read ({full} with cur = max); {zeroLines} with a '0 / N' line in the region");
        return 0;
    }

    /// <summary>
    /// --target &lt;dir&gt;: the target name and the target bar's chest as ScreenAnalyzer.ReadTargetAsync reads them,
    /// and which read named it (plain x1.5, or the white text at x2). A chest without a name also prints the
    /// lines of the white-text read.
    /// </summary>
    public static async Task<int> TargetAsync(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        int named = 0, namedPlain = 0, chests = 0, total = 0;
        foreach (string path in Frames(dir))
        {
            using var frame = Load(path);
            if (frame == null)
                continue;

            total++;
            var watch = Stopwatch.StartNew();
            var (name, chest) = await analyzer.ReadTargetAsync(frame);
            long ms = watch.ElapsedMilliseconds;
            if (chest != null)
                chests++;

            var roi = settings.Regions.TargetBox.ToPixels(frame.Width, frame.Height);
            string via = "";
            if (name != null)
            {
                named++;
                bool plain = WarText.FindTargetName(ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, roi, 1.5))) == name;
                if (plain)
                    namedPlain++;
                via = plain ? " (x1.5)" : " (white x2)";
            }

            string seen = "";
            if (name == null && chest != null)
            {
                var lines = await analyzer.OcrWhiteTextAsync(frame, roi, 2.0);
                seen = $" | white x2 lines: {string.Join(" ; ", lines.Select(l => $"'{l.Text}' {Box(l.Bounds)}"))}";
            }

            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | name {(name == null ? "not found" : $"'{name}'{via}")} | chest {(chest == null ? "-" : $"{chest.Score:0.000} at {Box(chest.Bounds)}")} ({ms} ms){seen}");
        }

        Console.WriteLine($"{named}/{total} frames with a target name ({namedPlain} by the x1.5 read), {chests} with the chest");
        return 0;
    }

    /// <summary>
    /// --warbar &lt;dir&gt;: each buff icon (War.BuffIcons) and Shura on the skill bar - the best score at any
    /// scale (0.30 floor, a hit at War.IconMatchScore or more marked HIT), then what the pilot's
    /// ScreenAnalyzer.FindSkillBarIcons call at that score returns.
    /// </summary>
    public static int WarBar(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        var templates = settings.War.BuffIcons.Append(settings.Lod.ShuraTemplate).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
    /// word "status" of the "Hide item apply status" check box, whose line passes CoversMost.
    /// </summary>
    public static async Task<int> MenuAsync(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        string tileText = settings.War.MenuTileText;
        int total = 0, buttons = 0, tiles = 0, lineOnly = 0;
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

            string buttonText = button == null ? "not found"
                : $"{button.Score:0.000} at {button.Bounds.CenterX},{button.Bounds.CenterY} {button.Bounds.Width}x{button.Bounds.Height} scale {button.Scale:0.00}";
            string hitText = tile == null ? "-"
                : $"{verdict} '{tile.Text}' {tile.Similarity:0.00} in line '{tile.LineText}' at {Box(tile.Bounds)}";
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | menubutton {buttonText} ({ms:0} ms) | tile {hitText}");
        }

        Console.WriteLine($"{total} frames, menubutton seen (>= 0.30) on {buttons}; '{tileText}': TILE {tiles}, line only {lineOnly}");
        return 0;
    }

    /// <summary>
    /// --tribe &lt;file or dir&gt;: the War.TribeRowText row of the Tribe Status window and its countdown,
    /// whole-frame OCR -> ScreenAnalyzer.ToLines -> WarText.FindWarRow at x1, x1.5 and x2. A file may be any
    /// image OpenCV reads (the user's screenshot is a .webp); a folder means its PNGs.
    /// Windows OCR drops every "h:mm:ss" token (a synthetic "00:04:25" reads as nothing, "00: 04: 25" reads),
    /// so a second read copies the band right of the row with a gap after each colon (<see cref="GapAfterColons"/>)
    /// and OCRs that at x2 ("colon gap"); the first output line shows the quirk on synthetic text.
    /// </summary>
    public static async Task<int> TribeAsync(string path)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        string rowText = settings.War.TribeRowText;

        var synthetic = new List<string>();
        foreach (string sample in new[] { "00:04:25", "00: 04: 25" })
        {
            using var image = new Mat(60, 300, MatType.CV_8UC4, new Scalar(25, 25, 25, 255));
            Cv2.PutText(image, sample, new Point(10, 40), HersheyFonts.HersheySimplex, 1.0, new Scalar(240, 240, 240, 255), 2, LineTypes.AntiAlias);
            var read = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(image, new PixelRect(0, 0, image.Width, image.Height), 2.0));
            synthetic.Add($"'{sample}' -> '{string.Join(" | ", read.Select(l => l.Text))}'");
        }

        Console.WriteLine($"synthetic OCR x2: {string.Join(", ", synthetic)}");

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
            var parts = new List<string>();
            IReadOnlyList<(string Text, PixelRect Bounds)> firstRead = [];
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                var watch = Stopwatch.StartNew();
                var lines = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, full, scale));
                var row = WarText.FindWarRow(lines, rowText);
                parts.Add($"x{scale:0.0}: {Describe(row)} ({watch.ElapsedMilliseconds} ms)");
                if (scale == 1.0)
                    firstRead = lines;
            }

            // The row line of the x1 read (as FindWarRow picks it), then the band right of it with gapped colons.
            string wanted = TextMatcher.Normalize(rowText);
            var rowLine = firstRead.FirstOrDefault(l => TextMatcher.Normalize(l.Text).Contains(wanted, StringComparison.Ordinal)
                                                        || TextMatcher.Similarity(l.Text, rowText) >= 0.80);
            string gapped = "no row";
            string rowRead = "";
            if (rowLine.Text != null)
            {
                var sameRow = firstRead.Where(l => l != rowLine && Math.Abs(l.Bounds.CenterY - rowLine.Bounds.CenterY) <= rowLine.Bounds.Height);
                rowRead = $" | x1.0 row '{rowLine.Text}' {Box(rowLine.Bounds)}, same row: {string.Join(" ; ", sameRow.Select(l => $"'{l.Text}' {Box(l.Bounds)}"))}";

                var watch = Stopwatch.StartNew();
                int h = rowLine.Bounds.Height;
                var band = PixelRect.FromBounds(rowLine.Bounds.Right, rowLine.Bounds.Y - h / 2, frame.Width, rowLine.Bounds.Bottom + h / 2)
                    .Clamp(frame.Width, frame.Height);
                using var spaced = GapAfterColons(frame, band, h, out int colons);
                var bandLines = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(spaced, new PixelRect(0, 0, spaced.Width, spaced.Height), 2.0))
                    .Select(l => (l.Text, new PixelRect(band.X + l.Bounds.X, band.Y + l.Bounds.Y, l.Bounds.Width, l.Bounds.Height)));
                var row = WarText.FindWarRow([rowLine, .. bandLines], rowText);
                gapped = $"{Describe(row)}, {colons} colon(s) ({watch.ElapsedMilliseconds} ms)";
            }

            Console.WriteLine($"{Path.GetFileName(file)} {frame.Width}x{frame.Height} | '{rowText}' {string.Join(" | ", parts)}{rowRead} | colon gap x2: {gapped}");
        }

        return 0;
    }

    private static string Describe(WarCountdown? row) => row switch
    {
        { InProgress: true } => "In progress",
        { Remaining: { } left } => $"{left:hh\\:mm\\:ss} ({left.TotalSeconds:0} s)",
        _ => "not found",
    };

    /// <summary>
    /// A copy of <paramref name="band"/> with a blank gap (half the text height) after each colon: two small
    /// bright blobs (Otsu) stacked between two glyphs of the line, <paramref name="textHeight"/> = the row's OCR line height.
    /// </summary>
    private static Mat GapAfterColons(Mat frame, PixelRect band, int textHeight, out int colons)
    {
        using var crop = new Mat(frame, new Rect(band.X, band.Y, band.Width, band.Height));
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        using var bright = new Mat();
        Cv2.Threshold(gray, bright, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(bright, labels, stats, centroids);

        var blobs = Enumerable.Range(1, Math.Max(0, count - 1))
            .Select(i => (X: stats.At<int>(i, 0), Y: stats.At<int>(i, 1), Right: stats.At<int>(i, 0) + stats.At<int>(i, 2),
                Bottom: stats.At<int>(i, 1) + stats.At<int>(i, 3), Height: stats.At<int>(i, 3)))
            .ToList();
        // Glyphs are at least half the text height (the window border is taller and is neither); dots a quarter or less.
        var dots = blobs.Where(b => b.Height <= textHeight / 4.0 && b.Right - b.X <= textHeight / 4.0).ToList();
        var glyphs = blobs.Where(b => b.Height >= textHeight / 2.0 && b.Height <= textHeight * 1.5).ToList();
        // A colon: two dots, one above the other (overlapping columns), between two glyphs of one text line.
        var gaps = new List<int>();
        foreach (var top in dots)
        {
            foreach (var bottom in dots)
            {
                bool stacked = bottom.Y >= top.Bottom && bottom.X < top.Right && bottom.Right > top.X;
                bool inLine = glyphs.Any(g => g.Right <= top.X && top.X - g.Right <= textHeight && g.Y <= top.Y && g.Bottom >= bottom.Bottom)
                              && glyphs.Any(g => g.X >= top.Right && g.X - top.Right <= textHeight && g.Y <= top.Y && g.Bottom >= bottom.Bottom);
                int cut = Math.Max(top.Right, bottom.Right); // first column right of the colon
                if (stacked && inLine && !gaps.Contains(cut))
                    gaps.Add(cut);
            }
        }

        gaps.Sort();

        colons = gaps.Count;
        int gap = Math.Max(4, textHeight / 2);
        using var dark = new Mat();
        Cv2.BitwiseNot(bright, dark);
        var background = Cv2.Mean(crop, dark);
        var result = new Mat(crop.Rows, crop.Cols + gap * gaps.Count, crop.Type(), background);
        int from = 0, to = 0;
        foreach (int cut in gaps.Append(crop.Cols))
        {
            int end = Math.Min(crop.Cols, cut);
            if (end > from)
            {
                using var source = new Mat(crop, new Rect(from, 0, end - from, crop.Rows));
                using var target = new Mat(result, new Rect(to, 0, end - from, crop.Rows));
                source.CopyTo(target);
            }

            to += end - from + gap;
            from = end;
        }

        return result;
    }
}

