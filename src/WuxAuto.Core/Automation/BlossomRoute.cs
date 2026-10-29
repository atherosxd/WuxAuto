using System.Globalization;
using WuxAuto.Core;

namespace WuxAuto.Automation;

/// <summary>Auto Blossom's route rules: the waypoint list, the order with its loop, and where to start.</summary>
public static class BlossomRoute
{
    /// <summary>The "x,y" entries as points; blanks and entries that are not two whole numbers are skipped.</summary>
    public static IReadOnlyList<(int X, int Y)> Parse(IEnumerable<string>? waypoints)
    {
        var route = new List<(int X, int Y)>();
        foreach (string entry in waypoints ?? [])
        {
            string[] parts = (entry ?? "").Split(',');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                route.Add((x, y));
        }

        return route;
    }

    /// <summary>
    /// The camera turns per waypoint: "N:KC" or "N:CK" (1-based waypoint, A = left / D = right, tap count) -> N -> (direction, count).
    /// Entries that do not parse, or have a count below 1, are skipped; a waypoint named twice keeps the first.
    /// </summary>
    public static IReadOnlyDictionary<int, (TurnDirection Direction, int Taps)> ParseTurns(IEnumerable<string>? turns)
    {
        var result = new Dictionary<int, (TurnDirection, int)>();
        foreach (string entry in turns ?? [])
        {
            string[] parts = (entry ?? "").Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int waypoint))
                continue;

            // "A5" or "5A" (the user writes the count first), "x" between allowed ("5xA").
            string rest = parts[1].Replace(" ", "", StringComparison.Ordinal);
            if (rest.Length < 2)
                continue;
            bool keyFirst = char.IsLetter(rest[0]);
            char keyChar = keyFirst ? rest[0] : rest[^1];
            string count = (keyFirst ? rest[1..] : rest[..^1]).TrimEnd('x', 'X').TrimStart('x', 'X');
            if (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int taps) || taps < 1)
                continue;

            TurnDirection? direction = char.ToUpperInvariant(keyChar) switch
            {
                'A' => TurnDirection.Left,
                'D' => TurnDirection.Right,
                _ => null,
            };
            if (direction is { } d)
                result.TryAdd(waypoint, (d, taps));
        }

        return result;
    }

    /// <summary>
    /// The point the walk aims at on the leg from <paramref name="from"/> to <paramref name="to"/>: <paramref name="ahead"/>
    /// world units past the foot of the perpendicular from <paramref name="at"/>, never past <paramref name="to"/> (pure
    /// pursuit - a walker off the line is drawn back onto it). A leg shorter than a unit: <paramref name="to"/>.
    /// </summary>
    public static (double X, double Y) LookAhead((int X, int Y) from, (int X, int Y) to, (int X, int Y) at, double ahead)
    {
        double lx = to.X - from.X, ly = to.Y - from.Y, len = Math.Sqrt(lx * lx + ly * ly);
        if (len < 1)
            return (to.X, to.Y);
        double ux = lx / len, uy = ly / len;
        double along = (at.X - from.X) * ux + (at.Y - from.Y) * uy;
        double s = Math.Clamp(along + ahead, 0, len);
        return (from.X + ux * s, from.Y + uy * s);
    }

    /// <summary>The 0-based index after <paramref name="index"/>; after the last, <paramref name="loopFrom"/> (1-based, clamped).</summary>
    public static int Next(int index, int count, int loopFrom)
    {
        if (count <= 0)
            return 0;
        if (index + 1 < count)
            return index + 1;
        return Math.Clamp(loopFrom - 1, 0, count - 1);
    }

    /// <summary>The index of the point closest to (x, y); the first wins a tie, 0 for an empty route.</summary>
    public static int Nearest(IReadOnlyList<(int X, int Y)> route, int x, int y)
    {
        int best = 0;
        long bestD = long.MaxValue;
        for (int i = 0; i < route.Count; i++)
        {
            long dx = route[i].X - x, dy = route[i].Y - y;
            long d = dx * dx + dy * dy;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }
}
