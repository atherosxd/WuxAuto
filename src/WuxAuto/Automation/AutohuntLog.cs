using WuxAuto.Vision;

namespace WuxAuto.Automation;

/// <summary>
/// The game's combat log (left side) prints "Auto hunt started" / "Auto hunt ended" whenever the
/// autohunt key is pressed. The log holds ~8 lines and is usually full, so a line only counts
/// when it is new compared with the lines read before the key press (multiset difference,
/// like the "Attack succeeded" check). OCR noise ("Auto hunt endeď", stray characters before the
/// line) is tolerated by keying each line from "autohunt" onward and comparing the rest fuzzily.
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
    public static bool? NewState(IReadOnlyList<string> before, IReadOnlyList<string> after,
        string startedText, string endedText, out string? line)
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
            double onScore = TextMatcher.Similarity(rest, started);
            double offScore = TextMatcher.Similarity(rest, ended);
            if (Math.Max(onScore, offScore) < 0.5)
                continue;

            state = onScore > offScore;
            line = key;
        }

        return state;
    }
}
