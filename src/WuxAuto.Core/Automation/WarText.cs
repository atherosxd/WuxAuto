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
}
