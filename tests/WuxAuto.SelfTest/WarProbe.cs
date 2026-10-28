using System.Diagnostics;
using System.IO;
using OpenCvSharp;
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
        using var source = Cv2.ImRead(path, ImreadModes.Color);
        if (source.Empty())
            return null;
        var frame = new Mat();
        Cv2.CvtColor(source, frame, ColorConversionCodes.BGR2BGRA);
        return frame;
    }

    private static string Box(PixelRect r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

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
    /// --target &lt;dir&gt;: the target name and the target bar's chest as ScreenAnalyzer.ReadTargetAsync reads them,
    /// and which read named it (plain x1.5, or the white text at x2). A chest without a name also prints the
    /// lines of the white-text read.
    /// </summary>
    public static async Task<int> TargetAsync(string dir)
    {
        var settings = SettingsStore.Load(persistMigration: false);
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
            using var image = new Mat(60, 300, MatType.CV_8UC4, new Scalar(25, 25, 25, 255));
            Cv2.PutText(image, sample, new Point(10, 40), HersheyFonts.HersheySimplex, 1.0, new Scalar(240, 240, 240, 255), 2, LineTypes.AntiAlias);
            var read = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(image, new PixelRect(0, 0, image.Width, image.Height), 2.0));
            synthetic.Add($"'{sample}' -> '{string.Join(" | ", read.Select(l => l.Text))}'");
        }

        Console.WriteLine($"synthetic OCR x2: {string.Join(", ", synthetic)}");

        // A synthetic Tribe Status row under a notice naming the war and a stray bare row line with no time
        // (both tried before it, neither decides), and another war's row below it.
        using (var image = new Mat(240, 700, MatType.CV_8UC4, new Scalar(25, 25, 25, 255)))
        {
            var white = new Scalar(240, 240, 240, 255);
            Cv2.PutText(image, $"[{rowText}] [10] minute(s) to opening", new Point(10, 40), HersheyFonts.HersheySimplex, 0.6, white, 1, LineTypes.AntiAlias);
            Cv2.PutText(image, rowText, new Point(10, 90), HersheyFonts.HersheySimplex, 0.6, white, 1, LineTypes.AntiAlias);
            Cv2.PutText(image, rowText, new Point(10, 140), HersheyFonts.HersheySimplex, 0.6, white, 1, LineTypes.AntiAlias);
            Cv2.PutText(image, "00:04:25", new Point(520, 140), HersheyFonts.HersheySimplex, 0.6, white, 1, LineTypes.AntiAlias);
            Cv2.PutText(image, "Dragonfall Outpost", new Point(10, 190), HersheyFonts.HersheySimplex, 0.6, white, 1, LineTypes.AntiAlias);
            Cv2.PutText(image, "In progress", new Point(520, 190), HersheyFonts.HersheySimplex, 0.6, white, 1, LineTypes.AntiAlias);
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
