using System.IO;
using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>
/// Runs the full "Test scan" pipeline over a saved frame (a PNG grabbed from the game) and writes an
/// annotated copy next to it. Run with: ... --frame &lt;png&gt; [--settings &lt;json&gt;]
/// </summary>
internal static class FrameProbe
{
    /// <summary>
    /// Fast check over a folder of saved frames: the minimap title lines (zone name) and any
    /// whole-screen words the dialog/zone guards react to. Run with: --titles &lt;dir&gt;
    /// </summary>
    public static async Task<int> TitlesAsync(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings);
        foreach (string path in Directory.GetFiles(dir, "*.png").Where(p => !p.EndsWith(".scan.png") && !p.EndsWith(".crop.png")).Order())
        {
            using var source = Cv2.ImRead(path, ImreadModes.Unchanged);
            if (source.Empty())
                continue;

            using var frame = new Mat();
            if (source.Channels() == 4)
                source.CopyTo(frame);
            else
                Cv2.CvtColor(source, frame, ColorConversionCodes.BGR2BGRA);

            var band = settings.Regions.Minimap.ToPixels(frame.Width, frame.Height);
            // Same rule as AutomationPilot.ReadMinimapTitleAsync.
            var lines = (await analyzer.Ocr.RecognizeAsync(frame, band, 2.0))
                .Select(w => w.LineText.Trim()).Distinct().ToList();
            string labyrinth = TextMatcher.Normalize(settings.Sequence.DestinationText);
            string title = lines.FirstOrDefault(l => TextMatcher.Normalize(l).Contains(labyrinth, StringComparison.Ordinal))
                           ?? lines.FirstOrDefault(l => l.StartsWith('[') && !l.Any(char.IsDigit) && TextMatcher.Normalize(l).Length >= 4)
                           ?? "-";
            var screen = (await analyzer.Ocr.RecognizeAsync(frame, new PixelRect(0, 0, frame.Width, frame.Height), 1.0))
                .Select(w => w.LineText).Distinct()
                .Where(l => l.Contains("bohai", StringComparison.OrdinalIgnoreCase) || l.Contains("cancel", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | title: {title} | dialog: {string.Join(" / ", screen)}");
        }

        return 0;
    }

    /// <summary>
    /// Fast check over a folder of saved frames: the LOD boss's pink ring and cyan belt (the spots the
    /// pilot Ctrl+clicks to walk up to it). Run with: --lodparts &lt;dir&gt;
    /// </summary>
    public static int LodParts(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings);
        foreach (string path in Directory.GetFiles(dir, "*.png").Where(p => !p.EndsWith(".scan.png") && !p.EndsWith(".crop.png")).Order())
        {
            using var frame = Cv2.ImRead(path, ImreadModes.Color);
            if (frame.Empty())
                continue;

            var ring = analyzer.FindBossRing(frame);
            var belt = analyzer.FindBossBelt(frame);
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | ring {(ring == null ? "-" : ring.Value.ToString())} | belt {(belt == null ? "-" : belt.Value.ToString())}");
        }

        return 0;
    }

    /// <summary>
    /// Fast check over a folder of saved frames: the Run skill icon on the skill bar and the gold
    /// ring round it (on / off), as the pilot judges it in town. A crop round every hit lands in
    /// %TEMP%\wuxauto-runskill\&lt;frame&gt;.png to check by eye. Run with: --runskill &lt;dir&gt;
    /// </summary>
    public static int RunSkill(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings);
        string template = settings.Sequence.RunSkillTemplate;
        string outDir = Path.Combine(Path.GetTempPath(), "wuxauto-runskill");
        Directory.CreateDirectory(outDir);
        foreach (string path in Directory.GetFiles(dir, "*.png").Where(p => !p.EndsWith(".scan.png") && !p.EndsWith(".crop.png")).Order())
        {
            using var frame = Cv2.ImRead(path, ImreadModes.Color);
            if (frame.Empty())
                continue;

            var roi = settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
            var (hit, ms) = analyzer.FindIconAnyScale(frame, template, roi, 0.30);
            string verdict = "-";
            string ringText = "";
            if (hit != null)
            {
                double ring = analyzer.RingFraction(frame, hit.Bounds);
                ringText = $" ring {ring:0.00}";
                verdict = hit.Score < settings.Sequence.RunSkillMatchScore ? "below score"
                    : ring >= settings.Sequence.RunSkillRingMin ? "ON" : "off -> click";
                var box = hit.Bounds.Inflate(hit.Bounds.Width / 2).Clamp(frame.Width, frame.Height);
                using var crop = new Mat(frame, new Rect(box.X, box.Y, box.Width, box.Height));
                Cv2.ImWrite(Path.Combine(outDir, Path.GetFileName(path)), crop);
            }

            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | {template}: {(hit == null ? "not found" : $"{hit.Score:0.000} at {hit.Bounds.CenterX},{hit.Bounds.CenterY} {hit.Bounds.Width}x{hit.Bounds.Height} scale {hit.Scale:0.00}")}{ringText} -> {verdict} ({ms:0} ms)");
        }

        return 0;
    }

    /// <summary>
    /// Fast check over a folder of saved frames: the LOD Shura icon on the skill bar (the stop-attack
    /// click), as the pilot looks for it. Run with: --shura &lt;dir&gt;
    /// </summary>
    public static int Shura(string dir)
    {
        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings);
        string template = settings.Lod.ShuraTemplate;
        foreach (string path in Directory.GetFiles(dir, "*.png").Where(p => !p.EndsWith(".scan.png") && !p.EndsWith(".crop.png")).Order())
        {
            using var frame = Cv2.ImRead(path, ImreadModes.Color);
            if (frame.Empty())
                continue;

            var roi = settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
            var (hit, ms) = analyzer.FindIconAnyScale(frame, template, roi, 0.30);
            string verdict = hit == null ? "-" : hit.Score >= settings.Lod.ShuraMatchScore ? "click" : "below score";
            Console.WriteLine($"{Path.GetFileName(path)} {frame.Width}x{frame.Height} | {template}: {(hit == null ? "not found" : $"{hit.Score:0.000} at {hit.Bounds.CenterX},{hit.Bounds.CenterY} {hit.Bounds.Width}x{hit.Bounds.Height} scale {hit.Scale:0.00}")} -> {verdict} ({ms:0} ms)");
        }

        return 0;
    }

    public static async Task<int> RunAsync(string path, string[] args)
    {
        var source = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (source.Empty())
        {
            Console.WriteLine($"could not read {path}");
            return 1;
        }

        using var frame = source.Channels() == 4
            ? source
            : new Mat();

        if (source.Channels() != 4)
            Cv2.CvtColor(source, frame, ColorConversionCodes.BGR2BGRA);

        var settings = SettingsStore.Load();
        using var analyzer = new ScreenAnalyzer(settings);

        Console.WriteLine($"frame: {path} {frame.Width}x{frame.Height}");
        Console.WriteLine($"thresholds: template {settings.Detection.TemplateThreshold:0.00}, compass {settings.Detection.CompassThreshold:0.00}, mode {settings.Detection.CompassMode}");

        var report = await analyzer.TestScanAsync(frame);
        foreach (string detail in report.Details)
            Console.WriteLine($"  {detail}");

        var coordTrace = new List<string>();
        var coords = await analyzer.ReadCoordinatesAsync(frame, coordTrace);
        foreach (string line in coordTrace)
            Console.WriteLine($"  coord read: {line}");
        Console.WriteLine(coords == null
            ? "  coordinates: not read"
            : $"  coordinates: {coords} (raw '{coords.Raw}')");

        // The searchlight, as FaceNorth scores it: in the box under the minimap line, any scale.
        var compassRoi = analyzer.CompassRegion(frame.Width, frame.Height);
        foreach (string beam in new[] { settings.Dungeon.FlashlightTemplate, settings.Dungeon.FlashlightTemplate + "west" })
        {
            var (beamHit, _) = analyzer.FindIconAnyScale(frame, beam, compassRoi, 0.30);
            Console.WriteLine($"  searchlight {beam} in {compassRoi}: {(beamHit == null ? "-" : $"{beamHit.Score:0.000} @ {beamHit.Bounds}")}");
        }

        // The item status list (bottom right): the dungeon keys.
        var itemRoi = new NormRect(0.62, 0.50, 0.38, 0.50).ToPixels(frame.Width, frame.Height);
        var itemLines = new List<string>();
        foreach (double up in new[] { 1.5, 2.0, 1.0 })
            itemLines.AddRange((await analyzer.Ocr.RecognizeAsync(frame, itemRoi, up)).Select(w => w.LineText));
        itemLines = itemLines.Distinct().ToList();
        static string Plain(string t) => new string(t.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        bool temple = itemLines.Any(l => Plain(l).Contains("fiend") && Plain(l).Contains("key"));
        bool den = itemLines.Any(l => Plain(l).Contains("rebirth"));
        Console.WriteLine($"  item list: {itemLines.Count} line(s); temple key {temple}, den of rebirth {den}");
        foreach (string l in itemLines.Where(l => Plain(l).Contains("temple") || Plain(l).Contains("fien") || Plain(l).Contains("rebi")))
            Console.WriteLine($"    key-ish line: '{l}'");

        // The teleport list's page counter, in the region the destination search reads.
        var listRoi = settings.Regions.Ocr.ToPixels(frame.Width, frame.Height);
        var counter = (await analyzer.Ocr.RecognizeAsync(frame, listRoi, settings.Detection.OcrScale)).Select(w => w.LineText).Distinct()
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*\d{1,2}\s*/\s*\d{1,2}\s*$"));
        Console.WriteLine($"  teleport page counter in {listRoi}: {string.Join(" | ", counter)}");
        var digitLines = (await analyzer.Ocr.RecognizeAsync(frame, listRoi, settings.Detection.OcrScale)).Select(w => (w.LineText, w.LineBounds)).Distinct()
            .Where(l => l.LineText.Any(char.IsDigit)).Take(6);
        foreach (var l in digitLines)
            Console.WriteLine($"  list line with digits: '{l.LineText}' {l.LineBounds}");
        Console.WriteLine($"  teleport list region: {settings.Regions.TeleportList.ToPixels(frame.Width, frame.Height)}");

        var topBar = new NormRect(0.50, 0, 0.45, 0.06).ToPixels(frame.Width, frame.Height);
        var topLines = (await analyzer.Ocr.RecognizeAsync(frame, topBar, 2.0)).Select(w => w.LineText).Distinct();
        Console.WriteLine($"  top bar: {string.Join(" | ", topLines)}");

        foreach (string word in new[] { "SETTINGS", "VISUAL", "SAVE" })
        {
            var (wordHit, _, _) = await analyzer.FindTextAsync(frame, new PixelRect(0, 0, frame.Width, frame.Height), word);
            if (wordHit != null)
                Console.WriteLine($"  settings word '{word}': '{wordHit.Text}' {wordHit.Similarity:0.00} {wordHit.Bounds} covers {TextMatcher.CoversMost(wordHit.Text, word)}");
        }

        var fontRow = await analyzer.ReadSettingRowAsync(frame, "Font size");
        if (fontRow != null)
            Console.WriteLine($"  setting 'Font size': label {fontRow.Label}, value {fontRow.Value} = {fontRow.Percent}, left {fontRow.Left}, right {fontRow.Right}");

        var tabRow = await analyzer.ReadSettingRowAsync(frame, "TAB target", numeric: false);
        if (tabRow != null)
            Console.WriteLine($"  setting 'TAB target': label {tabRow.Label}, value {tabRow.Value} = '{tabRow.ValueText}' " +
                              $"(default {WuxAuto.Automation.AutomationPilot.IsDefaultTabTarget(tabRow.ValueText ?? string.Empty)}), left {tabRow.Left}, right {tabRow.Right}");

        // The stage 5 gate glow, in the bands TickAimGate searches.
        int glowArea = Math.Max(150, frame.Width * frame.Height / 4000);
        foreach (var band in new[] { new NormRect(0.25, 0.07, 0.31, 0.15), new NormRect(0.25, 0.22, 0.50, 0.20) })
        {
            var glowRoi = band.ToPixels(frame.Width, frame.Height);
            var glow = analyzer.FindRedOrangeGlow(frame, glowRoi, glowArea);
            Console.WriteLine($"  gate glow in {glowRoi}: {(glow == null ? "-" : glow.Value.ToString())}");
            var selRing = analyzer.FindSelectionRing(frame);
            Console.WriteLine($"  selection ring: {(selRing == null ? "-" : selRing.Value.ToString())}");

            int cbArg = Array.IndexOf(args, "--checkbox");
            if (cbArg >= 0 && cbArg + 4 < args.Length)
            {
                var label = new PixelRect(int.Parse(args[cbArg + 1]), int.Parse(args[cbArg + 2]), int.Parse(args[cbArg + 3]), int.Parse(args[cbArg + 4]));
                var cb = analyzer.FindCheckboxLeftOf(frame, label);
                Console.WriteLine($"  checkbox for label {label}: box {cb}, ticked {analyzer.IsCheckboxTicked(frame, cb)}");
            }

            // Auto war: Regular War checkbox state and the AUTO BUFF dialog's X.
            var warWords = await analyzer.Ocr.RecognizeAsync(frame, new PixelRect(0, 0, frame.Width, frame.Height), 2.0);
            foreach (var g in warWords.GroupBy(w => w.LineBounds))
            {
                string t = TextMatcher.Normalize(g.First().LineText);
                if (t.StartsWith("regular") || t.Length is >= 2 and <= 3 || t == "smartattack")
                {
                    var cb = analyzer.FindCheckboxLeftOf(frame, g.Key);
                    Console.WriteLine($"  checkbox '{t}' at {g.Key}: box {cb}, ticked {analyzer.IsCheckboxTicked(frame, cb)}");
                }
                if (TextMatcher.Levenshtein(t, "autobuff") <= 1)
                    Console.WriteLine($"  AUTO BUFF title {g.Key}: X {analyzer.FindCloseButtonRightOf(frame, g.Key)}");
            }
        }

        var bossRing = analyzer.FindBossRing(frame);
        var bossBelt = analyzer.FindBossBelt(frame);
        Console.WriteLine($"  LOD boss ring: {(bossRing == null ? "-" : bossRing.Value.ToString())}, belt: {(bossBelt == null ? "-" : bossBelt.Value.ToString())}");

        // Every stage name, as CheckZone looks for it when resuming inside the Labyrinth.
        var allBossRoi = settings.Regions.BossName.ToPixels(frame.Width, frame.Height);
        foreach (string stageName in settings.Dungeon.Stages.Append(settings.Dungeon.FinalBoss))
        {
            var (stageHit, _, _) = await analyzer.FindTextAsync(frame, allBossRoi, stageName);
            bool wholeName = stageHit != null && (stageHit.Similarity >= 0.90 || TextMatcher.CoversMost(stageHit.Text, stageName));
            Console.WriteLine($"  stage name '{stageName}': {(stageHit == null ? "-" : $"'{stageHit.Text}' {stageHit.Similarity:0.00}{(wholeName ? " FULL" : " partial")} at {stageHit.LineBounds} (y {stageHit.LineBounds.CenterY * 100.0 / frame.Height:0}%, x {stageHit.LineBounds.CenterX * 100.0 / frame.Width:0}%)")}");
        }

        if (settings.Dungeon.Stages.Count > 0)
        {
            string stage = settings.Dungeon.Stages[0];
            var bossRoi = settings.Regions.BossName.ToPixels(frame.Width, frame.Height);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (bossHit, _, _) = await analyzer.FindTextAsync(frame, bossRoi, stage);
            watch.Stop();
            Console.WriteLine($"  boss '{stage}': {(bossHit == null ? "not found" : $"'{bossHit.Text}' {bossHit.Similarity:0.00}")} in {watch.ElapsedMilliseconds}ms");
        }

        // Exactly what the pilot's detector does, including the learned scale.
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        Console.WriteLine("pilot path (learned scale):");
        foreach (string name in analyzer.Library.Names)
        {
            var (match, ms) = analyzer.FindIconTimed(frame, name, full);
            Console.WriteLine($"  {name,-16} {(match == null ? "not found" : $"{match.Score:0.000} @ ({match.Bounds.X},{match.Bounds.Y} {match.Bounds.Width}x{match.Bounds.Height}) scale x{match.Scale:0.000}")}  {ms:0.0}ms");
        }

        // The map list scroll arrows, exactly as the pilot looks for them.
        var scrollRoi = settings.Regions.MapScroll.ToPixels(frame.Width, frame.Height);
        var (upArrow, downArrow) = await analyzer.FindMapScrollArrowsAsync(frame, scrollRoi);
        var (spawnsHit, _, _) = await analyzer.FindTextAsync(frame, scrollRoi, "Spawns");
        Console.WriteLine($"  scroll arrows: ROI {scrollRoi.X},{scrollRoi.Y} {scrollRoi.Width}x{scrollRoi.Height}; " +
                          $"spawns {(spawnsHit == null ? "not found" : $"@ {spawnsHit.Bounds.X},{spawnsHit.Bounds.Y} {spawnsHit.Bounds.Width}x{spawnsHit.Bounds.Height}")}; " +
                          $"up {(upArrow == null ? "-" : $"{upArrow.Value.CenterX},{upArrow.Value.CenterY}")}, " +
                          $"down {(downArrow == null ? "-" : $"{downArrow.Value.CenterX},{downArrow.Value.CenterY}")}");

        using var annotated = frame.Clone();
        OverlayRenderer.Draw(annotated, report.Overlays);

        string output = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
            Path.GetFileNameWithoutExtension(path) + ".scan.png");
        Cv2.ImWrite(output, annotated);
        Console.WriteLine($"annotated: {output}");

        int cropIndex = Array.IndexOf(args, "--crop");
        if (cropIndex >= 0 && cropIndex + 4 < args.Length &&
            int.TryParse(args[cropIndex + 1], out int cx) && int.TryParse(args[cropIndex + 2], out int cy) &&
            int.TryParse(args[cropIndex + 3], out int cw) && int.TryParse(args[cropIndex + 4], out int ch))
        {
            double zoom = cropIndex + 5 < args.Length && double.TryParse(args[cropIndex + 5], out double z) ? z : 3;
            var rect = new Rect(
                Math.Clamp(cx, 0, frame.Width - 1),
                Math.Clamp(cy, 0, frame.Height - 1),
                Math.Clamp(cw, 1, frame.Width - cx),
                Math.Clamp(ch, 1, frame.Height - cy));

            using var crop = new Mat(annotated, rect);
            using var zoomed = new Mat();
            Cv2.Resize(crop, zoomed, new Size(rect.Width * (int)zoom, rect.Height * (int)zoom), 0, 0, InterpolationFlags.Nearest);

            string cropPath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                $"{Path.GetFileNameWithoutExtension(path)}.crop.png");
            Cv2.ImWrite(cropPath, zoomed);
            Console.WriteLine($"crop: {cropPath} ({rect.Width}x{rect.Height} at {rect.X},{rect.Y} x{zoom:0})");
        }

        return 0;
    }
}
