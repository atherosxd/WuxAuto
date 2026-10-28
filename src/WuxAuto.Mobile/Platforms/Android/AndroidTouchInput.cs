using System.Collections.Concurrent;
using Android.AccessibilityServices;
using Android.Content;
using Android.Util;
using Android.Views;
using WuxAuto.Core;
using APath = Android.Graphics.Path;
using Stroke = Android.AccessibilityServices.GestureDescription.StrokeDescription;

namespace WuxAuto.Mobile;

/// <summary>
/// <see cref="ITouchInput"/> through the accessibility service's gestures. One gesture at a time, all of them on one
/// worker thread: a tap inside a held joystick stroke is lost (M0 MULTI), so a new gesture first ends the held one.
/// The caller blocks until its gesture has finished (or was refused). Nothing is dispatched while the game is not the
/// active window, except the lift that ends a finger already down in it.
/// </summary>
public sealed class AndroidTouchInput : ITouchInput, IDisposable
{
    /// <summary>A held press is one continued stroke per this many ms; also the longest wait for it to end.</summary>
    public const int HoldSegmentMs = 400;

    /// <summary>The platform's limit is 60 s per gesture.</summary>
    const int MaxStrokeMs = 59_000;

    /// <summary>Extra time a caller waits beyond the gesture's own duration before giving up on its callback.</summary>
    const int CallbackSlackMs = 1500;

    const string Tag = MainApplication.Tag;

    readonly BlockingCollection<Job> _jobs = new();
    readonly CancellationTokenSource _stop = new();
    readonly Thread _worker;
    readonly object _lock = new();
    readonly ManualResetEventSlim _noHold = new(true);

    // Guarded by _lock.
    Held? _held;
    JoystickStep? _joystick;

    volatile bool _releaseRequested;

    // When the last finger lift went out (Environment.TickCount64; worker and Release's joystick cut).
    long _liftedAt;

    public AndroidTouchInput()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "wux-touch" };
        _worker.Start();
    }

    /// <summary>Dry run: every call reports success and sends nothing. The engine points it at the settings.</summary>
    public Func<bool> DryRunSource { get; set; } = () => false;

    public bool IsDryRun => DryRunSource();

    /// <summary>
    /// The gap after a lift before the next gesture goes out (<see cref="PhoneSettings.TapSettleMs"/>; the engine points
    /// it at the settings): the game reads the finger once per frame and drops a tap within ~40 ms of a lift (Task 1;
    /// MuMu 2026-10-29: the gesture queued behind a held press was lost).
    /// </summary>
    public Func<int> LiftSettleMsSource { get; set; } = () => 100;

    static PilotAccessibilityService? Service => PilotAccessibilityService.Instance;

    public bool IsGameInFront => Service?.IsGameActive() == true;

    /// <summary>A finger is down (a <see cref="PressDown"/> not yet released).</summary>
    public bool IsHolding
    {
        get { lock (_lock) return _held != null; }
    }

    /// <summary>Starts the game's task and waits up to 3 s for it to become the active window. Do not call on the UI thread.</summary>
    public bool BringGameToFront()
    {
        var service = Service;
        if (service == null)
            return false;
        if (service.IsGameActive())
            return true;
        if (!service.LaunchGame())
            return false;

        for (int waited = 0; waited < 3000; waited += 100)
        {
            Thread.Sleep(100);
            if (service.IsGameActive())
                return true;
        }

        return false;
    }

    public bool Tap(int x, int y, int holdMs)
    {
        int ms = ClampMs(holdMs);
        return Run($"tap {x},{y}", ms, () =>
        {
            var (px, py) = Clamp(x, y);
            return Dispatch(ms, new Stroke(Dot(px, py), 0, ms));
        });
    }

    public bool Swipe(int x1, int y1, int x2, int y2, int durationMs)
    {
        int ms = ClampMs(durationMs);
        return Run($"swipe {x1},{y1}->{x2},{y2}", ms, () =>
        {
            var (ax, ay) = Clamp(x1, y1);
            var (bx, by) = Clamp(x2, y2);
            return Dispatch(ms, new Stroke(Line(ax, ay, bx, by), 0, ms));
        });
    }

    public bool Drag(int x1, int y1, int x2, int y2, int durationMs, int endHoldMs)
    {
        int ms = ClampMs(durationMs);
        int hold = Math.Clamp(endHoldMs, 0, MaxStrokeMs);
        return Run($"drag {x1},{y1}->{x2},{y2}", ms + hold, () =>
        {
            var (ax, ay) = Clamp(x1, y1);
            var (bx, by) = Clamp(x2, y2);
            var move = new Stroke(Line(ax, ay, bx, by), 0, ms, hold > 0);
            if (!Dispatch(ms, move))
            {
                if (hold > 0)
                    LiftAfter(move, bx, by);
                return false;
            }

            if (hold == 0)
                return true;

            // The finger rests at the end (1 px wiggle: a stroke needs a path) and lifts with the last segment.
            var (ox, oy) = Wiggle(bx, by);
            return Dispatch(hold, move.ContinueStroke(Line(bx, by, ox, oy), 0, hold, false)!);
        });
    }

    /// <summary>Two fingers on a horizontal line through the centre, <paramref name="fromGap"/> → <paramref name="toGap"/> px apart (each finger half of that from the centre).</summary>
    public bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs)
    {
        int ms = ClampMs(durationMs);
        return Run($"pinch {cx},{cy} {fromGap}->{toGap}", ms, () =>
        {
            var (w, _) = ScreenSize();
            var (x, y) = Clamp(cx, cy);
            int reach = Math.Max(1, Math.Min(x, w - 1 - x)); // both fingers stay on the screen
            int from = Math.Clamp(fromGap / 2, 1, reach);
            int to = Math.Clamp(toGap / 2, 1, reach);
            return Dispatch(ms,
                new Stroke(Line(x - from, y, x - to, y), 0, ms),
                new Stroke(Line(x + from, y, x + to, y), 0, ms));
        });
    }

    /// <summary>One stroke from the stick's centre to centre + (dx, dy) over <paramref name="durationMs"/>; blocks until it ends. <see cref="Release"/> cuts it short.</summary>
    public bool Joystick(int cx, int cy, int dx, int dy, int durationMs)
    {
        int ms = ClampMs(durationMs);
        return Run($"joystick {dx},{dy}", ms, () =>
        {
            var (ax, ay) = Clamp(cx, cy);
            var (bx, by) = Clamp(cx + dx, cy + dy);
            var step = new JoystickStep(ax, ay);
            lock (_lock)
                _joystick = step;
            try
            {
                bool done = Dispatch(ms, new Stroke(Line(ax, ay, bx, by), 0, ms));
                return done && !step.Cancelled;
            }
            finally
            {
                lock (_lock)
                    _joystick = null;
            }
        });
    }

    /// <summary>Finger down at (x, y), kept down with a continued stroke every <see cref="HoldSegmentMs"/> until <see cref="Release"/> or the next gesture. Returns once the finger is down.</summary>
    public bool PressDown(int x, int y)
    {
        return Run($"press {x},{y}", HoldSegmentMs, () =>
        {
            var (px, py) = Clamp(x, y);
            var (ox, oy) = Wiggle(px, py);
            var stroke = new Stroke(Line(px, py, ox, oy), 0, HoldSegmentMs, true);
            _releaseRequested = false;
            var service = Service;
            if (service == null)
                return false;

            var segment = service.DispatchAsync(new GestureDescription.Builder().AddStroke(stroke)!.Build()!);
            if (segment.IsCompleted && !segment.Result)
                return false;

            lock (_lock)
            {
                _noHold.Reset();
                _held = new Held(stroke, ox, oy, px, py, segment);
            }

            return true;
        });
    }

    /// <summary>
    /// Lets go: a held press lifts at the end of its current segment (waits up to ~3.5 s for it), a joystick step in
    /// flight is cut short at once (a 10 ms touch on the stick's centre cancels it without walking; only with the game in
    /// front, otherwise the step is just marked cancelled). Safe from any thread except
    /// the touch worker, and with nothing held.
    /// </summary>
    public void Release()
    {
        CancelJoystick();

        bool holding;
        lock (_lock)
            holding = _held != null;
        if (!holding)
            return;

        _releaseRequested = true;
        // The worker waits out the segment (HoldSegmentMs + slack), then the lift (its own slack).
        if (Thread.CurrentThread != _worker && !_noHold.Wait(HoldSegmentMs + 2 * CallbackSlackMs + 100))
            Log.Warn(Tag, "release: the held press did not end in time");
    }

    void CancelJoystick()
    {
        JoystickStep? step;
        lock (_lock)
        {
            step = _joystick;
            if (step == null || step.Cancelled)
                return;
            step.Cancelled = true;
        }

        // A new gesture cancels the one in flight; this one touches the stick's centre, which walks nowhere. Never with
        // another app in front: the step stays marked cancelled and runs out by itself.
        var service = Service;
        if (service == null || !service.IsGameActive())
            return;
        var (ox, oy) = Wiggle(step.Cx, step.Cy);
        _ = service.DispatchAsync(Build(new Stroke(Line(step.Cx, step.Cy, ox, oy), 0, 10)));
        Interlocked.Exchange(ref _liftedAt, Environment.TickCount64 + 10);
        Log.Info(Tag, "joystick step cut short");
    }

    /// <summary>
    /// Queues <paramref name="work"/> for the worker and waits for it. Dry run: true, nothing queued. No service or the
    /// game behind: false, nothing queued (the worker checks the game again right before dispatching).
    /// </summary>
    bool Run(string what, int gestureMs, Func<bool> work)
    {
        if (IsDryRun)
            return true;
        if (Service == null)
        {
            Log.Warn(Tag, $"{what}: accessibility service is off");
            return false;
        }

        if (!IsGameInFront)
        {
            Log.Info(Tag, $"{what}: game not in front, nothing sent");
            return false;
        }

        if (_stop.IsCancellationRequested)
            return false;

        var job = new Job(what, work);
        _jobs.Add(job);
        // Queue wait (a held segment and its lift end first) + the gesture itself, each with callback slack.
        int timeout = HoldSegmentMs + gestureMs + 3 * CallbackSlackMs;
        if (!job.Done.Task.Wait(timeout))
        {
            // The worker skips it if it has not started yet; one already being dispatched still goes out.
            job.Abandoned = true;
            Log.Warn(Tag, $"{what}: no result after {timeout} ms");
            return false;
        }

        return job.Done.Task.Result;
    }

    void WorkerLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Held? held;
                lock (_lock)
                    held = _held;

                if (held != null)
                {
                    try
                    {
                        KeepOrEndHold(held);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(Tag, $"held press: {ex}");
                        ClearHold();
                    }

                    continue;
                }

                Job job;
                try
                {
                    job = _jobs.Take(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Execute(job);
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"touch worker: {ex}");
            }
        }
    }

    /// <summary>Waits for the held segment to end, then continues it, or lifts the finger when a job, a release, the game leaving or the service going away says so.</summary>
    void KeepOrEndHold(Held held)
    {
        if (!held.Segment.Wait(HoldSegmentMs + CallbackSlackMs))
        {
            // No callback: the finger may still be down, so end the stroke.
            Log.Warn(Tag, "held press: no segment callback, lifting");
            LiftAfter(held.Stroke, held.EndX, held.EndY);
            ClearHold();
            return;
        }

        if (!held.Segment.Result)
        {
            // Cancelled by the system (another gesture, the screen turned off): the finger is already up.
            Log.Info(Tag, "held press ended by the system");
            ClearHold();
            return;
        }

        bool hasJob = _jobs.TryTake(out var job);
        var service = Service;
        if (hasJob || _releaseRequested || service == null || !service.IsGameActive())
        {
            LiftAfter(held.Stroke, held.EndX, held.EndY);
            ClearHold();
            if (job != null)
                Execute(job);
            return;
        }

        // Back and forth between the two points: a continued stroke has to start where the last one ended.
        var next = held.Stroke.ContinueStroke(Line(held.EndX, held.EndY, held.OtherX, held.OtherY), 0, HoldSegmentMs, true)!;
        var segment = service.DispatchAsync(Build(next));
        lock (_lock)
            _held = new Held(next, held.OtherX, held.OtherY, held.EndX, held.EndY, segment);
    }

    void Execute(Job job)
    {
        bool result = false;
        try
        {
            // The caller may have given up, or the game gone, between queuing and now.
            if (job.Abandoned)
                Log.Info(Tag, $"{job.What}: dropped, the caller stopped waiting");
            else if (Service?.IsGameActive() == true)
            {
                SettleAfterLift();
                result = job.Work();
            }
            else
                Log.Info(Tag, $"{job.What}: game not in front, nothing sent");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"{job.What} failed: {ex.Message}");
        }
        finally
        {
            job.Done.TrySetResult(result);
        }
    }

    /// <summary>Ends a continued stroke where it is (finger up). Goes out even with the game behind: the finger is down in the game's window.</summary>
    void LiftAfter(Stroke stroke, int x, int y)
    {
        var service = Service;
        if (service == null)
            return;
        var (ox, oy) = Wiggle(x, y);
        var lift = stroke.ContinueStroke(Line(x, y, ox, oy), 0, 20, false)!;
        if (!service.DispatchAsync(Build(lift)).Wait(20 + CallbackSlackMs))
            Log.Warn(Tag, "lift: no callback");
        Interlocked.Exchange(ref _liftedAt, Environment.TickCount64);
    }

    /// <summary>Worker only: waits out what is left of <see cref="LiftSettleMsSource"/> since the last lift.</summary>
    void SettleAfterLift()
    {
        int settle;
        try
        {
            settle = LiftSettleMsSource();
        }
        catch (Exception)
        {
            settle = 100;
        }

        int wait = Phone.LiftSettle.WaitMs(Interlocked.Read(ref _liftedAt), settle, Environment.TickCount64);
        if (wait > 0)
            Thread.Sleep(wait);
    }

    void ClearHold()
    {
        lock (_lock)
            _held = null;
        _releaseRequested = false;
        _noHold.Set();
    }

    /// <summary>Dispatches the strokes as one gesture and waits for it; true when it completed.</summary>
    static bool Dispatch(int gestureMs, params Stroke[] strokes)
    {
        var service = Service;
        if (service == null)
            return false;
        var task = service.DispatchAsync(Build(strokes));
        return task.Wait(gestureMs + CallbackSlackMs) && task.Result;
    }

    static GestureDescription Build(params Stroke[] strokes)
    {
        var builder = new GestureDescription.Builder();
        foreach (var stroke in strokes)
            builder.AddStroke(stroke);
        return builder.Build()!;
    }

    static int ClampMs(int ms) => Math.Clamp(ms, 1, MaxStrokeMs);

    /// <summary>The display's current size in pixels (touch coordinates follow its rotation).</summary>
    static (int W, int H) ScreenSize()
    {
        var context = (Context?)Service ?? Android.App.Application.Context;
        var size = DisplaySize.Current(context);
        return (Math.Max(1, size.W), Math.Max(1, size.H));
    }

    static (int X, int Y) Clamp(int x, int y)
    {
        var (w, h) = ScreenSize();
        return (Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1));
    }

    /// <summary>A point 1 px away (a stroke needs a path), inside the screen.</summary>
    static (int X, int Y) Wiggle(int x, int y)
    {
        var (_, h) = ScreenSize();
        return (x, y + 1 < h ? y + 1 : y - 1);
    }

    static APath Dot(int x, int y)
    {
        var (ox, oy) = Wiggle(x, y);
        return Line(x, y, ox, oy);
    }

    static APath Line(float x1, float y1, float x2, float y2)
    {
        var path = new APath();
        path.MoveTo(x1, y1);
        path.LineTo(x2, y2);
        return path;
    }

    public void Dispose()
    {
        Release();
        _stop.Cancel();
        _jobs.CompleteAdding();
    }

    sealed record Job(string What, Func<bool> Work)
    {
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Abandoned;
    }

    /// <summary>The held finger: its last dispatched segment, where that ends and where the next one goes.</summary>
    sealed record Held(Stroke Stroke, int EndX, int EndY, int OtherX, int OtherY, Task<bool> Segment);

    sealed class JoystickStep(int cx, int cy)
    {
        public int Cx { get; } = cx;
        public int Cy { get; } = cy;
        public bool Cancelled { get; set; }
    }
}
