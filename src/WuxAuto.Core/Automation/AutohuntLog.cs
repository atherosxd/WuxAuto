using WuxAuto.Vision;

namespace WuxAuto.Automation;

/// <summary>
/// The game's combat log (left side) prints "Auto hunt started" / "Auto hunt ended" whenever the
/// autohunt key is pressed - in town the off line reads "Auto hunt stopped; Smart Farm journey
/// cancelled." instead (seen 2026-10-27 on an autohunt_unread frame: J in town starts a "Smart
/// Farm journey" back to the hunting ground, the second J cancels it). The log holds ~8 lines and
/// is usually full, so a line only counts when it is new compared with the lines read before the
/// key press (multiset difference, like the "Attack succeeded" check). OCR noise ("Auto hunt
/// endeď", stray characters before the line) is tolerated by keying each line from "autohunt"
/// onward and comparing the rest fuzzily; a rest that starts with the expected words counts
/// whatever follows ("; Smart Farm journey cancelled.").
/// </summary>
public static class AutohuntLog
{
    /// <summary>The normalised text both messages start with ("autohunt"), or null when they share nothing.</summary>
    public static string? Prefix(string startedText, string endedText)
    {
        string a = TextMatcher.Normalize(startedText);
        string b = TextMatcher.Normalize(endedText);
        int n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n])
            n++;
        return n >= 4 ? a[..n] : null;
    }

    /// <summary>The line's key ("autohuntended") when it is an autohunt message, else null.</summary>
    public static string? Key(string line, string prefix)
    {
        string value = TextMatcher.Normalize(line);
        int from = value.IndexOf(prefix, StringComparison.Ordinal);
        return from < 0 ? null : value[from..];
    }

    /// <summary>
    /// The autohunt state announced by a line that is in <paramref name="after"/> but not in
    /// <paramref name="before"/>: true = "started", false = "ended", null = no new line (or one
    /// that reads like neither). With several new lines the bottom-most (newest) decides.
    /// </summary>
    /// <param name="stoppedText">A second "off" wording (town: "Auto hunt stopped"), or null.</param>
    public static bool? NewState(IReadOnlyList<string> before, IReadOnlyList<string> after,
        string startedText, string endedText, out string? line, string? stoppedText = null)
    {
        line = null;
        string? prefix = Prefix(startedText, endedText);
        if (prefix == null)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in before)
            counts[key] = counts.GetValueOrDefault(key) + 1;

        string started = TextMatcher.Normalize(startedText)[prefix.Length..];
        string ended = TextMatcher.Normalize(endedText)[prefix.Length..];
        string stopped = string.IsNullOrWhiteSpace(stoppedText) ? string.Empty : TextMatcher.Normalize(stoppedText);
        stopped = stopped.StartsWith(prefix, StringComparison.Ordinal) ? stopped[prefix.Length..] : string.Empty;
        bool? state = null;
        foreach (string key in after)
        {
            if (key.Length < prefix.Length)
                continue;

            int left = counts.GetValueOrDefault(key);
            if (left > 0)
            {
                counts[key] = left - 1;
                continue;
            }

            string rest = key[prefix.Length..];
            double onScore = Score(rest, started);
            double offScore = Math.Max(Score(rest, ended), stopped.Length > 0 ? Score(rest, stopped) : 0);
            if (Math.Max(onScore, offScore) < 0.5)
                continue;

            state = onScore > offScore;
            line = key;
        }

        return state;
    }

    /// <summary>1.0 when the line's rest starts with the expected words (whatever follows), else the fuzzy similarity.</summary>
    private static double Score(string rest, string expected) =>
        expected.Length >= 4 && rest.StartsWith(expected, StringComparison.Ordinal) ? 1.0 : TextMatcher.Similarity(rest, expected);
}
