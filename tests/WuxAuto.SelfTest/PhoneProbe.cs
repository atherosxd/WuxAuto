using System.Diagnostics;
using System.Drawing;
using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Core;
using WuxAuto.Phone;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>
/// The phone vision over a folder of phone frames (MuMu screencaps): every shortcut template's score and box (the
/// best one in its search zones, found or not), the ring's (+) slots and padlock, the minimap zone title and
/// coordinates and the lines in the phone's combat-log region, read with Windows OCR in place of ML Kit. Then the same
/// <see cref="ShortcutMap"/> on every frame scaled x0.9 and x1.1 (does the learned-scale search hold up on another
/// screen size). Run with: --phone &lt;dir&gt;
/// </summary>
internal static class PhoneProbe
{
    // Not used by ShortcutMap (yet): the padlock (ring zone) and the skill wheel's page tabs (bottom-right cluster).
    private static readonly string[] Locks = ["phone_ringlock", "phone_ringunlock"];
    private static readonly string[] WheelPages = ["phone_wheelpage1", "phone_wheelpage2", "phone_wheelpage3"];

    public static async Task<int> RunAsync(string dir)
    {
        string? assets = FindPhoneAssets();
        if (assets == null)
        {
            Console.WriteLine("src\\WuxAuto.Mobile\\Assets\\phone not found above " + AppContext.BaseDirectory);
            return 1;
        }

        // Only the phone templates (and any user override in the templates folder); the settings file is not read.
        AppPaths.Configure(AppPaths.Root, assets);
        var settings = new AppSettings();
        using var analyzer = new ScreenAnalyzer(settings, new OcrService());
        Console.WriteLine($"phone templates: {assets} ({string.Join(", ", analyzer.Library.Names)})");
        Console.WriteLine($"threshold {ShortcutMap.DefaultThreshold:0.00}; '*' = found by ShortcutMap, '-' = best score below it");

        string[] templates = ShortcutMap.ActionTemplates.Values
            .Concat(ShortcutMap.SkillTemplates.Values)
            .Append(ShortcutMap.JoystickTemplate)
            .Append(ShortcutMap.MenuTemplate)
            .ToArray();
        var found = templates.ToDictionary(t => t, _ => new List<(string Frame, double Score)>());
        var missed = templates.ToDictionary(t => t, _ => new List<(string Frame, double Score)>());
        var robust = new List<string>();
        var refreshMs = new List<double>();

        var files = Directory.GetFiles(dir, "*.png").Order().ToList();
        foreach (string path in files)
        {
            using var frame = ReadBgra(path);
            if (frame == null)
                continue;

            string name = Path.GetFileNameWithoutExtension(path);
            int w = frame.Width, h = frame.Height;
            Console.WriteLine();
            Console.WriteLine($"== {name} {w}x{h}");

            analyzer.ResetScaleCache();
            var map = new ShortcutMap(analyzer);
            var sw = Stopwatch.StartNew();
            map.Refresh(frame);
            sw.Stop();
            refreshMs.Add(sw.Elapsed.TotalMilliseconds);
            Console.WriteLine($"  ShortcutMap.Refresh: {sw.Elapsed.TotalMilliseconds:0} ms");

            foreach (string t in templates)
            {
                var best = BestAnyScale(analyzer, frame, t);
                bool hit = map.Matches.TryGetValue(t, out var m);
                double score = hit ? m!.Score : best?.Score ?? 0;
                string box = hit ? m!.Bounds.ToString() : best?.Bounds.ToString() ?? "-";
                string scale = hit ? $"x{m!.Scale:0.00}" : best != null ? $"x{best.Scale:0.00}" : "";
                if (analyzer.Library.Get(t) == null)
                    Console.WriteLine($"  {t,-22} (no template)");
                else
                    Console.WriteLine($"  {(hit ? '*' : '-')} {t,-20} {score:0.000} {box} {scale}");
                (hit ? found : missed)[t].Add((name, score));
            }

            var joy = map.Joystick;
            Console.WriteLine($"  joystick: {(joy is { } j ? $"centre {j.Cx},{j.Cy} r {j.Radius}" : "-")}");

            var ringZone = ShortcutMap.ZonesFor(ShortcutMap.JoystickTemplate, w, h)[0];
            var plus = analyzer.FindIcons(frame, "phone_ringplus", ringZone, ShortcutMap.DefaultThreshold, maxResults: 14);
            Console.WriteLine($"  ring (+) slots >= {ShortcutMap.DefaultThreshold:0.00}: {plus.Count}" +
                              (plus.Count > 0 ? $" (scores {plus.Min(p => p.Score):0.000}-{plus.Max(p => p.Score):0.000})" : ""));
            foreach (string extra in Locks.Concat(WheelPages))
            {
                var l = Locks.Contains(extra)
                    ? analyzer.FindIconAnyScale(frame, extra, ringZone, 0.0).Match
                    : BestAnyScale(analyzer, frame, extra);
                Console.WriteLine($"  {extra,-22} {(l != null ? $"{l.Score:0.000} {l.Bounds}" : "-")}");
            }

            PhoneRegions.Apply(settings, w, h);
            var minimap = settings.Regions.Minimap.ToPixels(w, h);
            var titleLines = (await analyzer.Ocr.RecognizeAsync(frame, minimap, 2.0)).Select(o => o.LineText.Trim()).Distinct().ToList();
            string title = titleLines.FirstOrDefault(l => l.StartsWith('[') && !l.Any(char.IsDigit) && TextMatcher.Normalize(l).Length >= 4) ?? "-";
            // M0's tight crop of the title + coordinates lines (1560,55 340x70 at 1920x1080), for comparison.
            var tight = PhoneLayout.ToPixels(1560, 55, 340, 70, PhoneAnchor.Right, w, h);
            var tightLines = (await analyzer.Ocr.RecognizeAsync(frame, tight, 2.0)).Select(o => o.LineText.Trim()).Distinct();
            var coords = await analyzer.ReadCoordinatesAsync(frame);
            Console.WriteLine($"  zone title: {title} | coordinates: {coords?.ToString() ?? "-"} (raw '{coords?.Raw}')");
            Console.WriteLine($"  title crop {tight}: {string.Join(" / ", tightLines)}");

            var log = settings.Regions.AttackMessage.ToPixels(w, h);
            var lines = ScreenAnalyzer.ToLines(await analyzer.Ocr.RecognizeAsync(frame, log, 1.5));
            Console.WriteLine($"  combat-log region {log}: {lines.Count} line(s)");
            foreach (var (text, bounds) in lines)
                Console.WriteLine($"    {bounds.Y,4} {Shorten(text)}");

            foreach (double factor in new[] { 0.9, 1.1 })
            {
                using var scaled = new Mat();
                CvInvoke.Resize(frame, scaled, new Size((int)Math.Round(w * factor), (int)Math.Round(h * factor)), 0, 0, Inter.Area);
                var other = new ShortcutMap(analyzer);
                other.Refresh(scaled);
                var parts = new List<string>();
                foreach (string t in templates.Where(t => map.Matches.ContainsKey(t)))
                {
                    var expected = map.Matches[t].Bounds;
                    if (!other.Matches.TryGetValue(t, out var s))
                    {
                        parts.Add($"{t} MISSED");
                        continue;
                    }

                    double dx = s.Bounds.CenterX - expected.CenterX * factor, dy = s.Bounds.CenterY - expected.CenterY * factor;
                    bool near = Math.Abs(dx) <= 6 && Math.Abs(dy) <= 6;
                    parts.Add($"{t} {s.Score:0.000}{(near ? "" : $" MOVED {dx:0},{dy:0}")}");
                }

                var extra = templates.Where(t => !map.Matches.ContainsKey(t) && other.Matches.ContainsKey(t)).ToList();
                foreach (string t in extra)
                    parts.Add($"{t} NEW {other.Matches[t].Score:0.000} {other.Matches[t].Bounds}");

                string line = $"  x{factor:0.0} ({scaled.Width}x{scaled.Height}): {string.Join("; ", parts)}";
                Console.WriteLine(line);
                robust.Add($"{name} {line.Trim()}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("== summary per template (found = ShortcutMap hit at the threshold; the best score on the frames without one)");
        foreach (string t in templates)
        {
            var f = found[t];
            var m = missed[t];
            string hitText = f.Count > 0 ? $"found on {f.Count} frame(s), {f.Min(x => x.Score):0.000}-{f.Max(x => x.Score):0.000}" : "found on 0 frames";
            string missText = m.Count > 0 ? $"not on {m.Count}, best there {m.Max(x => x.Score):0.000} ({m.MaxBy(x => x.Score).Frame})" : "not missing anywhere";
            Console.WriteLine($"  {t,-22} {(analyzer.Library.Get(t) == null ? "NO TEMPLATE" : $"{hitText}; {missText}")}");
        }

        int moved = robust.Count(r => r.Contains("MOVED") || r.Contains("MISSED") || r.Contains("NEW"));
        Console.WriteLine($"== robustness x0.9 / x1.1: {robust.Count} rescaled frames, {moved} with a missed / moved / new shortcut");
        foreach (string r in robust.Where(r => r.Contains("MOVED") || r.Contains("MISSED") || r.Contains("NEW")))
            Console.WriteLine($"  {r}");
        if (refreshMs.Count > 0)
            Console.WriteLine($"== ShortcutMap.Refresh (cold, no learned scale): median {refreshMs.Order().ElementAt(refreshMs.Count / 2):0} ms, max {refreshMs.Max():0} ms");

        return 0;
    }

    /// <summary>The best hit in the template's ShortcutMap zones at any score (so a miss shows how close it came).</summary>
    private static TemplateMatch? BestAnyScale(ScreenAnalyzer analyzer, Mat frame, string template)
    {
        TemplateMatch? best = null;
        foreach (var roi in ShortcutMap.ZonesFor(template, frame.Width, frame.Height))
        {
            var m = analyzer.FindIconAnyScale(frame, template, roi, 0.0).Match;
            if (m != null && (best == null || m.Score > best.Score))
                best = m;
        }

        return best;
    }

    /// <summary>
    /// The attack skill icons only (fast, no OCR): per PNG, every phone_attack template's best score and box in each of
    /// its zones (hotbar, cluster round the sword) at any scale, and which one <see cref="ShortcutMap.AttackSkill"/>
    /// picks. Run with: --phoneattack &lt;dir&gt; [--recurse]
    /// </summary>
    public static int Attack(string dir, bool recurse)
    {
        string? assets = FindPhoneAssets();
        if (assets == null)
        {
            Console.WriteLine("src\\WuxAuto.Mobile\\Assets\\phone not found above " + AppContext.BaseDirectory);
            return 1;
        }

        AppPaths.Configure(AppPaths.Root, assets);
        using var analyzer = new ScreenAnalyzer(new AppSettings(), new OcrService());
        Console.WriteLine($"threshold {ShortcutMap.DefaultThreshold:0.00}; zones: hotbar | cluster");
        foreach (string path in Directory.GetFiles(dir, "*.png", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order())
        {
            using var frame = ReadBgra(path);
            if (frame == null || frame.Width < 640)
                continue;

            analyzer.ResetScaleCache();
            var map = new ShortcutMap(analyzer);
            map.Refresh(frame);
            var pick = map.AttackSkill("1");
            string picked = pick is { } p
                ? map.Matches.First(m => ShortcutMap.AttackTemplates.Contains(m.Key) && m.Value.Bounds == p).Key
                : "-";
            Console.WriteLine($"== {Path.GetRelativePath(dir, path)} {frame.Width}x{frame.Height} pick: {picked} {pick?.ToString() ?? ""}");
            foreach (string t in ShortcutMap.AttackTemplates)
            {
                var parts = ShortcutMap.ZonesFor(t, frame.Width, frame.Height)
                    .Select((z, i) => analyzer.FindIconAnyScale(frame, i == 1 ? t + TemplateLibrary.WheelSuffix : t, z, 0.0).Match)
                    .Select(m => m == null ? "-" : $"{m.Score:0.000} {m.Bounds} x{m.Scale:0.00}");
                Console.WriteLine($"  {t,-14} {string.Join(" | ", parts)}");
            }
        }

        return 0;
    }

    private static Mat? ReadBgra(string path)
    {
        using var source = ImageFile.Read(path, ImreadModes.Unchanged);
        if (source.IsEmpty)
            return null;

        var frame = new Mat();
        if (source.NumberOfChannels == 4)
            source.CopyTo(frame);
        else
            CvInvoke.CvtColor(source, frame, ColorConversion.Bgr2Bgra);
        return frame;
    }

    private static string? FindPhoneAssets()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "src", "WuxAuto.Mobile", "Assets", "phone");
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string Shorten(string text) => text.Length <= 90 ? text : text[..87] + "...";
}
