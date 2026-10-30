using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Vision;

/// <summary>A world position read from the "x , y" label under the minimap title.</summary>
public sealed record GameCoordinates(int X, int Y, PixelRect Bounds, string Raw)
{
    public double DistanceTo(int x, int y) => Math.Sqrt(Math.Pow(X - (double)x, 2) + Math.Pow(Y - (double)y, 2));

    public override string ToString() => $"{X}, {Y}";
}

/// <summary>High level "what is on screen right now" queries used by the pilot and by the test scan.</summary>
public sealed class ScreenAnalyzer : IDisposable
{
    private readonly AppSettings _settings;

    /// <remarks>Takes ownership of <paramref name="ocr"/> and disposes it when the analyzer is disposed.</remarks>
    public ScreenAnalyzer(AppSettings settings, IOcrEngine ocr)
    {
        _settings = settings;
        Library = new TemplateLibrary();
        Matcher = new TemplateMatcher(Library);
        Compass = new CompassDetector(Library);
        Ocr = ocr;
    }

    public TemplateLibrary Library { get; }
    public TemplateMatcher Matcher { get; }
    public CompassDetector Compass { get; }
    public IOcrEngine Ocr { get; }

    /// <summary>
    /// Learned template scale per template name, keyed by client height. The PNGs may have been
    /// cropped at any resolution, so the 1080p assumption in <see cref="ScaleFor"/> is only a
    /// starting point - the first successful multi-scale search teaches the real scale, after which
    /// the fast single-scale path is used.
    /// </summary>
    private readonly Dictionary<(string Name, int Height), double> _learnedScale = new();

    /// <summary>Where the minimap's "x , y" line was last read, per client size (the compass anchor).</summary>
    private readonly Dictionary<(int Width, int Height), PixelRect> _minimapAnchor = new();

    /// <summary>
    /// "3653 , 16247": two numbers around a comma with a space on at least one side, or - because
    /// the thresholded comma often reads as a dot ("3489. 6867") - around a dot with 4+ digits on
    /// both sides. Stat bars like 100/100, 200.000 or 13.300 never match.
    /// </summary>
    private static readonly Regex CoordinatePair = new(
        @"(\d{3,6})(?:\s+,\s*|\s*,\s+)(\d{3,6})|(\d{4,6})\s*\.\s*(\d{4,6})", RegexOptions.Compiled);

    private static readonly Regex HasNumber = new(@"\d{3,}", RegexOptions.Compiled);

    /// <summary>Scale search range around the 1080p guess; wide enough for UI scaling (85%..145%).</summary>
    private const double MinScaleFactor = 0.70;
    private const double MaxScaleFactor = 1.55;
    private const double ScaleStep = 0.05;

    public double ScaleFor(int frameHeight) => TemplateMatcher.ScaleForFrame(frameHeight, _settings.Detection);

    public TemplateMatch? FindIcon(Mat frame, string templateName, PixelRect roi, double? threshold = null)
    {
        return FindIconTimed(frame, templateName, roi, threshold).Match;
    }

    /// <summary>All hits for a template, using the learned scale when it is known.</summary>
    public IReadOnlyList<TemplateMatch> FindIcons(Mat frame, string templateName, PixelRect roi, double? threshold = null, int maxResults = 6)
    {
        return FindWithLearnedScale(frame, templateName, roi, threshold ?? _settings.Detection.TemplateThreshold, maxResults);
    }

    public (TemplateMatch? Match, double Ms) FindIconTimed(Mat frame, string templateName, PixelRect roi, double? threshold = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var matches = FindWithLearnedScale(
            frame, templateName, roi, threshold ?? _settings.Detection.TemplateThreshold, maxResults: 1);
        stopwatch.Stop();

        var best = matches.Count > 0 ? matches[0] : null;
        return (best, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Best match over the whole scale range on every call, no learned scale. For templates whose
    /// score is followed over time (the searchlight while turning): a scale learned from one bad
    /// frame (x0.5 was seen) would otherwise stick and every later score would be measured wrong.
    /// </summary>
    public (TemplateMatch? Match, double Ms) FindIconAnyScale(Mat frame, string templateName, PixelRect roi, double threshold)
    {
        var stopwatch = Stopwatch.StartNew();
        var matches = Matcher.FindMultiScale(
            frame,
            templateName,
            roi,
            ScaleFor(frame.Height),
            threshold,
            minScaleFactor: MinScaleFactor,
            maxScaleFactor: MaxScaleFactor,
            scaleStep: ScaleStep,
            maxResults: 1);
        stopwatch.Stop();
        return (matches.Count > 0 ? matches[0] : null, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Resolution-independent lookup. The PNGs may have been cropped at any resolution, so the
    /// 1080p guess is only a starting point: a scale range is searched once, the winning scale is
    /// remembered per template and client height, and later frames use the fast single-scale path.
    /// </summary>
    private IReadOnlyList<TemplateMatch> FindWithLearnedScale(
        Mat frame,
        string templateName,
        PixelRect roi,
        double threshold,
        int maxResults)
    {
        var key = (templateName, frame.Height);

        // Fast path: the scale that worked last time at this resolution.
        if (_learnedScale.TryGetValue(key, out double learned))
        {
            var known = Matcher.Find(frame, templateName, roi, learned, threshold, maxResults);
            if (known.Count > 0)
                return known;
        }

        // Slow path: scan a generous scale range so any UI scale/resolution is covered.
        var matches = Matcher.FindMultiScale(
            frame,
            templateName,
            roi,
            ScaleFor(frame.Height),
            threshold,
            minScaleFactor: MinScaleFactor,
            maxScaleFactor: MaxScaleFactor,
            scaleStep: ScaleStep,
            maxResults: Math.Max(maxResults, 3));

        if (matches.Count > 0)
            _learnedScale[key] = matches[0].Scale;

        return matches;
    }

    /// <summary>Drop learned scales, e.g. after the user re-crops a template.</summary>
    public void ResetScaleCache()
    {
        _learnedScale.Clear();
        _minimapAnchor.Clear();
    }

    /// <summary>Re-read the PNGs and throw away every cached scale.</summary>
    public void ReloadTemplates()
    {
        Library.Reload();
        Matcher.ClearCache();
        ResetScaleCache();
    }

    public async Task<(TextHit? Hit, double Ms, IReadOnlyList<OcrWord> Words)> FindTextAsync(
        Mat frame,
        PixelRect roi,
        string text,
        int? anchorX = null,
        int? anchorY = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var words = await Ocr.RecognizeAsync(frame, roi, _settings.Detection.OcrScale);
        var hit = TextMatcher.FindBest(
            words,
            text,
            _settings.Detection.OcrMinSimilarity,
            anchorX,
            anchorY,
            Ocr.LastDurationMs);
        stopwatch.Stop();
        return (hit, stopwatch.Elapsed.TotalMilliseconds, words);
    }

    /// <summary>
    /// A row of the game's Settings window ("Font size   &lt;  110%  &gt;", "TAB target  &lt;  Default  &gt;"):
    /// the value read on the row of <paramref name="label"/> and the bright chevron glyph on either
    /// side of it. <paramref name="numeric"/> rows take the first word with a digit and fill
    /// <see cref="SettingRow.Percent"/>; text rows take the first OCR line right of the label.
    /// A dimmed chevron (the end of the option list) is still found (TAB target at Default, 1920x1080).
    /// <paramref name="chevronReach"/> &gt; 0: the chevrons are also looked for up to that many text heights right of the
    /// value, past the 30-height band (phone, MuMu 1920x1080: label 14 px high, band end x 1199, the font row's right
    /// chevron at ~1205 - "could not be set to 120%"; the desktop has the same cut-off, still open there).
    /// </summary>
    public async Task<SettingRow?> ReadSettingRowAsync(Mat frame, string label, bool numeric = true, int chevronReach = 0)
    {
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var (hit, _, _) = await FindTextAsync(frame, full, label);
        // The hit may be a single word ("Font" alone is under 60 % of "Font size", run 31): judge the line.
        if (hit == null || !(TextMatcher.CoversMost(hit.Text, label) || TextMatcher.CoversMost(hit.LineText, label)))
            return null;

        var labelBox = hit.LineBounds.Width > 0 ? hit.LineBounds : hit.Bounds;
        int h = Math.Max(8, labelBox.Height);
        var band = PixelRect.FromBounds(labelBox.Right + h, labelBox.Y - h, Math.Min(frame.Width, labelBox.Right + h * 30), labelBox.Bottom + h)
            .Clamp(frame.Width, frame.Height);

        var words = await Ocr.RecognizeAsync(frame, band, 2.0);
        var value = numeric ? NumericValue(words) : TextValue(words, band);
        if (value == null)
            return new SettingRow(labelBox, null, null, null, null);

        int? percent = null;
        if (numeric)
        {
            string digits = new(value.Value.Text.Where(char.IsDigit).ToArray());
            percent = int.TryParse(digits, out int v) ? v : null;
        }

        // Chevrons: the nearest bright glyph left and right of the value, on the same row.
        var box = value.Value.Bounds;
        var chevronBand = chevronReach > 0 && box.Right + h * chevronReach > band.Right
            ? PixelRect.FromBounds(band.X, band.Y, box.Right + h * chevronReach, band.Bottom).Clamp(frame.Width, frame.Height)
            : band;
        var blobs = FindBrightBlobs(frame, chevronBand)
            .Where(b => b.Height >= h * 0.6 && b.Height <= h * 2.5 && b.Width <= h * 1.6)
            .ToList();
        PixelRect? left = blobs.Where(b => b.Right <= box.X - 2).OrderByDescending(b => b.CenterX).Select(b => (PixelRect?)b).FirstOrDefault();
        PixelRect? right = blobs.Where(b => b.X >= box.Right + 2).OrderBy(b => b.CenterX).Select(b => (PixelRect?)b).FirstOrDefault();
        return new SettingRow(labelBox, box, percent, left, right, value.Value.Text);

        static (string Text, PixelRect Bounds)? NumericValue(IReadOnlyList<OcrWord> words)
        {
            var word = words.FirstOrDefault(w => w.Text.Any(char.IsDigit));
            return word == null ? null : (word.Text, word.Bounds);
        }

        // The leftmost line with real words ("Default", "Nearest enemy", "Enemy worth CP"). Words of
        // fewer than 2 letters are dropped: a chevron misread as "<" or "K". The line must start in
        // the left three quarters of the band - text further right is the game world beyond the
        // Settings window, not the value.
        static (string Text, PixelRect Bounds)? TextValue(IReadOnlyList<OcrWord> words, PixelRect band)
        {
            var line = words
                .Where(w => w.Text.Count(char.IsLetter) >= 2)
                .GroupBy(w => w.LineBounds)
                .OrderBy(g => g.Min(w => w.Bounds.X))
                .FirstOrDefault();
            if (line == null || line.Min(w => w.Bounds.X) > band.X + band.Width * 3 / 4)
                return null;

            var ordered = line.OrderBy(w => w.Bounds.X).ToList();
            var bounds = PixelRect.FromBounds(
                ordered.Min(w => w.Bounds.X), ordered.Min(w => w.Bounds.Y),
                ordered.Max(w => w.Bounds.Right), ordered.Max(w => w.Bounds.Bottom));
            return (string.Join(' ', ordered.Select(w => w.Text)), bounds);
        }
    }

    /// <summary>
    /// The phone's "Interface scale" row (MuMu 2560x1440, 2026-10-30: "Interface scale [AUTO] -  (AUTO)  +", one + read
    /// "1.01"): the value is the word that reads as a number with two decimals (<see cref="ParseScale"/>), else the
    /// rightmost "AUTO" word (the value; the left one is the AUTO button) = 1.00. The + is the nearest bright blob right of
    /// the value; the - the nearest flat dash left of it (its own search, see below).
    /// </summary>
    public async Task<ScaleRow?> ReadScaleRowAsync(Mat frame, string label = "Interface scale")
    {
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var (hit, _, _) = await FindTextAsync(frame, full, label);
        if (hit == null || !(TextMatcher.CoversMost(hit.Text, label) || TextMatcher.CoversMost(hit.LineText, label)))
            return null;

        var labelBox = hit.LineBounds.Width > 0 ? hit.LineBounds : hit.Bounds;
        int h = Math.Max(8, labelBox.Height);
        var band = PixelRect.FromBounds(labelBox.Right + h / 2, labelBox.Y - h, Math.Min(frame.Width, labelBox.Right + h * 30), labelBox.Bottom + h)
            .Clamp(frame.Width, frame.Height);

        var words = await Ocr.RecognizeAsync(frame, band, 2.0);
        var number = words.Select(w => (Word: w, Value: ParseScale(w.Text))).FirstOrDefault(x => x.Value != null);
        OcrWord? valueWord = number.Word;
        double? value = number.Value;
        bool auto = false;
        if (valueWord == null)
        {
            valueWord = words.Where(w => TextMatcher.Normalize(w.Text).Contains("auto", StringComparison.Ordinal))
                .OrderByDescending(w => w.Bounds.CenterX).FirstOrDefault();
            if (valueWord == null)
                return new ScaleRow(labelBox, null, false, null, null, null);
            auto = true;
            value = 1.00;
        }

        var box = valueWord.Bounds;
        var blobs = FindBrightBlobs(frame, band)
            .Where(b => b.Width <= h * 1.6 && b.Height <= h * 1.6 && (b.Height >= h * 0.5 || b.Width >= h * 0.5))
            .Where(b => b.CenterY >= box.Y - h / 2 && b.CenterY <= box.Bottom + h / 2)
            .ToList();
        // The - is a flat dash, ~2 px tall at 2560x1440 - under the blob search's usual 3 px floor, so the AUTO button's right
        // border (a tall thin line left of it) was the nearest blob and was tapped as the '-' (MuMu, 2026-10-30: "tapped '-'
        // at 1293,395" set the scale to AUTO). Its own search: flat blobs of 1 px and up left of the value, none tall.
        var dashBand = PixelRect.FromBounds(Math.Max(band.X, box.X - h * 6), box.CenterY - h / 2, box.X - 2, box.CenterY + h / 2 + 1)
            .Clamp(frame.Width, frame.Height);
        PixelRect? minus = dashBand.Width < 4 || dashBand.Height < 2 ? null : FindBrightBlobs(frame, dashBand, minHeight: 1)
            .Where(b => b.Width >= Math.Max(6, h * 0.4) && b.Width >= b.Height * 3 && b.Height <= Math.Max(4, h / 4))
            .OrderByDescending(b => b.CenterX).Select(b => (PixelRect?)b).FirstOrDefault();
        PixelRect? plus = blobs.Where(b => b.X >= box.Right + 2).OrderBy(b => b.CenterX).Select(b => (PixelRect?)b).FirstOrDefault();
        return new ScaleRow(labelBox, value, auto, minus, plus, valueWord.Text);
    }

    /// <summary>An interface scale as the row shows it ("1.01", "(1.10)", "1,05"); null for anything else.</summary>
    public static double? ParseScale(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^\(?([0-9])[.,]([0-9]{2})\)?$");
        return m.Success ? int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[2].Value) / 100.0 : null;
    }

    /// <summary>
    /// Finds the map list's page arrows from the "Spawns" column header: they are the light
    /// glyphs in the band just above that word. Picked as bright blobs, so there is no template
    /// and no fixed offset to go stale when the map window scales differently.
    /// </summary>
    public async Task<(PixelRect? Up, PixelRect? Down)> FindMapScrollArrowsAsync(Mat frame, PixelRect roi)
    {
        var (spawns, _, _) = await FindTextAsync(frame, roi, "Spawns");
        if (spawns == null)
            return (null, null);

        int h = Math.Max(8, spawns.Bounds.Height);
        int left = Math.Max(roi.X, spawns.Bounds.X - h * 5);
        int right = Math.Min(roi.Right, spawns.Bounds.Right + h * 5);
        // The arrows sit just above the header text; the band must stop above the text itself,
        // otherwise the first row's '>' (which lives in the same column, one line lower) gets
        // picked as the down arrow.
        int top = Math.Max(roi.Y, spawns.Bounds.Y - (int)(h * 3.0));
        int bottom = Math.Min(roi.Bottom, spawns.Bounds.Y - Math.Max(1, h / 4));
        if (right - left < 8 || bottom - top < 6)
            return (null, null);

        var band = new PixelRect(left, top, right - left, bottom - top);
        var blobs = FindBrightBlobs(frame, band)
            .Where(b => b.Width >= h * 0.5 && b.Width <= h * 4.0)
            .Where(b => b.Height >= 3 && b.Height <= h * 1.4)
            .OrderBy(b => b.CenterX)
            .ToList();

        if (blobs.Count == 0)
            return (null, null);

        PixelRect? up = blobs.Count >= 2 ? Centre(blobs[0], h) : null;
        PixelRect? down = Centre(blobs[^1], h);
        return (up, down);

        static PixelRect Centre(PixelRect b, int h)
            => new(b.CenterX - h / 2, b.CenterY - h / 2, h, h);
    }


    /// <summary>
    /// The game's confirm dialog ("Fist Scroll Box Confirm to use?" over CANCEL / OK; the sell
    /// dialog looks the same): a line containing <paramref name="text"/> (one letter of OCR noise
    /// allowed per word) and, under it, the button. Read first: a line that is exactly
    /// <paramref name="button"/> ("0K" counts for OK) within 8 text heights below the message and
    /// 12 to either side. Else the lowest row of gold buttons there, its right-most one (OK and
    /// USE ALL are gold, CANCEL is dark; OK sits bottom right). Whole-frame OCR at x1, 40-600 ms.
    /// </summary>
    public async Task<ConfirmDialog?> FindConfirmDialogAsync(Mat frame, string text, string button, List<string>? trace = null)
    {
        string wanted = TextMatcher.Normalize(text);
        string wantedButton = TextMatcher.Normalize(button);
        if (wanted.Length < 3 || wantedButton.Length == 0)
            return null;

        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        // x1: the dialog text is large and this runs every frame while armed (speed, user).
        var words = await Ocr.RecognizeAsync(frame, full, 1.0);
        var lines = words.GroupBy(w => w.LineBounds).Select(g => (Bounds: g.Key, Text: g.First().LineText, Words: g.ToList())).ToList();
        trace?.Add($"{lines.Count} line(s): {string.Join(" | ", lines.Select(l => l.Text))}");

        bool HasWanted(IEnumerable<OcrWord> lineWords, string lineText)
            => TextMatcher.Normalize(lineText).Contains(wanted, StringComparison.Ordinal)
               || lineWords.Any(w => TextMatcher.Normalize(w.Text) is { Length: >= 5 } word
                                     && Math.Abs(word.Length - wanted.Length) <= 1
                                     && TextMatcher.Levenshtein(word, wanted) <= 1);

        // The widest matching line: the message, not a stray word somewhere in the chat.
        var message = lines.Where(l => HasWanted(l.Words, l.Text)).OrderByDescending(l => l.Bounds.Width).FirstOrDefault();
        if (message.Text == null)
            return null;

        // Sizes in text heights: the dialog keeps its pixel size at every resolution (user's crop:
        // text 17 px, OK 180x45 some 105 px under the message, 98 px right of its centre).
        int h = Math.Max(8, message.Bounds.Height);
        int reachY = 8 * h;
        int reachX = 12 * h;
        bool Under(PixelRect box)
            => box.Y > message.Bounds.Bottom && box.CenterY - message.Bounds.Bottom <= reachY
               && Math.Abs(box.CenterX - message.Bounds.CenterX) <= reachX;

        bool IsButtonText(string value)
        {
            string normalized = TextMatcher.Normalize(value);
            return normalized == wantedButton || (wantedButton == "ok" && normalized == "0k");
        }

        // A line that is only the button word (never the word inside a longer message).
        var read = lines.Where(l => IsButtonText(l.Text) && Under(l.Bounds))
            .OrderBy(l => l.Bounds.Y)
            .Select(l => (PixelRect?)l.Bounds)
            .FirstOrDefault();
        if (read is { } readBox)
        {
            trace?.Add($"message '{message.Text}' {message.Bounds}; '{button}' read at {readBox}");
            return new ConfirmDialog(message.Text, message.Bounds, readBox, "read");
        }

        var gold = FindGoldButtons(frame, minWidth: 3 * h, minHeight: (int)(1.2 * h), maxHeight: 5 * h).Where(Under).ToList();
        trace?.Add($"message '{message.Text}' {message.Bounds}; '{button}' not read, {gold.Count} gold button(s) under it: {string.Join(", ", gold)}");
        if (gold.Count == 0)
            return null;

        int lowest = gold.Max(b => b.CenterY);
        var bottomRow = gold.Where(b => lowest - b.CenterY <= b.Height / 2).OrderByDescending(b => b.CenterX).First();
        return new ConfirmDialog(message.Text, message.Bounds, bottomRow, "gold button");
    }

    /// <summary>
    /// Gold buttons of the login screens (LOG IN, CONNECT, OK, ...): their dark text on gold is
    /// often unreadable for OCR, but the filled gold rectangle is easy to find by colour. Thin gold
    /// frame lines are dropped by size, shape and fill ratio. Largest first.
    /// </summary>
    public IReadOnlyList<PixelRect> FindGoldButtons(Mat frame)
        => FindGoldButtons(frame, (int)(frame.Width * 0.06), (int)(frame.Height * 0.025), (int)(frame.Height * 0.12));

    /// <summary>Gold buttons with explicit size limits (pixels) - for dialogs whose size does not follow the client's.</summary>
    public IReadOnlyList<PixelRect> FindGoldButtons(Mat frame, int minWidth, int minHeight, int maxHeight)
    {
        using var bgr = new Mat();
        CvInvoke.CvtColor(frame, bgr, ColorConversion.Bgra2Bgr);
        using var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        using var mask = new Mat();
        // Measured on the LOG IN button: H 15-18, S 128-143, V 103-232. The sunset behind the login
        // panel has the same hue but S ~85, so saturation is what separates them.
        CvHelpers.InRange(hsv, new MCvScalar(12, 110, 95), new MCvScalar(25, 255, 255), mask);
        using var kernel = CvHelpers.StructuringElement(MorphShapes.Rectangle, new Size(3, 3));
        CvHelpers.MorphologyEx(mask, mask, MorphOp.Close, kernel);

        var buttons = new List<PixelRect>();
        foreach (var (box, contourArea) in CvHelpers.ExternalContours(mask))
        {
            double aspect = box.Width / (double)Math.Max(1, box.Height);
            double fill = contourArea / Math.Max(1.0, box.Width * box.Height);
            if (box.Width < minWidth || box.Height < minHeight || box.Height > maxHeight)
                continue;
            if (aspect < 1.8 || aspect > 12 || fill < 0.5)
                continue;

            buttons.Add(new PixelRect(box.X, box.Y, box.Width, box.Height));
        }

        return buttons.OrderByDescending(b => b.Width * b.Height).ToList();
    }

    /// <summary>
    /// The next stage's gate glows red/orange in the distance once the camera is tilted up.
    /// Biggest saturated red/orange patch in <paramref name="roi"/>, or null.
    /// </summary>
    public PixelRect? FindRedOrangeGlow(Mat frame, PixelRect roi, int minArea)
    {
        using var crop = new Mat(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        CvInvoke.CvtColor(crop, bgr, ColorConversion.Bgra2Bgr);
        using var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        using var low = new Mat();
        using var high = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(0, 120, 120), new MCvScalar(22, 255, 255), low);
        CvHelpers.InRange(hsv, new MCvScalar(168, 120, 120), new MCvScalar(180, 255, 255), high);
        using var mask = new Mat();
        CvInvoke.BitwiseOr(low, high, mask);
        using var kernel = CvHelpers.StructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        CvHelpers.MorphologyEx(mask, mask, MorphOp.Close, kernel);

        var best = CvHelpers.ExternalContours(mask)
            .Where(t => t.Area >= minArea)
            .OrderByDescending(t => t.Area)
            .FirstOrDefault();
        if (best.Area <= 0)
            return null;

        return new PixelRect(roi.X + best.Box.X, roi.Y + best.Box.Y, best.Box.Width, best.Box.Height);
    }

    /// <summary>
    /// How much of the gold ring the game draws round an active skill's icon is there: the share of
    /// 36 directions from the icon's centre with a gold pixel (H 15-30, S 60-150, V 150-240; the
    /// user's crop of the active Run skill: a flat 185,161,107, lighter 213,190,133 at the outside;
    /// the icon's own flame is more saturated, S 180+) between 0.68 and 0.85 of <paramref name="icon"/>'s
    /// width out from its centre - the ring sits at radius 23-27 round the 33 px template of that crop.
    /// The user's active icon: 0.5 (the icon covers parts of the ring). Under a modal dialog the whole
    /// screen is dimmed (a 1920x1080 frame: ring V 66-86, the icon's flame 102 instead of 255), so the
    /// brightness floor follows the brightest pixel of the icon (150 of 255). 0 when the ring would
    /// leave the frame. <paramref name="minFraction"/> / <paramref name="maxFraction"/>: the band, as fractions of the
    /// icon's width (the phone's Run orb has its ring further out, see <c>AutomationPilot.PhoneRunRingMin</c>).
    /// </summary>
    public double RingFraction(Mat frame, PixelRect icon, double minFraction = 0.68, double maxFraction = 0.85)
    {
        double cx = icon.X + icon.Width / 2.0;
        double cy = icon.Y + icon.Height / 2.0;
        double rMin = icon.Width * minFraction;
        double rMax = icon.Width * maxFraction;
        int margin = (int)Math.Ceiling(rMax) + 1;
        var roi = new PixelRect((int)cx - margin, (int)cy - margin, margin * 2 + 1, margin * 2 + 1);
        if (roi.X < 0 || roi.Y < 0 || roi.Right > frame.Width || roi.Bottom > frame.Height || rMin < 2)
            return 0;

        using var crop = new Mat(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        if (crop.NumberOfChannels == 4)
            CvInvoke.CvtColor(crop, bgr, ColorConversion.Bgra2Bgr);
        else
            crop.CopyTo(bgr);
        using var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);

        var iconRect = new Rectangle(icon.X - roi.X, icon.Y - roi.Y, icon.Width, icon.Height);
        using var iconHsv = new Mat(hsv, iconRect);
        using var value = new Mat();
        CvInvoke.ExtractChannel(iconHsv, value, 2);
        var (maxValue, _) = CvHelpers.MaxLoc(value);
        double minValue = Math.Max(40, 150 * maxValue / 255);

        using var mask = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(15, 60, minValue), new MCvScalar(30, 150, 240), mask);
        byte[] maskPixels = CvHelpers.Bytes(mask);

        const int directions = 36;
        int hits = 0;
        for (int i = 0; i < directions; i++)
        {
            double angle = i * 2 * Math.PI / directions;
            bool hit = false;
            for (double r = rMin; r <= rMax && !hit; r += 1.0)
            {
                int x = (int)Math.Round(cx + r * Math.Cos(angle)) - roi.X;
                int y = (int)Math.Round(cy + r * Math.Sin(angle)) - roi.Y;
                hit = x >= 0 && y >= 0 && x < mask.Width && y < mask.Height && maskPixels[y * mask.Width + x] != 0;
            }

            if (hit)
                hits++;
        }

        return hits / (double)directions;
    }

    /// <summary>
    /// The magenta ring the game draws on the floor under the selected monster (user crop:
    /// H 143-150, S 95-145, V 195-255). Searched in the game world only - the HUD has a few
    /// magenta pixels in fixed places (hotbar, buff icons). On 267 saved frames every hit of
    /// 1.5 per 10 000 of the client or more was a real selection ring.
    /// </summary>
    public PixelRect? FindSelectionRing(Mat frame, double minFraction = 1.5e-4)
    {
        var roi = new PixelRect((int)(frame.Width * 0.05), (int)(frame.Height * 0.12),
            (int)(frame.Width * 0.70), (int)(frame.Height * 0.78)).Clamp(frame.Width, frame.Height);
        if (roi.Width < 8 || roi.Height < 8)
            return null;

        using var crop = new Mat(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        if (crop.NumberOfChannels == 4)
            CvInvoke.CvtColor(crop, bgr, ColorConversion.Bgra2Bgr);
        else
            crop.CopyTo(bgr);
        using var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        using var mask = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(138, 89, 190), new MCvScalar(156, 255, 255), mask);

        int count = CvInvoke.CountNonZero(mask);
        if (count < frame.Width * (double)frame.Height * minFraction)
            return null;

        var box = CvInvoke.BoundingRectangle(mask);
        return new PixelRect(roi.X + box.X, roi.Y + box.Y, box.Width, box.Height);
    }

    /// <summary>The top of the minimap panel, where its zone title sits ("[Blossom Garden" at 1725-1880, 70-95 of 2048x1152).</summary>
    public static readonly NormRect MinimapTitleStrip = new(0.76, 0.03, 0.23, 0.08);

    /// <summary>
    /// The text lines of the minimap band (<see cref="RegionSettings.Minimap"/>, OCR x2), for the zone title. Live
    /// 2026-10-29: over Blossom Garden's bright yellow map the whole-band read returned no words at all, while a small crop
    /// round the title read "[Blossom Garden" cleanly - so without a title-like line ('[', 4+ letters, no digits) the
    /// <see cref="MinimapTitleStrip"/> is read too, x2 then x3. Dark minimaps (the Labyrinth, LOD) read in the band as before.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadMinimapLinesAsync(Mat frame)
    {
        var band = _settings.Regions.Minimap.ToPixels(frame.Width, frame.Height);
        var lines = (await Ocr.RecognizeAsync(frame, band, 2.0)).Select(w => w.LineText.Trim()).Distinct().ToList();
        if (lines.Any(IsTitleLike))
            return lines;

        var strip = MinimapTitleStrip.ToPixels(frame.Width, frame.Height);
        foreach (double scale in new[] { 2.0, 3.0 })
        {
            lines.AddRange((await Ocr.RecognizeAsync(frame, strip, scale)).Select(w => w.LineText.Trim()).Where(l => !lines.Contains(l)).Distinct());
            if (lines.Any(IsTitleLike))
                break;
        }

        return lines;

        static bool IsTitleLike(string line) => line.Contains('[') && !line.Any(char.IsDigit) && TextMatcher.Normalize(line).Length >= 4;
    }

    /// <summary>Where the Blossom pillars are looked for: above the character, left of the minimap panel.</summary>
    public static readonly NormRect PillarBand = new(0.15, 0.05, 0.62, 0.45);

    /// <summary>
    /// Auto Blossom (user, 2026-10-29): the two pillar bases the camera is lined up on - the user's zoomed-out frame had them
    /// at 0.42 and 0.62 w, ~0.22 h. A pair = two hits of <paramref name="templateName"/> in <see cref="PillarBand"/> on about
    /// one row (|dy| &lt; 0.05 h), 0.10-0.40 w apart; with more, the pair whose midpoint is nearest the centre. Null = none.
    /// </summary>
    public PillarPair? FindPillarPair(Mat frame, string templateName, double threshold)
    {
        var roi = PillarBand.ToPixels(frame.Width, frame.Height);
        // The matcher returns the same base more than once (at several scales: 0.979 three times on the user's frame), so
        // ask for more and keep the best hit per place - duplicates must not crowd the second pillar out.
        var hits = new List<TemplateMatch>();
        foreach (var hit in FindIcons(frame, templateName, roi, threshold, maxResults: 10).OrderByDescending(h => h.Score))
        {
            if (!hits.Any(h => Math.Abs(h.Bounds.CenterX - hit.Bounds.CenterX) < h.Bounds.Width / 2
                               && Math.Abs(h.Bounds.CenterY - hit.Bounds.CenterY) < h.Bounds.Height))
                hits.Add(hit);
        }

        PillarPair? best = null;
        for (int i = 0; i < hits.Count; i++)
        {
            for (int j = i + 1; j < hits.Count; j++)
            {
                var (a, b) = hits[i].Bounds.CenterX <= hits[j].Bounds.CenterX ? (hits[i], hits[j]) : (hits[j], hits[i]);
                double apart = (b.Bounds.CenterX - a.Bounds.CenterX) / (double)frame.Width;
                double dy = Math.Abs(b.Bounds.CenterY - a.Bounds.CenterY) / (double)frame.Height;
                if (apart < 0.10 || apart > 0.40 || dy >= 0.05)
                    continue;

                double offset = ((a.Bounds.CenterX + b.Bounds.CenterX) / 2.0 - frame.Width / 2.0) / frame.Width;
                if (best == null || Math.Abs(offset) < Math.Abs(best.OffsetFraction))
                    best = new PillarPair(a, b, offset);
            }
        }

        return best;
    }

    /// <summary>
    /// LOD (user, 2026-10-24): the pink ring round the boss's feet. Under the boss's blue skill light
    /// it is pale (live: H ~150, S 50-80, V 255), too pale for <see cref="FindSelectionRing"/>, so the
    /// colour band is wider. Not the box of every pink pixel either: the biggest patch that lies flat
    /// (a ring on the floor seen from above is wider than tall), plus the pink pieces next to it - the
    /// boss's legs and the character cut the ring into arcs.
    /// </summary>
    public PixelRect? FindBossRing(Mat frame)
    {
        var roi = WorldArea(frame);
        if (roi.Width < 8 || roi.Height < 8)
            return null;

        using var hsv = ToHsv(frame, roi);
        using var mask = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(135, 35, 170), new MCvScalar(162, 255, 255), mask);
        int k = Math.Max(3, frame.Height / 90) | 1;
        using var kernel = CvHelpers.StructuringElement(MorphShapes.Ellipse, new Size(k, k));
        CvHelpers.MorphologyEx(mask, mask, MorphOp.Close, kernel);

        double minPixels = frame.Width * (double)frame.Height * 1.5e-4;
        var parts = Components(mask).Where(c => c.Pixels >= minPixels / 6).ToList();
        var seed = parts
            .Where(c => c.Pixels >= minPixels && c.Box.Width >= frame.Width * 0.04 && c.Box.Width >= c.Box.Height)
            .OrderByDescending(c => c.Pixels)
            .Select(c => (Rectangle?)c.Box)
            .FirstOrDefault();
        if (seed is not { } ring)
            return null;

        // The boss's legs and the character stand on the ring and cut it into pieces: add every
        // flat-ish pink piece close to it.
        int reach = (int)(frame.Height * 0.05);
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var box = parts[i].Box;
                if (box.Height > box.Width * 2 || !Inflate(ring, reach).IntersectsWith(box))
                    continue;

                var union = Rectangle.Union(ring, box);
                parts.RemoveAt(i);
                if (union == ring)
                    continue;

                ring = union;
                grew = true;
            }
        }

        return new PixelRect(roi.X + ring.X, roi.Y + ring.Y, ring.Width, ring.Height);

        static Rectangle Inflate(Rectangle r, int by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
    }

    /// <summary>
    /// How far the character is from the centre of the boss's ring (<see cref="FindBossRing"/>), in
    /// ring radii: up to 1 = on or inside it. The camera follows the character, so its feet are at
    /// (0.5 w, 0.54 h). The ring lies flat, about half as tall as wide at the game's camera pitch (saved
    /// 1920x1080 frames: 0.46-0.5). A flatter box is only part of it - with the character under the boss,
    /// the boss's body hides all but the front arc (a 160x50 arc read 2.0 from its own centre) - so the
    /// distance is also taken from where the whole ring's centre would be with the front or the back arc
    /// seen, and the nearest counts.
    /// </summary>
    public static double DistanceFromRing(PixelRect ring, int frameWidth, int frameHeight)
    {
        double meX = frameWidth / 2.0, meY = frameHeight * 0.54;
        double rx = Math.Max(1, ring.Width / 2.0);
        double ry = Math.Max(Math.Max(1, ring.Height / 2.0), ring.Width / 4.0);
        double From(double centreY) => Math.Sqrt(Math.Pow((meX - ring.CenterX) / rx, 2) + Math.Pow((meY - centreY) / ry, 2));

        double away = From(ring.Y + ring.Height / 2.0);
        if (ring.Height < ring.Width * 0.4)
            away = Math.Min(away, Math.Min(From(ring.Bottom - ry), From(ring.Y + ry)));
        return away;
    }

    /// <summary>
    /// LOD (user, 2026-10-24): the belt hanging down the boss's front, four cyan circles one above the
    /// other. Found as 3+ cyan patches stacked in one column, or one tall thin cyan patch when the
    /// glow joins them.
    /// </summary>
    public PixelRect? FindBossBelt(Mat frame)
    {
        var roi = WorldArea(frame);
        if (roi.Width < 8 || roi.Height < 8)
            return null;

        using var hsv = ToHsv(frame, roi);
        using var mask = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(80, 120, 170), new MCvScalar(100, 255, 255), mask);

        double unit = frame.Height / 1080.0;
        var blobs = Components(mask)
            .Where(c => c.Pixels >= 40 * unit * unit && c.Box.Width <= frame.Height * 0.08)
            .Select(c => c.Box)
            .ToList();

        Rectangle? found = blobs
            .Where(b => b.Height >= frame.Height * 0.10 && b.Height >= 3 * b.Width)
            .OrderByDescending(b => b.Height)
            .Select(b => (Rectangle?)b)
            .FirstOrDefault();

        // A chain: from each patch downwards, the next patch in the same column within a gap.
        double column = frame.Height * 0.03, gap = frame.Height * 0.08;
        foreach (var top in blobs.OrderBy(b => b.Y))
        {
            var chain = new List<Rectangle> { top };
            var last = top;
            foreach (var next in blobs.Where(b => b.Y > last.Y).OrderBy(b => b.Y))
            {
                if (Math.Abs(CenterX(next) - CenterX(top)) <= column && next.Y - last.Bottom <= gap)
                {
                    chain.Add(next);
                    last = next;
                }
            }

            if (chain.Count < 3 || last.Bottom - top.Y < frame.Height * 0.08)
                continue;

            int left = chain.Min(b => b.X), right = chain.Max(b => b.Right);
            var box = new Rectangle(left, top.Y, right - left, last.Bottom - top.Y);
            if (found is not { } f || box.Height > f.Height)
                found = box;
        }

        return found is { } belt ? new PixelRect(roi.X + belt.X, roi.Y + belt.Y, belt.Width, belt.Height) : null;

        static double CenterX(Rectangle r) => r.X + r.Width / 2.0;
    }

    /// <summary>The game world between the HUD panels, where the ring and boss detectors look.</summary>
    private static PixelRect WorldArea(Mat frame) => new PixelRect((int)(frame.Width * 0.05), (int)(frame.Height * 0.12),
        (int)(frame.Width * 0.70), (int)(frame.Height * 0.78)).Clamp(frame.Width, frame.Height);

    /// <summary>Connected patches of a mask: bounding box and pixel count.</summary>
    private static List<(Rectangle Box, int Pixels)> Components(Mat mask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = CvInvoke.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        // One row per label: left, top, width, height, area.
        int[] table = CvHelpers.Int32s(stats);
        int columns = stats.Cols;
        var list = new List<(Rectangle, int)>(Math.Max(0, count - 1));
        for (int i = 1; i < count; i++)
        {
            int row = i * columns;
            var box = new Rectangle(table[row], table[row + 1], table[row + 2], table[row + 3]);
            list.Add((box, table[row + 4]));
        }

        return list;
    }

    /// <summary>
    /// The gold-framed checkbox left of a settings label (autohunt window, 1024x768: box centre
    /// ~20 px left of the label). Found by its gold border; falls back to 1.7 text heights left.
    /// </summary>
    public PixelRect FindCheckboxLeftOf(Mat frame, PixelRect label)
    {
        int h = Math.Max(6, label.Height);
        var fallback = new PixelRect(label.X - (int)(1.7 * h) - h / 2, label.CenterY - h / 2, h, h);
        var band = new PixelRect(label.X - 4 * h, label.CenterY - h - h / 2, 4 * h - 2, 3 * h).Clamp(frame.Width, frame.Height);
        if (band.Width < 4 || band.Height < 4)
            return fallback;

        using var hsv = ToHsv(frame, band);
        using var mask = new Mat();
        CvHelpers.InRange(hsv, new MCvScalar(10, 70, 110), new MCvScalar(32, 255, 255), mask);
        if (CvInvoke.CountNonZero(mask) < h)
            return fallback;

        // The rows sit ~2 px apart, so the gold frames of the boxes above and below touch this one
        // and merge into one tall outline. Its width is still the box size: the box is that square,
        // centred on the label's row.
        int labelCy = label.CenterY - band.Y;
        var best = CvHelpers.ExternalContours(mask).Select(c => c.Box)
            .Where(r => r.Width >= h * 0.6 && r.Width <= h * 3 && r.Y <= labelCy && r.Bottom >= labelCy)
            .OrderByDescending(r => r.Right)
            .Select(r => (Rectangle?)r)
            .FirstOrDefault();
        if (best is not { } r0)
            return fallback;

        int side = r0.Width;
        return new PixelRect(band.X + r0.X, label.CenterY - side / 2, side, side);
    }

    /// <summary>
    /// A ticked box has a bright tick inside (inner max ~200); an empty one stays dark (~68).
    /// </summary>
    public bool IsCheckboxTicked(Mat frame, PixelRect box)
    {
        int padX = Math.Max(1, box.Width / 4), padY = Math.Max(1, box.Height / 4);
        var inner = new PixelRect(box.X + padX, box.Y + padY, box.Width - 2 * padX, box.Height - 2 * padY).Clamp(frame.Width, frame.Height);
        if (inner.Width < 2 || inner.Height < 2)
            return false;

        using var crop = new Mat(frame, new Rectangle(inner.X, inner.Y, inner.Width, inner.Height));
        using var gray = new Mat();
        CvInvoke.CvtColor(crop, gray, crop.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);
        var (max, _) = CvHelpers.MaxLoc(gray);
        return max > 140;
    }

    /// <summary>
    /// A dialog's X right of its title (AUTO BUFF: title ends ~878, X at 905,116 at 1024x768): the
    /// first bright blob in the band right of the title, else two text heights right of it.
    /// </summary>
    public PixelRect FindCloseButtonRightOf(Mat frame, PixelRect title)
    {
        int h = Math.Max(6, title.Height);
        var fallback = new PixelRect(title.Right + 2 * h - h / 2, title.CenterY - h / 2, h, h);
        var band = new PixelRect(title.Right + 2, title.Y - h, 6 * h, title.Height + 2 * h).Clamp(frame.Width, frame.Height);
        if (band.Width < 4 || band.Height < 4)
            return fallback;

        var blob = FindBrightBlobs(frame, band)
            .Where(b => b.Width <= 3 * h && b.Height <= 3 * h && b.Width >= 3 && b.Height >= 3)
            .OrderBy(b => b.X)
            .Select(b => (PixelRect?)b)
            .FirstOrDefault();
        return blob ?? fallback;
    }

    /// <summary>
    /// Auto War: the X right of a window title found the way <see cref="FindCloseButtonRightOf"/> finds it (the first
    /// button-sized bright blob in the same band), but null when there is none instead of a guessed point - a blind
    /// click there can land in the world. The TELEPORT SCROLL's X was a blob on all 12 saved frames with the list open
    /// (1024x768 to 2048x1152).
    /// </summary>
    /// <param name="reach">How far right of the title to look, in title heights (the phone's SETTINGS X sits ~6.2 away at
    /// 2560x1440, MuMu 2026-10-30).</param>
    /// <param name="minHeights">The smallest blob side, in title heights (0 = the 3 px floor only). The phone's SETTINGS
    /// window has specks on its top border left of the X (a 5x4 one at 1648,189 next to the 26x26 X at 1776,227, title 32
    /// high) - the first live close tapped the speck.</param>
    public PixelRect? FindCloseBlobRightOf(Mat frame, PixelRect title, int reach = 6, double minHeights = 0)
    {
        int h = Math.Max(6, title.Height);
        var band = new PixelRect(title.Right + 2, title.Y - h, reach * h, title.Height + 2 * h).Clamp(frame.Width, frame.Height);
        return ButtonBlobs(frame, band, h)
            .Where(b => b.Width >= minHeights * h && b.Height >= minHeights * h)
            .OrderBy(b => b.X).Select(b => (PixelRect?)b).FirstOrDefault();
    }

    /// <summary>
    /// Auto War: the bright blobs in <paramref name="band"/> of button size (3 px to 3 text heights each way, as
    /// <see cref="FindCloseButtonRightOf"/> takes them).
    /// </summary>
    private IEnumerable<PixelRect> ButtonBlobs(Mat frame, PixelRect band, int h)
    {
        if (band.Width < 4 || band.Height < 4)
            return [];

        return FindBrightBlobs(frame, band)
            .Where(b => b.Width <= 3 * h && b.Height <= 3 * h && b.Width >= 3 && b.Height >= 3);
    }

    private static Mat ToHsv(Mat frame, PixelRect roi)
    {
        using var crop = new Mat(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        if (crop.NumberOfChannels == 4)
            CvInvoke.CvtColor(crop, bgr, ColorConversion.Bgra2Bgr);
        else
            crop.CopyTo(bgr);
        var hsv = new Mat();
        CvInvoke.CvtColor(bgr, hsv, ColorConversion.Bgr2Hsv);
        return hsv;
    }

    /// <param name="minHeight">The lowest blob kept, in pixels (3 by default; the phone's interface-scale dash is 2 px tall).</param>
    public IReadOnlyList<PixelRect> FindBrightBlobs(Mat frame, PixelRect roi, int minHeight = 3)
    {
        var blobs = new List<PixelRect>();
        roi = roi.Clamp(frame.Width, frame.Height);
        if (roi.Width < 4 || roi.Height < 4)
            return blobs;

        using var sub = new Mat(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
        using var gray = new Mat();
        switch (sub.NumberOfChannels)
        {
            case 4:
                CvInvoke.CvtColor(sub, gray, ColorConversion.Bgra2Gray);
                break;
            case 3:
                CvInvoke.CvtColor(sub, gray, ColorConversion.Bgr2Gray);
                break;
            default:
                sub.CopyTo(gray);
                break;
        }

        using var mask = new Mat();
        CvInvoke.Threshold(gray, mask, 0, 255, ThresholdType.Binary | ThresholdType.Otsu);

        foreach (var (box, _) in CvHelpers.ExternalContours(mask))
        {
            if (box.Width < 2 || box.Height < minHeight || box.Width * box.Height < 6)
                continue;

            // The panel border/scrollbar runs the full height of the strip - not a glyph.
            if (box.Height >= roi.Height - 2)
                continue;

            blobs.Add(new PixelRect(roi.X + box.X, roi.Y + box.Y, box.Width, box.Height));
        }

        return blobs;
    }

    /// <summary>
    /// Phone: which way the searchlight points, in degrees clockwise from the way <paramref name="templateName"/> points
    /// (0 = lined up, 90 = a quarter turn clockwise of it). The box round <paramref name="roi"/> is turned back in
    /// <paramref name="stepDeg"/> steps and the straight template matched in each at <paramref name="scale"/> (the scale it
    /// matched at); the turn that scores best is the beam's angle. Turning the picture rather than the template keeps the
    /// minimap's own marker under the beam as it is (a padded, turned template scored it as black - 0.60 for a lined-up
    /// beam, MuMu 1920x1080). Null when nothing could be scored.
    /// </summary>
    public (int AngleCw, double Score)? BeamAngle(Mat frame, string templateName, PixelRect roi, double scale, int stepDeg = 15)
    {
        if (Library.Get(templateName) is not { } source)
            return null;

        // Room round the box for the turned picture: half the template's diagonal at this scale.
        int margin = (int)Math.Ceiling(Math.Sqrt((double)source.Width * source.Width + (double)source.Height * source.Height) * scale / 2);
        var area = PixelRect.FromBounds(roi.X - margin, roi.Y - margin, roi.Right + margin, roi.Bottom + margin).Clamp(frame.Width, frame.Height);
        if (area.Width < 8 || area.Height < 8)
            return null;

        // Grey once (the matcher works in grey anyway): a quarter of the work per turned copy.
        using var gray = TemplateMatcher.ToGray(frame, area);
        int side = (int)Math.Ceiling(Math.Sqrt((double)area.Width * area.Width + (double)area.Height * area.Height)) + 2;
        using var square = new Mat();
        CvInvoke.CopyMakeBorder(gray, square, (side - area.Height) / 2, side - area.Height - (side - area.Height) / 2,
            (side - area.Width) / 2, side - area.Width - (side - area.Width) / 2, BorderType.Constant, new MCvScalar(0));
        var whole = new PixelRect(0, 0, side, side);
        var centre = new PointF(side / 2f, side / 2f);

        int step = Math.Clamp(stepDeg, 5, 45);
        double best = double.MinValue;
        int bestAngle = 0;
        using var rotation = new Mat();
        using var turned = new Mat();
        for (int angle = 0; angle < 360; angle += step)
        {
            // OpenCV turns anticlockwise for a positive angle: a beam that points angle degrees clockwise stands upright.
            CvInvoke.GetRotationMatrix2D(centre, angle, 1.0, rotation);
            CvInvoke.WarpAffine(square, turned, rotation, new Size(side, side), Inter.Linear, Warp.Default,
                BorderType.Constant, new MCvScalar(0));
            var hit = Matcher.FindBest(turned, templateName, whole, scale, 0.0);
            if (hit != null && hit.Score > best)
            {
                best = hit.Score;
                bestAngle = angle;
            }
        }

        return best > double.MinValue ? (bestAngle, best) : null;
    }

    public CompassReading? ReadCompass(Mat frame)
    {
        double scale = ScaleFor(frame.Height);
        var roi = CompassRegion(frame.Width, frame.Height);
        return Compass.Detect(frame, roi, scale, _settings.Detection);
    }

    /// <summary>
    /// Reads the "x , y" position from under the minimap title. OCR is deliberately given a
    /// larger upscale than the rest of the UI because the digits are small and thin.
    /// </summary>
    public async Task<GameCoordinates?> ReadCoordinatesAsync(Mat frame, List<string>? trace = null)
    {
        var key = (frame.Width, frame.Height);
        GameCoordinates? found = null;

        // Fast path: a small box around where the line was found last time.
        if (_minimapAnchor.TryGetValue(key, out var anchor))
        {
            int h = Math.Max(8, anchor.Height);
            var near = new PixelRect(anchor.X - h * 4, anchor.Y - h, anchor.Width + h * 8, anchor.Height + h * 2)
                .Clamp(frame.Width, frame.Height);
            found = await ReadCoordinatePairAsync(frame, near, trace);
        }

        // Search the whole top-right panel band, then the old fixed region.
        found ??= await ReadCoordinatePairAsync(frame, _settings.Regions.Minimap.ToPixels(frame.Width, frame.Height), trace);
        found ??= await ReadCoordinatesInAsync(frame, _settings.Regions.Coordinates.ToPixels(frame.Width, frame.Height));

        if (found != null)
            _minimapAnchor[key] = found.Bounds;
        return found;
    }

    /// <summary>
    /// The box the searchlight is matched in. Once the "x , y" line has been read it is placed
    /// relative to it (from the line down ~20 text heights, ~12 either side); before that,
    /// and when no line was ever read, <see cref="RegionSettings.Compass"/> is used.
    /// </summary>
    public PixelRect CompassRegion(int clientWidth, int clientHeight)
    {
        if (!_minimapAnchor.TryGetValue((clientWidth, clientHeight), out var line))
            return _settings.Regions.CompassRect(clientWidth, clientHeight);

        // Capped: the label is ~9 px at 768 px client height, so anything taller is a bad read.
        int h = Math.Clamp(line.Height, 8, Math.Max(10, clientHeight / 64));
        // The player marker moves around inside the minimap (seen at y 122 and y 160 at 1024x768),
        // and a beam pointing north reaches up to the coordinate line - so the box starts there.
        // Down to the panel's bottom edge: at the labyrinth entrance the marker sits at y ~258.
        return PixelRect.FromBounds(line.CenterX - h * 12, line.Y, line.CenterX + h * 12, line.Bottom + h * 24)
            .Clamp(clientWidth, clientHeight);
    }

    /// <summary>
    /// Finds the "x , y" line inside <paramref name="roi"/>. The label is small, thin and drawn over
    /// the minimap, and plain OCR drops the first number and reads the comma as a dot
    /// (". 16247"). So a normal pass only locates lines with digits; each one is then re-read
    /// widened to the left (where the dropped number is) with a x4 upscale + Otsu threshold,
    /// which reads "3653 , 16247" reliably on real frames.
    /// </summary>
    private async Task<GameCoordinates?> ReadCoordinatePairAsync(Mat frame, PixelRect roi, List<string>? trace = null)
    {
        if (roi.Width < 12 || roi.Height < 12)
            return null;

        var words = await Ocr.RecognizeAsync(frame, roi, Math.Max(2.0, _settings.Detection.OcrScale));
        var candidates = words
            .GroupBy(w => w.LineBounds)
            .Where(line => HasNumber.IsMatch(line.First().LineText))
            .Select(line => line.Key)
            .OrderBy(bounds => bounds.Y)
            .ToList();
        trace?.Add($"roi {roi}: lines {string.Join(" | ", words.Select(w => w.LineText).Distinct())}");

        // The whole box may already be the label (fast path around the last anchor).
        if (candidates.Count == 0 || roi.Height <= 60)
            candidates.Insert(0, roi);

        // The plain read is sometimes the clean one: '3601 . 2149' while every enhanced re-read
        // dropped digits (', 2149', '3-01 . 2149') and the run stopped 300 from the waypoint (run 25).
        // Only a full pair counts here - a plain read that drops the first number has no match.
        foreach (var line in words.GroupBy(w => w.LineBounds))
        {
            string text = line.First().LineText;
            var match = CoordinatePair.Match(text);
            if (!match.Success || line.Key.Height > Math.Max(20, frame.Height / 25) || line.Key.Width > frame.Width / 4)
                continue;

            bool comma = match.Groups[1].Success;
            if (!comma && (match.Groups[3].Value.Length < 4 || match.Groups[4].Value.Length < 4))
                continue;

            trace?.Add($"  plain line '{text}' {line.Key} MATCH");
            int px = int.Parse(match.Groups[comma ? 1 : 3].Value);
            int py = int.Parse(match.Groups[comma ? 2 : 4].Value);
            return new GameCoordinates(px, py, line.Key, text);
        }

        foreach (var bounds in candidates)
        {
            int h = Math.Max(8, bounds.Height);
            var box = PixelRect.FromBounds(bounds.X - h * 10, bounds.Y - h, bounds.Right + h * 3, bounds.Bottom + h)
                .Clamp(frame.Width, frame.Height);
            // Which upscale reads cleanly changes with what is behind the label (the searchlight
            // beam sits right behind it when facing north), so try a few.
            // Otsu first; the plain upscale last: Otsu dropped the 6 of '3604' at every scale
            // ('304. 2012', run 30) and the run gave up 270 short of the boss.
            foreach (var (up, binarize) in new[] { (4, true), (3, true), (5, true), (3, false), (4, false), (6, true) })
            {
                var (text, textBounds) = await OcrEnhancedAsync(frame, box, up, binarize);
                var match = CoordinatePair.Match(text);
                trace?.Add($"  box {box} x{up}{(binarize ? string.Empty : " plain")}: '{text}' {textBounds}{(match.Success ? " MATCH" : string.Empty)}");
                if (!match.Success)
                    continue;

                // The label is one short line (~9 px tall at 768); a taller box means words from
                // elsewhere were merged in (an 878x588 "label" once anchored a full-screen compass box).
                if (textBounds.Height > Math.Max(20, frame.Height / 25) || textBounds.Width > frame.Width / 4)
                    continue;

                bool comma = match.Groups[1].Success;
                int x = int.Parse(match.Groups[comma ? 1 : 3].Value);
                int y = int.Parse(match.Groups[comma ? 2 : 4].Value);
                return new GameCoordinates(x, y, textBounds, text);
            }
        }

        return null;
    }

    /// <summary>OCR of a small box after a cubic upscale by <paramref name="up"/> and an Otsu threshold (light text on a busy background).</summary>
    private async Task<(string Text, PixelRect Bounds)> OcrEnhancedAsync(Mat frame, PixelRect box, int up, bool binarize = true)
    {
        const int Pad = 20;
        using var crop = new Mat(frame, new Rectangle(box.X, box.Y, box.Width, box.Height));
        using var gray = new Mat();
        CvInvoke.CvtColor(crop, gray, ColorConversion.Bgra2Gray);
        using var big = new Mat();
        CvInvoke.Resize(gray, big, new Size(), up, up, Inter.Cubic);
        using var binary = new Mat();
        if (binarize)
            CvInvoke.Threshold(big, binary, 0, 255, ThresholdType.Binary | ThresholdType.Otsu);
        else
            big.CopyTo(binary);
        using var padded = new Mat();
        CvInvoke.CopyMakeBorder(binary, padded, Pad, Pad, Pad, Pad, BorderType.Constant, new MCvScalar(0, 0, 0));
        using var bgra = new Mat();
        CvInvoke.CvtColor(padded, bgra, ColorConversion.Gray2Bgra);

        var words = await Ocr.RecognizeAsync(bgra, new PixelRect(0, 0, bgra.Width, bgra.Height), 1.0);
        if (words.Count == 0)
            return (string.Empty, box);

        // The bounds anchor the compass box, so only the label itself counts - stray words from
        // the title above would inflate the text height (a 1013x720 compass box was seen).
        var label = words.Where(w => w.Text.Any(char.IsDigit) || w.Text.Trim() is "," or ".").ToList();
        if (label.Count == 0)
            label = words.ToList();

        int left = label.Min(w => w.Bounds.X);
        int top = label.Min(w => w.Bounds.Y);
        int right = label.Max(w => w.Bounds.Right);
        int bottom = label.Max(w => w.Bounds.Bottom);
        var bounds = PixelRect.FromBounds(
            box.X + (left - Pad) / up, box.Y + (top - Pad) / up,
            box.X + (right - Pad) / up, box.Y + (bottom - Pad) / up);
        return (string.Join(' ', words.Select(w => w.Text)), bounds);
    }

    /// <summary>Loose reader for a region that only contains the coordinate label.</summary>
    private async Task<GameCoordinates?> ReadCoordinatesInAsync(Mat frame, PixelRect roi)
    {
        if (roi.Width < 12 || roi.Height < 12)
            return null;

        var words = await Ocr.RecognizeAsync(frame, roi, Math.Max(2.0, _settings.Detection.OcrScale));
        if (words.Count == 0)
            return null;

        // Best case: one OCR line like "3595 , 34153".
        foreach (var line in words.GroupBy(w => w.LineText))
        {
            var numbers = ExtractCoordinates(line.Key);
            if (numbers.Count >= 2)
                return new GameCoordinates(numbers[0], numbers[1], line.First().LineBounds, line.Key);
        }

        // Fallback: the engine split the pair over two lines (or dropped the comma).
        var ordered = words.OrderBy(w => w.Bounds.CenterY).ThenBy(w => w.Bounds.X).ToList();
        var all = new List<int>();
        foreach (var word in ordered)
            all.AddRange(ExtractCoordinates(word.Text));

        if (all.Count >= 2)
        {
            var bounds = ordered[0].LineBounds;
            return new GameCoordinates(all[0], all[1], bounds, string.Join(' ', ordered.Select(w => w.Text)));
        }

        return null;
    }

    /// <summary>Pulls 3-7 digit runs out of OCR text, ignoring punctuation and page counters.</summary>
    private static List<int> ExtractCoordinates(string text)
    {
        var found = new List<int>();
        int index = 0;
        while (index < text.Length)
        {
            if (!char.IsDigit(text[index]))
            {
                index++;
                continue;
            }

            int start = index;
            while (index < text.Length && char.IsDigit(text[index]))
                index++;

            int length = index - start;
            if (length is >= 3 and <= 7 && int.TryParse(text.AsSpan(start, length), out int value))
                found.Add(value);
        }

        return found;
    }

    /// <summary>OCR words joined into their lines: one entry per line box, top to bottom.</summary>
    public static IReadOnlyList<(string Text, PixelRect Bounds)> ToLines(IReadOnlyList<OcrWord> words)
        => words.GroupBy(w => w.LineBounds)
            .Select(g => (Text: g.First().LineText, Bounds: g.Key))
            .OrderBy(l => l.Bounds.Y)
            .ThenBy(l => l.Bounds.X)
            .ToList();

    /// <summary>
    /// OCR of <paramref name="roi"/> with only white text kept bright: every pixel becomes the darkest of
    /// its three colour channels, so white numbers stay white while the red HP bar, the blue MP bar and
    /// coloured scenery turn dark. Lines in frame coordinates, top to bottom.
    /// </summary>
    internal async Task<IReadOnlyList<(string Text, PixelRect Bounds)>> OcrWhiteTextAsync(Mat frame, PixelRect roi, double scale)
    {
        using var crop = new Mat(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
        var channels = crop.Split();
        try
        {
            using var darkest = new Mat();
            CvInvoke.Min(channels[0], channels[1], darkest);
            CvInvoke.Min(darkest, channels[2], darkest);
            using var bgra = new Mat();
            CvInvoke.CvtColor(darkest, bgra, ColorConversion.Gray2Bgra);
            var words = await Ocr.RecognizeAsync(bgra, new PixelRect(0, 0, bgra.Width, bgra.Height), scale);
            return ToLines(words)
                .Select(l => (l.Text, new PixelRect(roi.X + l.Bounds.X, roi.Y + l.Bounds.Y, l.Bounds.Width, l.Bounds.Height)))
                .ToList();
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    /// <summary>
    /// Auto War: the player's HP as "cur / max" from <see cref="RegionSettings.PlayerHp"/>: OCR x2 of the
    /// white text only (<see cref="OcrWhiteTextAsync"/>), then plain OCR x2 when that reads none - the white
    /// read blacks out digits that are not white, should the game tint them at low HP or at death (no frame
    /// shows that yet). Saved frames (561): the white read alone reads 508 - every frame with the game's
    /// HUD at the top left - and plain x2 alone 493, dropping a leading digit on three ("1073 / 41073" for
    /// 41073); both together 511 (the fallback only adds desktop captures with the game window offset).
    /// Null when unread.
    /// </summary>
    public async Task<(int Cur, int Max)?> ReadPlayerHpAsync(Mat frame)
    {
        var roi = _settings.Regions.PlayerHp.ToPixels(frame.Width, frame.Height);
        return WarText.ParseHp(await OcrWhiteTextAsync(frame, roi, 2.0))
               ?? WarText.ParseHp(ToLines(await Ocr.RecognizeAsync(frame, roi, 2.0)));
    }

    /// <summary>
    /// Auto War: the target's name in <see cref="RegionSettings.TargetBox"/> (a name only counts with its
    /// "N / M" line under it) and the target bar's treasure chest (monstericon, top centre), each null when
    /// not seen. OCR x1.5, then x2 of the white text only when that names no one: the slash of the numbers
    /// line on the red bar is often dropped. Saved frames (561): 57 named at x1.5, 114 with the second read.
    /// </summary>
    public async Task<(string? Name, TemplateMatch? Chest)> ReadTargetAsync(Mat frame)
    {
        var roi = _settings.Regions.TargetBox.ToPixels(frame.Width, frame.Height);
        string? name = WarText.FindTargetName(ToLines(await Ocr.RecognizeAsync(frame, roi, 1.5)))
                       ?? WarText.FindTargetName(await OcrWhiteTextAsync(frame, roi, 2.0));
        var chest = FindIcon(frame, "monstericon", new NormRect(0.25, 0, 0.50, 0.16).ToPixels(frame.Width, frame.Height), 0.80);
        return (name, chest);
    }

    /// <summary>R - max(G, B) above this = red text (<see cref="RedTextMask"/>): a red name reads 120-180, orange about 60.</summary>
    internal const int RedTextMinExcess = 80;

    /// <summary>
    /// A BGR(A) image as one channel with only red kept bright: 255 where red exceeds both green and blue by more than
    /// <see cref="RedTextMinExcess"/>, else 0. White, grey, black, orange and pale pink all turn dark.
    /// </summary>
    internal static Mat RedTextMask(Mat bgra)
    {
        var channels = bgra.Split();
        try
        {
            using var greenBlue = new Mat();
            CvInvoke.Max(channels[0], channels[1], greenBlue);
            using var excess = new Mat();
            CvInvoke.Subtract(channels[2], greenBlue, excess);    // 8-bit: saturates at 0
            var mask = new Mat();
            CvInvoke.Threshold(excess, mask, RedTextMinExcess, 255, ThresholdType.Binary);
            return mask;
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    /// <summary>
    /// OCR of <paramref name="roi"/> with only red text kept bright (<see cref="RedTextMask"/>): a player's red name, which
    /// the white read blacks out. Lines in frame coordinates, top to bottom.
    /// </summary>
    internal async Task<IReadOnlyList<(string Text, PixelRect Bounds)>> OcrRedTextAsync(Mat frame, PixelRect roi, double scale)
    {
        using var crop = new Mat(frame, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
        using var mask = RedTextMask(crop);
        using var bgra = new Mat();
        CvInvoke.CvtColor(mask, bgra, ColorConversion.Gray2Bgra);
        var words = await Ocr.RecognizeAsync(bgra, new PixelRect(0, 0, bgra.Width, bgra.Height), scale);
        return ToLines(words)
            .Select(l => (l.Text, new PixelRect(roi.X + l.Bounds.X, roi.Y + l.Bounds.Y, l.Bounds.Width, l.Bounds.Height)))
            .ToList();
    }

    /// <summary>
    /// Auto War's fight: what the target box holds (user, 2026-10-28: a player's name is red and the white read blacks it
    /// out). The name as <see cref="ReadTargetAsync"/> finds it - OCR x1.5, then the white text at x2 - and, when neither
    /// names one, the red text at x2 (<see cref="WarText.FindTargetName"/> on the red read alone). The HP bar's numbers
    /// line (<see cref="WarText.TargetHpLine"/>: in the box's centre band, so the pet panel's "100/100" never counts) of any
    /// of these reads is a target on its own, named or not; the chest as in <see cref="ReadTargetAsync"/>.
    /// <paramref name="seenOnly"/> (the attack step, where only "is it still there" matters): stop at the first sign - the
    /// chest, then the first read with a name or the numbers line. <see cref="ReadTargetAsync"/> itself is left as it was
    /// (the remember step needs a real name).
    /// </summary>
    public async Task<WarTargetRead> ReadWarTargetAsync(Mat frame, bool seenOnly = false)
    {
        var roi = _settings.Regions.TargetBox.ToPixels(frame.Width, frame.Height);
        var chest = FindIcon(frame, "monstericon", new NormRect(0.25, 0, 0.50, 0.16).ToPixels(frame.Width, frame.Height), 0.80);
        if (seenOnly && chest != null)
            return new WarTargetRead(null, null, chest, "chest");

        var plain = ToLines(await Ocr.RecognizeAsync(frame, roi, 1.5));
        var hpLine = WarText.TargetHpLine(plain, roi);
        if (WarText.FindTargetName(plain) is { } name)
            return new WarTargetRead(name, hpLine, chest, "x1.5");
        if (seenOnly && hpLine != null)
            return new WarTargetRead(null, hpLine, chest, "numbers");

        var white = await OcrWhiteTextAsync(frame, roi, 2.0);
        hpLine ??= WarText.TargetHpLine(white, roi);
        if (WarText.FindTargetName(white) is { } whiteName)
            return new WarTargetRead(whiteName, hpLine, chest, "white x2");
        if (seenOnly && hpLine != null)
            return new WarTargetRead(null, hpLine, chest, "numbers");

        var red = await OcrRedTextAsync(frame, roi, 2.0);
        hpLine ??= WarText.TargetHpLine(red, roi);
        if (WarText.FindTargetName(red) is { } redName)
            return new WarTargetRead(redName, hpLine, chest, "red x2");

        string via = hpLine != null ? "numbers" : chest != null ? "chest" : "none";
        return new WarTargetRead(null, hpLine, chest, via);
    }

    /// <summary>
    /// Skill bar icons in <see cref="RegionSettings.Hotbar"/>: per template its <see cref="BestLeftmost"/>
    /// hit at <paramref name="minScore"/> or more, null when it is not on the bar. In the order given.
    /// </summary>
    public IReadOnlyList<(string Template, TemplateMatch? Match)> FindSkillBarIcons(Mat frame, IReadOnlyList<string> templates, double minScore)
    {
        var bar = _settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
        return templates
            .Select(template => (template, BestLeftmost(FindIcons(frame, template, bar, minScore, maxResults: 4))))
            .ToList();
    }

    /// <summary>
    /// The best-scoring hit; hits within 0.01 of it are a tie and the leftmost wins (the same icon can sit
    /// in two slots of the skill bar: Shura at 1035 and 1240 on a 1920x1080 bar, both 1.00). Null when none.
    /// </summary>
    internal static TemplateMatch? BestLeftmost(IReadOnlyCollection<TemplateMatch> hits)
    {
        if (hits.Count == 0)
            return null;

        double top = hits.Max(h => h.Score);
        return hits.Where(h => h.Score >= top - 0.01).MinBy(h => h.Bounds.CenterX);
    }

    /// <summary>
    /// Auto War: the countdown of the Tribe Status row <paramref name="rowText"/> ("Bohai Outpost (Adept)" under
    /// NEXT WARS) - its time or "In progress"; null when it cannot tell (never zero). <paramref name="lines"/> is a
    /// whole-frame OCR x1 (<see cref="ToLines"/>), decided by <see cref="WarText.FindWarRow"/> first. Windows OCR
    /// drops every "h:mm:ss" token in such a read (a synthetic "00:04:25" reads as nothing, "00: 04: 25" reads), so
    /// when that decides nothing each candidate row line (<see cref="WarText.WarRowLines"/>: most of the row's letters,
    /// the bare row before a chat notice naming the war) is read again: <see cref="CountdownBand"/> right of it with
    /// a gap after each colon (<see cref="GapAfterColons"/>), OCR x2. The user's Tribe Status screenshot (795x829):
    /// x1, x1.5 and x2 read no time, the gapped band 00:04:25.
    /// </summary>
    public async Task<WarCountdown?> ReadWarCountdownAsync(Mat frame, IReadOnlyList<(string Text, PixelRect Bounds)> lines, string rowText, List<string>? trace = null)
    {
        var row = WarText.FindWarRow(lines, rowText);
        if (row != null)
        {
            trace?.Add("decided by the whole-frame read");
            return row;
        }

        var candidates = WarText.WarRowLines(lines, rowText);
        if (candidates.Count == 0)
            trace?.Add($"no line carries most of '{rowText}'");

        foreach (var line in candidates)
        {
            int h = Math.Max(4, line.Bounds.Height);
            var band = CountdownBand(line.Bounds, frame.Width, frame.Height);
            using var spaced = GapAfterColons(frame, band, h, out int colons);
            var bandLines = ToLines(await Ocr.RecognizeAsync(spaced, new PixelRect(0, 0, spaced.Width, spaced.Height), 2.0))
                .Select(l => (l.Text, new PixelRect(band.X + l.Bounds.X, band.Y + l.Bounds.Y, l.Bounds.Width, l.Bounds.Height)))
                .ToList();
            var decided = WarText.FindWarRow([line, .. bandLines], rowText);
            trace?.Add($"row '{line.Text}' {line.Bounds}: {colons} colon(s), band read '{string.Join(" | ", bandLines.Select(l => l.Text))}'");
            if (decided != null)
                return decided;
        }

        return null;
    }

    /// <summary>
    /// Where the war row's time is read: right of the row line, half a line height above and below, at most 20
    /// line heights long (the screenshot's time ends 10.7 heights past "Bohai Outpost (Adept)"; the rest of the
    /// frame is other windows or the world), clamped to the frame.
    /// </summary>
    internal static PixelRect CountdownBand(PixelRect row, int frameWidth, int frameHeight)
    {
        int h = Math.Max(4, row.Height);
        return PixelRect.FromBounds(row.Right, row.Y - h / 2, Math.Min(frameWidth, row.Right + 20 * h), row.Bottom + h / 2)
            .Clamp(frameWidth, frameHeight);
    }

    /// <summary>
    /// A copy of <paramref name="band"/> with a blank gap (half the text height, at least 4 px, in the band's
    /// background colour) after each colon (<see cref="ColonCuts"/> over the Otsu-bright blobs);
    /// <paramref name="textHeight"/> = the row's OCR line height.
    /// </summary>
    private static Mat GapAfterColons(Mat frame, PixelRect band, int textHeight, out int colons)
    {
        using var crop = new Mat(frame, new Rectangle(band.X, band.Y, band.Width, band.Height));
        using var gray = new Mat();
        CvInvoke.CvtColor(crop, gray, crop.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);
        using var bright = new Mat();
        CvInvoke.Threshold(gray, bright, 0, 255, ThresholdType.Binary | ThresholdType.Otsu);

        // connectedComponentsWithStats with its defaults (8-connectivity, 32-bit labels), as Components runs it.
        var blobs = Components(bright)
            .Select(c => new PixelRect(c.Box.X, c.Box.Y, c.Box.Width, c.Box.Height))
            .ToList();
        var cuts = ColonCuts(blobs, textHeight);

        colons = cuts.Count;
        int gap = Math.Max(4, textHeight / 2);
        using var dark = new Mat();
        CvInvoke.BitwiseNot(bright, dark);
        var background = CvInvoke.Mean(crop, dark);
        // cv::Mat(rows, cols, type, scalar): allocated by OpenCV, every pixel set to the scalar.
        var result = new Mat(crop.Rows, crop.Cols + gap * cuts.Count, crop.Depth, crop.NumberOfChannels);
        result.SetTo(background);
        int from = 0, to = 0;
        foreach (int cut in cuts.Append(crop.Cols))
        {
            int end = Math.Min(crop.Cols, cut);
            if (end > from)
            {
                using var source = new Mat(crop, new Rectangle(from, 0, end - from, crop.Rows));
                using var target = new Mat(result, new Rectangle(to, 0, end - from, crop.Rows));
                source.CopyTo(target);
            }

            to += end - from + gap;
            from = end;
        }

        return result;
    }

    /// <summary>
    /// The colons among the bright blobs of one text line (band coordinates): two dots (at most a quarter of
    /// <paramref name="textHeight"/> each way) stacked in one column, with a glyph (half to 1.5 text heights tall,
    /// spanning both dots; the window border is taller) within a text height on either side. Per colon the first
    /// column right of it, sorted. A full stop, "..", or a colon at the end of a line is none.
    /// </summary>
    internal static IReadOnlyList<int> ColonCuts(IReadOnlyList<PixelRect> blobs, int textHeight)
    {
        var dots = blobs.Where(b => b.Height <= textHeight / 4.0 && b.Width <= textHeight / 4.0).ToList();
        var glyphs = blobs.Where(b => b.Height >= textHeight / 2.0 && b.Height <= textHeight * 1.5).ToList();
        var cuts = new List<int>();
        foreach (var top in dots)
        {
            foreach (var bottom in dots)
            {
                bool stacked = bottom.Y >= top.Bottom && bottom.X < top.Right && bottom.Right > top.X;
                bool inLine = glyphs.Any(g => g.Right <= top.X && top.X - g.Right <= textHeight && g.Y <= top.Y && g.Bottom >= bottom.Bottom)
                              && glyphs.Any(g => g.X >= top.Right && g.X - top.Right <= textHeight && g.Y <= top.Y && g.Bottom >= bottom.Bottom);
                int cut = Math.Max(top.Right, bottom.Right);
                if (stacked && inLine && !cuts.Contains(cut))
                    cuts.Add(cut);
            }
        }

        cuts.Sort();
        return cuts;
    }

    /// <summary>
    /// Auto War: what closes the windows the war timer opened - the TRIBE STATUS window first, else the MENU:
    /// its title in capitals (<see cref="WarText.FindWindowTitle"/>; the Menu's mixed-case "Tribe Status" tile is
    /// not one) and the X in the window's top right corner, far further from the title than a dialog's: the
    /// button-sized bright blob in <see cref="WarCloseBand"/> picked by <see cref="PickWarClose"/>. No such blob =
    /// <see cref="WarWindow.Close"/> null: no blind click (with the MENU grid scrolled under the title, a point on
    /// the title's row can be a tile, which opens another window). Null when neither title shows.
    /// Measured on the user's screenshots (title box, X, window's right edge; title widths right of the title's
    /// centre for the X's centre / the X's right side / the edge):
    /// TRIBE STATUS 322-467 x 8-24 (16 px), X 753-767 x 12-26, edge 795 - 2.52 / 2.57 / 2.76; a corner ornament
    /// (775-785 x 0-13) sits between the X and the edge. MENU (faint glyphs - the title does not read there, the grid
    /// is scrolled under it) 171-226 x 22-35 (13 px), X 352-366 x 26-39, edge ~380 - 2.93 / 3.05 / 3.3; the tile
    /// label "Inventory (I)" (279-361 x 28-41) shares the title's rows, left of the X.
    /// </summary>
    public WarWindow? FindWarWindowClose(Mat frame, IReadOnlyList<(string Text, PixelRect Bounds)> lines)
    {
        foreach (var (title, reach, edge, x) in new[] { (WarText.TribeStatusTitle, 22, 2.7, 2.52), (WarText.MenuTitle, 12, 3.2, 2.93) })
        {
            if (WarText.FindWindowTitle(lines, title) is not { } line)
                continue;

            var band = WarCloseBand(line.Bounds, reach, edge, frame.Width, frame.Height);
            var buttons = ButtonBlobs(frame, band, Math.Max(6, line.Bounds.Height));
            return new WarWindow(title, line.Text, line.Bounds, PickWarClose(buttons, line.Bounds, x));
        }

        return null;
    }

    /// <summary>
    /// The war window's X among the button-sized blobs right of its title: the one whose centre is nearest
    /// <paramref name="xWidths"/> title widths right of the title's centre, and no more than one title height from
    /// that point - neither the leftmost (the scrolled MENU grid's labels lie left of the X) nor the rightmost
    /// (TRIBE STATUS's corner ornament lies right of it). Null when none is that close.
    /// </summary>
    internal static PixelRect? PickWarClose(IEnumerable<PixelRect> buttons, PixelRect title, double xWidths)
    {
        int h = Math.Max(6, title.Height);
        double expected = title.CenterX + xWidths * title.Width;
        return buttons
            .Where(b => Math.Abs(b.CenterX - expected) <= h)
            .OrderBy(b => Math.Abs(b.CenterX - expected))
            .Select(b => (PixelRect?)b)
            .FirstOrDefault();
    }

    /// <summary>
    /// Where a war window's X is looked for: the title's rows (half a title height above and below, so the rows
    /// under the title stay out), from just right of the title to the nearer of <paramref name="reachHeights"/>
    /// title heights and <paramref name="edgeWidths"/> title widths right of the title's centre. The width bound
    /// stops short of the window's right edge (measured in <see cref="FindWarWindowClose"/>), so nothing in the world
    /// beyond it (a click there walks the character) is ever taken for the X; it holds while OCR's title box is
    /// within a few pixels of the glyphs. The height bound keeps a mis-sized title box from reaching far. Not
    /// everything is kept out: with the MENU grid scrolled under its title (the user's screenshot) the tile labels
    /// share the title's rows - "Inventory (I)" at 279-361 x 28-41 lies inside the band, left of the X - and
    /// TRIBE STATUS's corner ornament lies right of its X; <see cref="PickWarClose"/> chooses among them.
    /// </summary>
    internal static PixelRect WarCloseBand(PixelRect title, int reachHeights, double edgeWidths, int frameWidth, int frameHeight)
    {
        int h = Math.Max(6, title.Height);
        int left = title.Right + 2;
        int right = Math.Min(left + reachHeights * h, title.CenterX + (int)(edgeWidths * title.Width));
        return PixelRect.FromBounds(left, title.Y - h / 2, right, title.Bottom + h / 2).Clamp(frameWidth, frameHeight);
    }

    /// <summary>Manual "what do the detectors see" pass used by the Test scan button.</summary>
    public async Task<ScanReport> TestScanAsync(Mat frame)
    {
        var report = new ScanReport();
        double scale = ScaleFor(frame.Height);
        var full = new PixelRect(0, 0, frame.Width, frame.Height);

        report.Details.Add($"frame {frame.Width}x{frame.Height}, template scale x{scale:0.000}, OCR scale x{_settings.Detection.OcrScale:0.00}");

        foreach (string name in Library.Names)
        {
            var stopwatch = Stopwatch.StartNew();
            var matches = Matcher.FindMultiScale(
                frame,
                name,
                full,
                scale,
                Math.Max(0.45, _settings.Detection.TemplateThreshold - 0.25),
                minScaleFactor: MinScaleFactor,
                maxScaleFactor: MaxScaleFactor,
                scaleStep: ScaleStep,
                maxResults: 3);
            stopwatch.Stop();

            if (matches.Count == 0)
            {
                report.Details.Add($"{name}: no match (>= 0.45) in {stopwatch.Elapsed.TotalMilliseconds:0.0}ms");
                continue;
            }

            foreach (var match in matches)
            {
                bool passes = match.Score >= _settings.Detection.TemplateThreshold;
                report.Overlays.Add(new OverlayItem(
                    match.Bounds,
                    $"{name} {match.Score:0.00}",
                    passes ? OverlayColor.Green : OverlayColor.Orange));
                report.Details.Add($"{name}: {match} {(passes ? "PASS" : "below threshold")}");
            }

            report.Details.Add($"{name}: {matches.Count} hit(s) in {stopwatch.Elapsed.TotalMilliseconds:0.0}ms");
        }

        var coords = await ReadCoordinatesAsync(frame);
        report.Details.Add(coords == null ? "coordinates: not read" : $"coordinates: {coords} at {coords.Bounds} (raw '{coords.Raw}')");
        if (coords != null)
            report.Overlays.Add(new OverlayItem(coords.Bounds, $"{coords}", OverlayColor.Green));

        var compassRoi = CompassRegion(frame.Width, frame.Height);
        var (templateReading, brightnessReading) = Compass.DetectBoth(frame, compassRoi, scale, _settings.Detection);
        if (templateReading != null)
            report.Details.Add($"compass/template: {templateReading}");
        if (brightnessReading != null)
            report.Details.Add($"compass/brightness: {brightnessReading}");

        var compassReading = _settings.Detection.CompassMode switch
        {
            CompassMode.Template => templateReading,
            CompassMode.Brightness => brightnessReading,
            _ => templateReading ?? brightnessReading,
        };

        report.Overlays.Add(new OverlayItem(compassRoi, "compass ROI", OverlayColor.Blue));
        if (compassReading != null)
        {
            report.Overlays.Add(new OverlayItem(compassRoi, $"facing {compassReading.AngleFromNorth:0.0}deg", OverlayColor.Yellow));
            report.Details.Add($"compass: facing {compassReading.AngleFromNorth:0.0}deg ({compassReading.Method})");
        }
        else
        {
            report.Details.Add("compass: no reading");
        }

        var ocrRoi = _settings.Regions.Ocr.ToPixels(frame.Width, frame.Height);
        report.Overlays.Add(new OverlayItem(ocrRoi, "ocr ROI", OverlayColor.Blue));
        var words = await Ocr.RecognizeAsync(frame, ocrRoi, _settings.Detection.OcrScale);
        report.Details.Add($"ocr: {words.Count} word(s) in {Ocr.LastDurationMs:0}ms - {Ocr.Status}");
        foreach (var line in words.GroupBy(w => w.LineText))
            report.Details.Add($"ocr line: '{line.Key}' at {line.First().LineBounds}");

        var desired = _settings.Sequence.DestinationText;
        var hit = TextMatcher.FindBest(words, desired, _settings.Detection.OcrMinSimilarity);
        if (hit != null)
        {
            report.Overlays.Add(new OverlayItem(hit.Bounds, $"'{desired}' {hit.Similarity:0.00}", OverlayColor.Green));
            report.Details.Add($"ocr match: {hit}");
        }
        else
        {
            report.Details.Add($"ocr match: '{desired}' not found");
        }

        return report;
    }

    public void Dispose()
    {
        Compass.Dispose();
        Matcher.Dispose();
        Library.Dispose();
        (Ocr as IDisposable)?.Dispose();
    }
}
