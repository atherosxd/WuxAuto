// FROZEN COPY for the Emgu parity probe: the OpenCvSharp vision code main added at 28763e1 (Auto War,
// LOD ring distance) - ScreenAnalyzer's new members, verbatim apart from the namespace and usings. Do not edit.
using OpenCvSharp;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Legacy;

public sealed partial class ScreenAnalyzer
{
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
    /// Auto War: the X right of a window title found the way <see cref="FindCloseButtonRightOf"/> finds it (the first
    /// button-sized bright blob in the same band), but null when there is none instead of a guessed point - a blind
    /// click there can land in the world. The TELEPORT SCROLL's X was a blob on all 12 saved frames with the list open
    /// (1024x768 to 2048x1152).
    /// </summary>
    public PixelRect? FindCloseBlobRightOf(Mat frame, PixelRect title)
    {
        int h = Math.Max(6, title.Height);
        var band = new PixelRect(title.Right + 2, title.Y - h, 6 * h, title.Height + 2 * h).Clamp(frame.Width, frame.Height);
        return ButtonBlobs(frame, band, h).OrderBy(b => b.X).Select(b => (PixelRect?)b).FirstOrDefault();
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
        using var crop = new Mat(frame, new Rect(band.X, band.Y, band.Width, band.Height));
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, crop.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var bright = new Mat();
        Cv2.Threshold(gray, bright, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(bright, labels, stats, centroids);

        var blobs = Enumerable.Range(1, Math.Max(0, count - 1))
            .Select(i => new PixelRect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3)))
            .ToList();
        var cuts = ColonCuts(blobs, textHeight);

        colons = cuts.Count;
        int gap = Math.Max(4, textHeight / 2);
        using var dark = new Mat();
        Cv2.BitwiseNot(bright, dark);
        var background = Cv2.Mean(crop, dark);
        var result = new Mat(crop.Rows, crop.Cols + gap * cuts.Count, crop.Type(), background);
        int from = 0, to = 0;
        foreach (int cut in cuts.Append(crop.Cols))
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
}
