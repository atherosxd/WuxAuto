using WuxAuto.Core;

namespace WuxAuto.Automation;

/// <summary>
/// Auto Blossom's "Record route" (user, 2026-10-29: "I walk the road once by hand and WuxAuto saves the waypoints"): the
/// minimap coordinate readings taken while the player walks become waypoints, one every <c>spacing</c> world units, so the
/// legs are short and follow the road. A reading more than <c>jump</c> from the last one is taken only when the next
/// reading agrees with it (one misread - a dropped digit - must not put a waypoint across the map). A / D presses become
/// the camera turns of the latest waypoint (done when the pilot leaves it, "N:5A"); "Loop starts here" marks the loop.
/// Platform-free: the desktop recorder feeds it.
/// </summary>
public sealed class RouteRecording(int spacing = 120, int jump = 400)
{
    /// <summary>"Loop starts here" adds the current spot as a waypoint when it is this far from the latest one.</summary>
    private const int LoopMarkNewPoint = 60;

    private readonly List<(int X, int Y)> _points = [];
    private readonly Dictionary<int, int> _turns = [];   // 1-based waypoint -> net taps, right positive
    private (int X, int Y)? _current;
    private (int X, int Y)? _candidate;

    public int Count => _points.Count;

    /// <summary>1-based waypoint the route loops back to; 1 until "Loop starts here".</summary>
    public int LoopFrom { get; private set; } = 1;

    /// <summary>The latest accepted reading.</summary>
    public (int X, int Y)? Current => _current;

    /// <summary>One coordinate reading. True when it added a waypoint.</summary>
    public bool AddReading(int x, int y)
    {
        if (_current is (int cx, int cy) && Distance(cx, cy, x, y) > jump)
        {
            bool agrees = _candidate is (int kx, int ky) && Distance(kx, ky, x, y) <= 200;
            _candidate = (x, y);
            if (!agrees)
                return false;
        }

        _candidate = null;
        _current = (x, y);
        if (_points.Count > 0 && Distance(_points[^1].X, _points[^1].Y, x, y) < spacing)
            return false;

        _points.Add((x, y));
        return true;
    }

    /// <summary>One A (left) or D (right) press, for the latest waypoint; before the first waypoint it is dropped.</summary>
    public void AddTurn(TurnDirection direction)
    {
        if (_points.Count == 0)
            return;
        _turns[_points.Count] = _turns.GetValueOrDefault(_points.Count) + (direction == TurnDirection.Right ? 1 : -1);
    }

    /// <summary>The loop starts at the latest waypoint - or at the current spot, made a waypoint, when it is well past it.</summary>
    public void MarkLoopStart()
    {
        if (_current is not (int x, int y))
            return;
        if (_points.Count == 0 || Distance(_points[^1].X, _points[^1].Y, x, y) >= LoopMarkNewPoint)
            _points.Add((x, y));
        LoopFrom = _points.Count;
    }

    /// <summary>The waypoints as settings.json keeps them ("x,y").</summary>
    public List<string> WaypointStrings() => _points.Select(p => $"{p.X},{p.Y}").ToList();

    /// <summary>The camera turns as the settings keep them ("N:5A", "N:2D"); presses that cancelled out leave none.</summary>
    public List<string> TurnStrings() => _turns
        .Where(t => t.Value != 0)
        .OrderBy(t => t.Key)
        .Select(t => $"{t.Key}:{Math.Abs(t.Value)}{(t.Value < 0 ? "A" : "D")}")
        .ToList();

    private static double Distance(int ax, int ay, int bx, int by)
    {
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
