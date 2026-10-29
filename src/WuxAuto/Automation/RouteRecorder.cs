using System.IO;
using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Interop;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto Blossom's "Record route" (user, 2026-10-29: "I walk the road once by hand and WuxAuto saves the waypoints"). While
/// recording - the game bound, the pilot not running - every frame of the vision loop polls the turn keys (A / D: the
/// player's own camera turns) and every <see cref="ReadEveryMs"/> the minimap coordinates are read; <see cref="RouteRecording"/>
/// turns them into waypoints. Stop writes the waypoints, the loop start and the camera turns into the Blossom settings and
/// saves them. Start / Stop / MarkLoop come from the UI thread, the ticks from the loop thread - one lock guards the state.
/// </summary>
public sealed class RouteRecorder(AppSettings settings, ScreenAnalyzer analyzer, WindowInput input)
{
    private const int ReadEveryMs = 500;

    private readonly object _gate = new();
    private RouteRecording? _recording;
    private long _readAt;
    private bool _leftWasDown, _rightWasDown;
    private int _unread;

    public event Action<LogLevel, string>? Log;

    /// <summary>The recording started, stopped or gained a waypoint (any thread).</summary>
    public event Action? Changed;

    public bool IsRecording
    {
        get { lock (_gate) return _recording != null; }
    }

    /// <summary>One line for the settings panel.</summary>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_recording is not { } r)
                    return "";
                string turns = r.TurnStrings() is { Count: > 0 } t ? string.Join(", ", t) : "none";
                string at = r.Current is (int x, int y) ? $"at {x}, {y}" : "no coordinates read yet";
                return $"Recording: {r.Count} waypoint(s), loop from {r.LoopFrom}, turns {turns} - {at}";
            }
        }
    }

    public bool Start()
    {
        if (!input.IsBound)
        {
            Log?.Invoke(LogLevel.Warning, "Record route: bind the game window first.");
            return false;
        }

        lock (_gate)
        {
            _recording = new RouteRecording();
            _readAt = 0;
            _unread = 0;
            _leftWasDown = _rightWasDown = false;
        }

        Log?.Invoke(LogLevel.Info, "Recording the route: walk it by hand. A waypoint is saved every 120 units; your A / D camera turns are " +
                                   "saved with the latest waypoint. Press 'Loop starts here' where the loop begins, then 'Stop recording'.");
        Changed?.Invoke();
        return true;
    }

    /// <summary>"Loop starts here" while recording; otherwise it only says how to use it.</summary>
    public void MarkLoop()
    {
        bool recording;
        int? loopFrom = null;
        lock (_gate)
        {
            recording = _recording != null;
            if (_recording is { } r && r.Current != null)
            {
                r.MarkLoopStart();
                loopFrom = r.LoopFrom;
            }
        }

        if (!recording)
            Log?.Invoke(LogLevel.Info, "Loop starts here: press 'Record route' first, then mark the loop start while walking.");
        else if (loopFrom is { } n)
            Log?.Invoke(LogLevel.Info, $"The loop starts at waypoint {n}.");
        else
            Log?.Invoke(LogLevel.Warning, "No coordinates read yet - walk a little, then mark the loop start.");

        Changed?.Invoke();
    }

    /// <summary>
    /// Ends the recording. With at least 2 waypoints they replace the Blossom route (waypoints, loop start, camera turns) and
    /// are saved; fewer - nothing changes. False when nothing was saved.
    /// </summary>
    public bool Stop()
    {
        RouteRecording? r;
        lock (_gate)
        {
            r = _recording;
            _recording = null;
        }

        Changed?.Invoke();
        if (r == null)
            return false;

        if (r.Count < 2)
        {
            Log?.Invoke(LogLevel.Warning, $"Record route: only {r.Count} waypoint(s) - the route was not changed.");
            return false;
        }

        var b = settings.Blossom;
        b.Waypoints = r.WaypointStrings();
        b.LoopFrom = r.LoopFrom;
        b.WaypointTurns = r.TurnStrings();
        SettingsStore.Save(settings);

        // A copy outside settings.json: a WuxAuto from before Auto Blossom drops the settings it does not know when it saves.
        string copy = Path.Combine(AppPaths.Root, "blossom-route.txt");
        try
        {
            File.WriteAllLines(copy,
            [
                $"# Auto Blossom route recorded {DateTime.Now:yyyy-MM-dd HH:mm}",
                $"LoopFrom: {b.LoopFrom}",
                $"Camera turns: {string.Join(", ", b.WaypointTurns)}",
                .. b.Waypoints,
            ]);
        }
        catch (Exception ex)
        {
            Log?.Invoke(LogLevel.Warning, $"Record route: the copy {copy} could not be written ({ex.Message}).");
        }

        Log?.Invoke(LogLevel.Success, $"Route saved: {r.Count} waypoints, loop from {r.LoopFrom}, camera turns " +
                                      $"{(b.WaypointTurns.Count == 0 ? "none" : string.Join(", ", b.WaypointTurns))} (a copy in {copy}).");
        return true;
    }

    public async Task TickAsync(Mat frame)
    {
        RouteRecording? r;
        lock (_gate)
            r = _recording;
        if (r == null)
            return;

        // The player's own camera turns: a press of the turn keys while the game is in front (the "pressed since the last
        // call" bit catches a tap shorter than a frame).
        if (input.IsTargetForeground)
        {
            PollTurn(input.VirtualKeyOf(settings.Dungeon.RotateLeftKey), ref _leftWasDown, TurnDirection.Left, r);
            PollTurn(input.VirtualKeyOf(settings.Input.RotateKey), ref _rightWasDown, TurnDirection.Right, r);
        }

        long now = Environment.TickCount64;
        if (now < _readAt)
            return;
        _readAt = now + ReadEveryMs;

        var coords = await analyzer.ReadCoordinatesAsync(frame);
        if (coords == null)
        {
            if (++_unread == 10)
                Log?.Invoke(LogLevel.Warning, "Record route: the minimap coordinates do not read - keep the minimap open and visible.");
            return;
        }

        _unread = 0;
        bool added;
        int count;
        lock (_gate)
        {
            if (!ReferenceEquals(_recording, r))
                return;
            added = r.AddReading(coords.X, coords.Y);
            count = r.Count;
        }

        if (added)
        {
            Log?.Invoke(LogLevel.Trace, $"Record route: waypoint {count} at {coords}.");
            Changed?.Invoke();
        }
    }

    private void PollTurn(int? vk, ref bool wasDown, TurnDirection direction, RouteRecording r)
    {
        if (vk is not int key)
            return;
        short state = NativeMethods.GetAsyncKeyState(key);
        bool down = (state & 0x8000) != 0;
        bool pressed = (down && !wasDown) || (!down && !wasDown && (state & 0x0001) != 0);
        wasDown = down;
        if (!pressed)
            return;

        lock (_gate)
        {
            if (ReferenceEquals(_recording, r))
                r.AddTurn(direction);
        }

        Changed?.Invoke();
    }
}
