using WuxAuto.Core;

namespace WuxAuto.Vision;

/// <summary>Small fuzzy matcher for OCR output ("Labyrinth" vs "Labyrinlh").</summary>
public static class TextMatcher
{
    public static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int count = 0;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[count++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..count]);
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    public static double Similarity(string a, string b)
    {
        string left = Normalize(a);
        string right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
            return 0;

        if (left == right)
            return 1.0;

        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            // Substring containment is how "Labyrinth" matches "[The Labyrinth (Adept)]" in OCR
            // output, but it must not turn a stray letter or two into a match for a boss name.
            int shortest = Math.Min(left.Length, right.Length);
            if (shortest >= 4)
            {
                double ratio = shortest / (double)Math.Max(left.Length, right.Length);
                return 0.80 + 0.15 * ratio;
            }
        }

        int distance = Levenshtein(left, right);
        return 1.0 - distance / (double)Math.Max(left.Length, right.Length);
    }

    /// <summary>
    /// True when <paramref name="read"/> has at least 60% of the letters of <paramref name="target"/>.
    /// Containment matching accepts any 4+ letter piece ("open" for "DUNGEON GATE OPENED",
    /// "[Rage" for "Ragesoul Heathen"), which is wrong wherever such words appear elsewhere.
    /// </summary>
    public static bool CoversMost(string read, string target)
        => Normalize(read).Length >= Normalize(target).Length * 0.6;

    /// <summary>Picks the word (or line) that best matches the target text.</summary>
    public static TextHit? FindBest(IReadOnlyList<OcrWord> words, string target, double minSimilarity, int? anchorX = null, int? anchorY = null, double ocrMs = 0)
    {
        if (words.Count == 0 || string.IsNullOrWhiteSpace(target))
            return null;

        var candidates = new List<TextHit>();

        foreach (var word in words)
        {
            double similarity = Similarity(word.Text, target);
            if (similarity >= minSimilarity)
                candidates.Add(new TextHit(word.Text, similarity, word.Bounds, word.LineText, word.LineBounds, ocrMs));
        }

        // OCR sometimes splits a label; fall back to the whole line.
        foreach (var line in words.GroupBy(w => w.LineText))
        {
            double similarity = Similarity(line.Key, target);
            if (similarity >= minSimilarity)
            {
                var first = line.First();
                candidates.Add(new TextHit(line.Key, similarity * 0.98, first.LineBounds, line.Key, first.LineBounds, ocrMs));
            }
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.Similarity).First();
        if (anchorX.HasValue && anchorY.HasValue)
        {
            var nearby = candidates
                .Where(c => c.Similarity >= best.Similarity - 0.05)
                .OrderBy(c => c.Bounds.DistanceTo(anchorX.Value, anchorY.Value))
                .First();
            best = nearby;
        }

        return best;
    }
}
