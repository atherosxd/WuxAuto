using System.Text.RegularExpressions;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Automation;

/// <summary>Auto Blossom's pure text rules: the target's HP numbers and the chat's AFK Checker count.</summary>
public static class BlossomText
{
    static readonly Regex HpPair = new(@"(\d+)/(\d+)", RegexOptions.Compiled);
    static readonly Regex AfkCount = new(@"(\d)\s*/\s*5(?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// The target bar's "cur / max" (user, 2026-10-30: "Only Attack monster if full health"): the line
    /// <see cref="WarText.TargetHpLine"/> accepts in <paramref name="box"/>, an O read as 0. Null when none reads.
    /// </summary>
    public static (int Cur, int Max)? TargetHp(IReadOnlyList<(string Text, PixelRect Bounds)> lines, PixelRect box)
    {
        if (WarText.TargetHpLine(lines, box) is not { } line)
            return null;
        var m = HpPair.Match(line.Text.Replace(" ", "").Replace('O', '0').Replace('o', '0'));
        return m.Success && int.TryParse(m.Groups[1].Value, out int cur) && int.TryParse(m.Groups[2].Value, out int max)
            ? (cur, max)
            : null;
    }

    /// <summary>
    /// The newest AFK Checker count in the chat box ("(Whisper) [AFK Checker] : Start 1/5" -> 1, "... Reset 0/5" -> 0; user,
    /// 2026-10-30): the bottom-most line in the chat area (left 45 % of the client, below 55 % of its height) that names
    /// <paramref name="checker"/> and holds a "k/5". Null when none reads.
    /// </summary>
    public static int? AfkCheckerCount(IReadOnlyList<(string Text, PixelRect Bounds)> lines, string checker, int width, int height)
    {
        string key = TextMatcher.Normalize(checker);
        if (key.Length == 0)
            return null;

        foreach (var (text, bounds) in lines.OrderByDescending(l => l.Bounds.Y))
        {
            if (bounds.CenterX > width * 0.45 || bounds.CenterY < height * 0.55)
                continue;
            if (!TextMatcher.Normalize(text).Contains(key, StringComparison.Ordinal))
                continue;
            var m = AfkCount.Match(text);
            if (m.Success)
                return int.Parse(m.Groups[1].Value);
        }

        return null;
    }
}
