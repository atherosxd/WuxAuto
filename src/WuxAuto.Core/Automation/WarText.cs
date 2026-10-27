using System.Text.RegularExpressions;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Automation;

/// <summary>What the Tribe Status window says about one war: the time left, or that it is in progress.</summary>
public readonly record struct WarCountdown(TimeSpan? Remaining, bool InProgress);

/// <summary>
/// Pure text rules of the Auto War mode (OCR-tolerant; no screen, no input). Each member is the
/// reading of one piece of game text.
/// </summary>
public static class WarText
{
    static readonly Regex TimeRegex = new(@"(\d{1,2}):(\d{2}):(\d{2})", RegexOptions.Compiled);
    static readonly Regex ColonSpaces = new(@"\s*:\s*", RegexOptions.Compiled);
    static readonly Regex TimeRun = new(@"[0-9Oo:lI|]+", RegexOptions.Compiled);

    /// <summary>
    /// The first <c>h:mm:ss</c> in an OCR read of the Tribe Status row's time ("00:04:25").
    /// Spaces round the colons are dropped, and O/o read as 0 and l/I/| as 1 inside a run of
    /// digit-like characters. Minutes or seconds of 60 or more are not a time. Null when none.
    /// </summary>
    public static TimeSpan? ParseCountdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string compact = ColonSpaces.Replace(text, ":");
        foreach (Match run in TimeRun.Matches(compact))
        {
            string fixedRun = run.Value.Replace('O', '0').Replace('o', '0')
                .Replace('l', '1').Replace('I', '1').Replace('|', '1');
            foreach (Match m in TimeRegex.Matches(fixedRun))
            {
                int h = int.Parse(m.Groups[1].Value);
                int min = int.Parse(m.Groups[2].Value);
                int s = int.Parse(m.Groups[3].Value);
                if (min < 60 && s < 60)
                    return new TimeSpan(h, min, s);
            }
        }

        return null;
    }

    /// <summary>The "In progress" word the Tribe Status row shows instead of a time while the war runs.</summary>
    public static bool IsInProgress(string text)
        => TextMatcher.Similarity(text, "In progress") >= 0.80
           || TextMatcher.Normalize(text).Contains("inprogress", StringComparison.Ordinal);

    /// <summary>
    /// The Tribe Status row of one war (e.g. "Bohai Outpost (Adept)"): its time may be merged into the
    /// row's own OCR line or be a separate line on the same row, right of the name. Null when the row
    /// is not on screen or shows neither a time nor "In progress" (unknown, never zero).
    /// </summary>
    public static WarCountdown? FindWarRow(IReadOnlyList<(string Text, PixelRect Bounds)> lines, string rowText)
    {
        string wanted = TextMatcher.Normalize(rowText);
        if (wanted.Length == 0)
            return null;

        // Every row-like line is tried in order: the "[Bohai Outpost (Adept)] [10] minute(s) to opening"
        // notice matches the row text too but carries no time, and must not hide the real row.
        for (int rowIndex = 0; rowIndex < lines.Count; rowIndex++)
        {
            string normalized = TextMatcher.Normalize(lines[rowIndex].Text);
            if (!normalized.Contains(wanted, StringComparison.Ordinal)
                && TextMatcher.Similarity(lines[rowIndex].Text, rowText) < 0.80)
                continue;

            var (rowLine, rowBounds) = lines[rowIndex];
            var merged = Decide(rowLine);
            if (merged is not null)
                return merged;

            int self = rowIndex;
            var sameRow = lines
                .Where((l, i) => i != self
                    && Math.Abs(l.Bounds.CenterY - rowBounds.CenterY) <= rowBounds.Height
                    && l.Bounds.X >= rowBounds.Right)
                .OrderBy(l => l.Bounds.X);
            foreach (var (text, _) in sameRow)
            {
                var decided = Decide(text);
                if (decided is not null)
                    return decided;
            }
        }

        return null;
    }

    static WarCountdown? Decide(string text)
    {
        var time = ParseCountdown(text);
        if (time is not null)
            return new WarCountdown(time, false);
        return IsInProgress(text) ? new WarCountdown(null, true) : null;
    }

    /// <summary>
    /// The war's "[Bohai Outpost (Adept)] Confirm to move?" dialog line: the war keyword and "confirm".
    /// The "minute(s) to opening" notice and other "Confirm to use?" dialogs do not count.
    /// </summary>
    public static bool IsWarDialogLine(string line, string warKeyword)
    {
        string n = TextMatcher.Normalize(line);
        string k = TextMatcher.Normalize(warKeyword);
        return k.Length > 0 && n.Contains(k, StringComparison.Ordinal) && n.Contains("confirm", StringComparison.Ordinal);
    }

    /// <summary>
    /// The teleport-list entry for the zone named by the (cut-off) minimap title, never a guess between caves:
    /// (1) a title ending in ']' is complete, and the entry with the same normalised text wins;
    /// (2) otherwise the entries the title is a full prefix of: exactly one wins, two or more are
    /// <paramref name="ambiguous"/> (the shortest is returned);
    /// (3) otherwise the longest common prefix, scored against the longer of the two (prefix at least
    /// min(10, title length), score at least 0.75), ambiguous when a second entry scores within 0.05.
    /// Null when nothing is close.
    /// </summary>
    public static string? BestListEntry(string minimapTitle, IReadOnlyList<string> entries, out bool ambiguous)
    {
        ambiguous = false;
        string t = TextMatcher.Normalize(minimapTitle);
        if (t.Length == 0)
            return null;

        if (minimapTitle.TrimEnd().EndsWith(']'))
        {
            foreach (string entry in entries)
            {
                if (TextMatcher.Normalize(entry) == t)
                    return entry;
            }
        }

        var prefixed = entries
            .Where(entry => TextMatcher.Normalize(entry).StartsWith(t, StringComparison.Ordinal))
            .ToList();
        if (prefixed.Count == 1)
            return prefixed[0];
        if (prefixed.Count > 1)
        {
            ambiguous = true;
            return prefixed.OrderBy(entry => TextMatcher.Normalize(entry).Length).First();
        }

        string? best = null;
        double bestScore = 0;
        double second = 0;
        foreach (string entry in entries)
        {
            string e = TextMatcher.Normalize(entry);
            int p = 0;
            while (p < t.Length && p < e.Length && t[p] == e[p])
                p++;

            if (p < Math.Min(10, t.Length))
                continue;

            double score = p / (double)Math.Max(t.Length, e.Length);
            if (score < 0.75)
                continue;

            if (score > bestScore)
            {
                second = bestScore;
                bestScore = score;
                best = entry;
            }
            else if (score > second)
            {
                second = score;
            }
        }

        ambiguous = best is not null && second > 0 && bestScore - second <= 0.05;
        return best;
    }

    static readonly Regex HpOcrO = new(@"[Oo](?=[0-9/Oo])|(?<=[0-9/])[Oo]", RegexOptions.Compiled);
    static readonly Regex HpPair = new(@"^\D{0,2}(\d{1,7})/(\d{2,7})\D{0,2}$", RegexOptions.Compiled);
    static readonly Regex NumbersLine = new(@"\d+\s*/\s*\d+", RegexOptions.Compiled);

    /// <summary>
    /// The player's HP read as "cur / max" from the OCR lines: the topmost pair (the HP bar sits above
    /// the MP bar and the "0/3000" bar), max at least 100 and cur not above it. Null when none.
    /// </summary>
    public static (int Cur, int Max)? ParseHp(IReadOnlyList<(string Text, PixelRect Bounds)> lines)
    {
        (int Cur, int Max)? best = null;
        int bestY = int.MaxValue;
        foreach (var (text, bounds) in lines)
        {
            string compact = HpOcrO.Replace(text.Replace(" ", ""), "0");
            var m = HpPair.Match(compact);
            if (!m.Success)
                continue;

            int cur = int.Parse(m.Groups[1].Value);
            int max = int.Parse(m.Groups[2].Value);
            if (max < 100 || cur > max || bounds.Y >= bestY)
                continue;

            best = (cur, max);
            bestY = bounds.Y;
        }

        return best;
    }

    /// <summary>
    /// An HP read that means "dead": 0 out of the player's own maximum. The MP bar and the "0/3000"
    /// bar also read 0 / N, so the maximum must match the one seen alive (within 1 %), or, with none
    /// seen yet, be at least 1000.
    /// </summary>
    public static bool IsDeadRead((int Cur, int Max) hp, int? aliveMax)
    {
        if (hp.Cur != 0)
            return false;
        return aliveMax is int alive
            ? Math.Abs(hp.Max - alive) <= alive / 100
            : hp.Max >= 1000;
    }

    /// <summary>
    /// The enemy named in the target box: a line of 3+ letters (no ':' and not a sentence ending in '.')
    /// with an "N / M" numbers line right under it (within 3 name heights) that overlaps it
    /// horizontally. The version/ping line, system notices and bare numbers never qualify.
    /// Top to bottom, the first such name, trimmed. Null when none.
    /// </summary>
    public static string? FindTargetName(IReadOnlyList<(string Text, PixelRect Bounds)> lines)
    {
        foreach (var (text, bounds) in lines.OrderBy(l => l.Bounds.Y))
        {
            string trimmed = text.Trim();
            if (trimmed.Contains(':') || trimmed.EndsWith('.'))
                continue;
            if (TextMatcher.Normalize(trimmed).Count(char.IsLetter) < 3)
                continue;

            bool hasNumbers = lines.Any(n =>
                NumbersLine.IsMatch(n.Text)
                && n.Bounds.Y > bounds.Y
                && n.Bounds.Y - bounds.Bottom <= 3 * bounds.Height
                && n.Bounds.X < bounds.Right && n.Bounds.Right > bounds.X);
            if (hasNumbers)
                return trimmed;
        }

        return null;
    }

    /// <summary>
    /// The combat-log line "Arrived: &lt;monster&gt;": the word (up to 2 characters in, one letter off, so
    /// "Arived" passes) followed by the home monster's name (contained, or at least 0.80 similar).
    /// An empty monster accepts any rest.
    /// </summary>
    public static bool IsArrivedLine(string line, string arrivedText, string monster)
    {
        string a = TextMatcher.Normalize(arrivedText);
        string n = TextMatcher.Normalize(line);
        string mon = TextMatcher.Normalize(monster);
        if (a.Length == 0)
            return false;

        for (int start = 0; start <= 2; start++)
        {
            for (int len = a.Length - 1; len <= a.Length + 1; len++)
            {
                if (len < 1 || start + len > n.Length)
                    continue;
                if (TextMatcher.Levenshtein(n.Substring(start, len), a) > 1)
                    continue;

                string rest = n[(start + len)..];
                if (mon.Length == 0
                    || rest.Contains(mon, StringComparison.Ordinal)
                    || TextMatcher.Similarity(rest, mon) >= 0.80)
                    return true;
            }
        }

        return false;
    }

    /// <summary>The item line "[Auto buff scroll] dd.mm.yyyy" (the autobuff scroll's remaining time).</summary>
    public static bool IsAutoBuffScrollLine(string line)
    {
        string n = TextMatcher.Normalize(line);
        int i = n.IndexOf("autobuff", StringComparison.Ordinal);
        return i >= 0 && n.IndexOf("scro", i + "autobuff".Length, StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// The lines of <paramref name="after"/> not used up by an equal line of <paramref name="before"/>
    /// (counted per string), in <paramref name="after"/>'s order. An old log line that only moved up is not new.
    /// </summary>
    public static IReadOnlyList<string> NewLines(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string b in before)
            left[b] = left.GetValueOrDefault(b) + 1;

        var result = new List<string>();
        foreach (string a in after)
        {
            if (left.GetValueOrDefault(a) > 0)
                left[a]--;
            else
                result.Add(a);
        }

        return result;
    }
}
