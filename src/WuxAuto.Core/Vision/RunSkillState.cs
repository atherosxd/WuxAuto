using WuxAuto.Core;

namespace WuxAuto.Vision;

/// <summary>One Run skill icon on the skill bar and the share of the gold ring round it.</summary>
public sealed record RunSkillCopy(TemplateMatch Icon, double Ring);

/// <summary>
/// The town Run skill check over every copy of the Run icon on the skill bar. The same skill can sit
/// in two slots (the user's 1273x1024 bar: x 664 and 858), and the game draws the gold ring only round
/// the slot that was pressed (user, 2026-10-27), so one ringed copy = on. <see cref="Pick"/> is the
/// ringed copy when there is one, else the copy to click (best score, equal scores -> the leftmost).
/// Before this only the leftmost copy was measured: with the ring on the other slot it read "off" and
/// the click on it would have turned Run off.
/// </summary>
public sealed record RunSkillState(IReadOnlyList<RunSkillCopy> Copies, RunSkillCopy Pick, bool On)
{
    /// <summary>Scores this close count as equal (the copies are the same picture).</summary>
    private const double ScoreTie = 0.01;

    /// <summary>
    /// The verdict over <paramref name="hits"/>; null when there are none. Hits on the same spot (the
    /// matcher can return one icon twice) count once.
    /// </summary>
    public static RunSkillState? Decide(IEnumerable<RunSkillCopy> hits, double ringMin)
    {
        var copies = new List<RunSkillCopy>();
        foreach (var hit in hits)
        {
            if (!copies.Any(kept => SameSpot(kept.Icon.Bounds, hit.Icon.Bounds)))
                copies.Add(hit);
        }

        if (copies.Count == 0)
            return null;

        var ringed = copies
            .Where(c => c.Ring >= ringMin)
            .OrderByDescending(c => c.Ring)
            .FirstOrDefault();
        if (ringed != null)
            return new RunSkillState(copies, ringed, On: true);

        RunSkillCopy best = copies[0];
        foreach (var copy in copies.Skip(1))
        {
            double diff = copy.Icon.Score - best.Icon.Score;
            if (diff > ScoreTie || (Math.Abs(diff) <= ScoreTie && copy.Icon.Bounds.CenterX < best.Icon.Bounds.CenterX))
                best = copy;
        }

        return new RunSkillState(copies, best, On: false);
    }

    private static bool SameSpot(PixelRect a, PixelRect b)
    {
        int radius = Math.Max(3, Math.Min(a.Width, b.Width) / 2);
        return Math.Abs(a.CenterX - b.CenterX) <= radius && Math.Abs(a.CenterY - b.CenterY) <= radius;
    }
}
