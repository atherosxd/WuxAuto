using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenCvSharp;
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
    /// </summary>
    public async Task<SettingRow?> ReadSettingRowAsync(Mat frame, string label, bool numeric = true)
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
        var blobs = FindBrightBlobs(frame, band)
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
        Cv2.CvtColor(frame, bgr, ColorConversionCodes.BGRA2BGR);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        // Measured on the LOG IN button: H 15-18, S 128-143, V 103-232. The sunset behind the login
        // panel has the same hue but S ~85, so saturation is what separates them.
        Cv2.InRange(hsv, new Scalar(12, 110, 95), new Scalar(25, 255, 255), mask);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var buttons = new List<PixelRect>();
        foreach (var contour in contours)
        {
            var box = Cv2.BoundingRect(contour);
            double aspect = box.Width / (double)Math.Max(1, box.Height);
            double fill = Cv2.ContourArea(contour) / Math.Max(1.0, box.Width * box.Height);
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
        using var crop = new Mat(frame, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        Cv2.CvtColor(crop, bgr, ColorConversionCodes.BGRA2BGR);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var low = new Mat();
        using var high = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 120, 120), new Scalar(22, 255, 255), low);
        Cv2.InRange(hsv, new Scalar(168, 120, 120), new Scalar(180, 255, 255), high);
        using var mask = new Mat();
        Cv2.BitwiseOr(low, high, mask);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var best = contours
            .Select(c => (Box: Cv2.BoundingRect(c), Area: Cv2.ContourArea(c)))
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
    /// leave the frame.
    /// </summary>
    public double RingFraction(Mat frame, PixelRect icon)
    {
        double cx = icon.X + icon.Width / 2.0;
        double cy = icon.Y + icon.Height / 2.0;
        double rMin = icon.Width * 0.68;
        double rMax = icon.Width * 0.85;
        int margin = (int)Math.Ceiling(rMax) + 1;
        var roi = new PixelRect((int)cx - margin, (int)cy - margin, margin * 2 + 1, margin * 2 + 1);
        if (roi.X < 0 || roi.Y < 0 || roi.Right > frame.Width || roi.Bottom > frame.Height || rMin < 2)
            return 0;

        using var crop = new Mat(frame, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        if (crop.Channels() == 4)
            Cv2.CvtColor(crop, bgr, ColorConversionCodes.BGRA2BGR);
        else
            crop.CopyTo(bgr);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var iconRect = new Rect(icon.X - roi.X, icon.Y - roi.Y, icon.Width, icon.Height);
        using var iconHsv = new Mat(hsv, iconRect);
        using var value = iconHsv.ExtractChannel(2);
        Cv2.MinMaxLoc(value, out _, out double maxValue);
        double minValue = Math.Max(40, 150 * maxValue / 255);

        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(15, 60, minValue), new Scalar(30, 150, 240), mask);

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
                hit = x >= 0 && y >= 0 && x < mask.Width && y < mask.Height && mask.At<byte>(y, x) != 0;
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

        using var crop = new Mat(frame, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        if (crop.Channels() == 4)
            Cv2.CvtColor(crop, bgr, ColorConversionCodes.BGRA2BGR);
        else
            crop.CopyTo(bgr);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(138, 89, 190), new Scalar(156, 255, 255), mask);

        int count = Cv2.CountNonZero(mask);
        if (count < frame.Width * (double)frame.Height * minFraction)
            return null;

        var box = Cv2.BoundingRect(mask);
        return new PixelRect(roi.X + box.X, roi.Y + box.Y, box.Width, box.Height);
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
        Cv2.InRange(hsv, new Scalar(135, 35, 170), new Scalar(162, 255, 255), mask);
        int k = Math.Max(3, frame.Height / 90) | 1;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        double minPixels = frame.Width * (double)frame.Height * 1.5e-4;
        var parts = Components(mask).Where(c => c.Pixels >= minPixels / 6).ToList();
        var seed = parts
            .Where(c => c.Pixels >= minPixels && c.Box.Width >= frame.Width * 0.04 && c.Box.Width >= c.Box.Height)
            .OrderByDescending(c => c.Pixels)
            .Select(c => (Rect?)c.Box)
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

                var union = ring | box;
                parts.RemoveAt(i);
                if (union == ring)
                    continue;

                ring = union;
                grew = true;
            }
        }

        return new PixelRect(roi.X + ring.X, roi.Y + ring.Y, ring.Width, ring.Height);

        static Rect Inflate(Rect r, int by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
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
        Cv2.InRange(hsv, new Scalar(80, 120, 170), new Scalar(100, 255, 255), mask);

        double unit = frame.Height / 1080.0;
        var blobs = Components(mask)
            .Where(c => c.Pixels >= 40 * unit * unit && c.Box.Width <= frame.Height * 0.08)
            .Select(c => c.Box)
            .ToList();

        Rect? found = blobs
            .Where(b => b.Height >= frame.Height * 0.10 && b.Height >= 3 * b.Width)
            .OrderByDescending(b => b.Height)
            .Select(b => (Rect?)b)
            .FirstOrDefault();

        // A chain: from each patch downwards, the next patch in the same column within a gap.
        double column = frame.Height * 0.03, gap = frame.Height * 0.08;
        foreach (var top in blobs.OrderBy(b => b.Y))
        {
            var chain = new List<Rect> { top };
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
            var box = new Rect(left, top.Y, right - left, last.Bottom - top.Y);
            if (found is not { } f || box.Height > f.Height)
                found = box;
        }

        return found is { } belt ? new PixelRect(roi.X + belt.X, roi.Y + belt.Y, belt.Width, belt.Height) : null;

        static double CenterX(Rect r) => r.X + r.Width / 2.0;
    }

    /// <summary>The game world between the HUD panels, where the ring and boss detectors look.</summary>
    private static PixelRect WorldArea(Mat frame) => new PixelRect((int)(frame.Width * 0.05), (int)(frame.Height * 0.12),
        (int)(frame.Width * 0.70), (int)(frame.Height * 0.78)).Clamp(frame.Width, frame.Height);

    /// <summary>Connected patches of a mask: bounding box and pixel count.</summary>
    private static List<(Rect Box, int Pixels)> Components(Mat mask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var list = new List<(Rect, int)>(Math.Max(0, count - 1));
        for (int i = 1; i < count; i++)
        {
            var box = new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            list.Add((box, stats.At<int>(i, 4)));
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
        Cv2.InRange(hsv, new Scalar(10, 70, 110), new Scalar(32, 255, 255), mask);
        if (Cv2.CountNonZero(mask) < h)
            return fallback;

        // The rows sit ~2 px apart, so the gold frames of the boxes above and below touch this one
        // and merge into one tall outline. Its width is still the box size: the box is that square,
        // centred on the label's row.
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        int labelCy = label.CenterY - band.Y;
        var best = contours.Select(Cv2.BoundingRect)
            .Where(r => r.Width >= h * 0.6 && r.Width <= h * 3 && r.Y <= labelCy && r.Bottom >= labelCy)
            .OrderByDescending(r => r.Right)
            .Select(r => (Rect?)r)
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

        using var crop = new Mat(frame, new Rect(inner.X, inner.Y, inner.Width, inner.Height));
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, crop.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        Cv2.MinMaxLoc(gray, out _, out double max);
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

    private static Mat ToHsv(Mat frame, PixelRect roi)
    {
        using var crop = new Mat(frame, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var bgr = new Mat();
        if (crop.Channels() == 4)
            Cv2.CvtColor(crop, bgr, ColorConversionCodes.BGRA2BGR);
        else
            crop.CopyTo(bgr);
        var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        return hsv;
    }

    public IReadOnlyList<PixelRect> FindBrightBlobs(Mat frame, PixelRect roi)
    {
        var blobs = new List<PixelRect>();
        roi = roi.Clamp(frame.Width, frame.Height);
        if (roi.Width < 4 || roi.Height < 4)
            return blobs;

        using var sub = new Mat(frame, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var gray = new Mat();
        switch (sub.Channels())
        {
            case 4:
                Cv2.CvtColor(sub, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            case 3:
                Cv2.CvtColor(sub, gray, ColorConversionCodes.BGR2GRAY);
                break;
            default:
                sub.CopyTo(gray);
                break;
        }

        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        using var hierarchy = new Mat();
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        foreach (var contour in contours)
        {
            var box = Cv2.BoundingRect(contour);
            if (box.Width < 2 || box.Height < 3 || box.Width * box.Height < 6)
                continue;

            // The panel border/scrollbar runs the full height of the strip - not a glyph.
            if (box.Height >= roi.Height - 2)
                continue;

            blobs.Add(new PixelRect(roi.X + box.X, roi.Y + box.Y, box.Width, box.Height));
        }

        return blobs;
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
        using var crop = new Mat(frame, new Rect(box.X, box.Y, box.Width, box.Height));
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        using var big = new Mat();
        Cv2.Resize(gray, big, new Size(), up, up, InterpolationFlags.Cubic);
        using var binary = new Mat();
        if (binarize)
            Cv2.Threshold(big, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        else
            big.CopyTo(binary);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(binary, padded, Pad, Pad, Pad, Pad, BorderTypes.Constant, Scalar.Black);
        using var bgra = new Mat();
        Cv2.CvtColor(padded, bgra, ColorConversionCodes.GRAY2BGRA);

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
        using var crop = new Mat(frame, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        var channels = Cv2.Split(crop);
        try
        {
            using var darkest = new Mat();
            Cv2.Min(channels[0], channels[1], darkest);
            Cv2.Min(darkest, channels[2], darkest);
            using var bgra = new Mat();
            Cv2.CvtColor(darkest, bgra, ColorConversionCodes.GRAY2BGRA);
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
