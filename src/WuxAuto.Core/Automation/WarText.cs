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

        int rowIndex = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string normalized = TextMatcher.Normalize(lines[i].Text);
            if (normalized.Contains(wanted, StringComparison.Ordinal)
                || TextMatcher.Similarity(lines[i].Text, rowText) >= 0.80)
            {
                rowIndex = i;
                break;
            }
        }

        if (rowIndex < 0)
            return null;

        var (rowLine, rowBounds) = lines[rowIndex];
        var merged = Decide(rowLine);
        if (merged is not null)
            return merged;

        var sameRow = lines
            .Where((l, i) => i != rowIndex
                && Math.Abs(l.Bounds.CenterY - rowBounds.CenterY) <= rowBounds.Height
                && l.Bounds.X >= rowBounds.Right)
            .OrderBy(l => l.Bounds.X);
        foreach (var (text, _) in sameRow)
        {
            var decided = Decide(text);
            if (decided is not null)
                return decided;
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
    /// The teleport-list entry for the zone named by the (cut-off) minimap title: the longest common
    /// prefix wins, scored against the longer of the two. <paramref name="ambiguous"/> is true when a
    /// second entry scores within 0.05 of the best (the best is still returned); null when none is close.
    /// </summary>
    public static string? BestListEntry(string minimapTitle, IReadOnlyList<string> entries, out bool ambiguous)
    {
        ambiguous = false;
        string t = TextMatcher.Normalize(minimapTitle);
        if (t.Length == 0)
            return null;

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
