using WuxAuto.Core;

namespace WuxAuto.Phone;

/// <summary>
/// An <see cref="ITouchInput"/> that remembers when the newest gesture began (<see cref="LastSentTicks"/>), so the phone
/// engine can tell a picture from before the pilot's own touch (<see cref="PhoneLoopGate.TouchFrameMs"/>). Only gestures
/// that reach the device count: a dry run never calls them (TouchControls answers it first), and letting go
/// (<see cref="Release"/>) changes nothing on screen worth waiting for.
/// </summary>
/// <param name="clock">Ticks of the frame clock (default <see cref="FrameGate.NowTicks"/>).</param>
public sealed class StampedTouch(ITouchInput inner, Func<long>? clock = null) : ITouchInput
{
    private readonly Func<long> _clock = clock ?? FrameGate.NowTicks;
    private long _lastSent;

    /// <summary>When the newest gesture began (0 = none yet).</summary>
    public long LastSentTicks => Interlocked.Read(ref _lastSent);

    /// <summary>Forgets the last gesture (a new Start).</summary>
    public void Reset() => Interlocked.Exchange(ref _lastSent, 0);

    public bool IsDryRun => inner.IsDryRun;
    public bool IsGameInFront => inner.IsGameInFront;
    public bool BringGameToFront() => inner.BringGameToFront();

    public bool Tap(int x, int y, int holdMs) => Stamp(() => inner.Tap(x, y, holdMs));
    public bool Swipe(int x1, int y1, int x2, int y2, int durationMs) => Stamp(() => inner.Swipe(x1, y1, x2, y2, durationMs));
    public bool Drag(int x1, int y1, int x2, int y2, int durationMs, int endHoldMs) => Stamp(() => inner.Drag(x1, y1, x2, y2, durationMs, endHoldMs));
    public bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs) => Stamp(() => inner.Pinch(cx, cy, fromGap, toGap, durationMs));
    public bool Joystick(int cx, int cy, int dx, int dy, int durationMs) => Stamp(() => inner.Joystick(cx, cy, dx, dy, durationMs));
    public bool PressDown(int x, int y) => Stamp(() => inner.PressDown(x, y));
    public void Release() => inner.Release();

    /// <summary>Stamps when the gesture begins (its effect can show while it runs) and only if it was sent.</summary>
    private bool Stamp(Func<bool> gesture)
    {
        long start = _clock();
        bool sent = gesture();
        if (sent)
            Interlocked.Exchange(ref _lastSent, start);
        return sent;
    }
}
