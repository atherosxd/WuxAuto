using WuxAuto.Vision;

namespace WuxAuto.Automation;

/// <summary>The four values of the game's Settings "TAB target" row, in the order the chevrons step through them.</summary>
public static class TabTargetOptions
{
    public static readonly string[] All = ["Default", "Nearest enemy", "Enemy worth CP", "Nearest monster"];

    /// <summary>Index of the option an OCR read most resembles (similarity at least 0.75, ties to the first); null when none.</summary>
    public static int? IndexOf(string value)
    {
        int? best = null;
        double bestScore = 0.75;
        for (int i = 0; i < All.Length; i++)
        {
            double score = TextMatcher.Similarity(value, All[i]);
            if (score >= bestScore && (best is null || score > bestScore))
            {
                best = i;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Chevron clicks from <paramref name="current"/> to <paramref name="wanted"/> (negative = left); null when either is not recognised.</summary>
    public static int? StepsTo(string current, string wanted)
    {
        int? from = IndexOf(current);
        int? to = IndexOf(wanted);
        return from is null || to is null ? null : to - from;
    }
}
