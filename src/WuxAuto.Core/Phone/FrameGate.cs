namespace WuxAuto.Phone;

/// <summary>
/// Whether the newest captured frame is recent enough to act on. The phone engine skips the pilot's tick when it is
/// not: capture can stop mid-run (consent revoked, a single-use token on Android 14+, the projection killed) and the
/// pilot must then pause instead of tapping on an old picture. Times are <see cref="TimeSpan"/> ticks (100 ns) of one
/// monotonic clock, <see cref="NowTicks"/>.
/// </summary>
public sealed class FrameGate
{
    public FrameGate(int staleMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(staleMs);
        StaleMs = staleMs;
    }

    /// <summary>A frame older than this is stale.</summary>
    public int StaleMs { get; }

    /// <summary>The clock frames are stamped with: milliseconds since boot, as ticks.</summary>
    public static long NowTicks() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// True when a frame stamped <paramref name="frameTicks"/> is at most <see cref="StaleMs"/> old at
    /// <paramref name="nowTicks"/>. No frame (0 or less) is never fresh, and neither is a stamp from the future: the
    /// clocks disagree, so nothing says the picture is current.
    /// </summary>
    public bool Fresh(long nowTicks, long frameTicks)
    {
        if (frameTicks <= 0)
            return false;

        long age = nowTicks - frameTicks;
        return age >= 0 && age <= StaleMs * TimeSpan.TicksPerMillisecond;
    }
}
