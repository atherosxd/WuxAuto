namespace WuxAuto.Phone;

/// <summary>
/// The pause the touch worker keeps between a finger lift and the next gesture: the game reads the finger once per frame
/// and drops a tap that starts within ~40 ms of a lift (Task 1: lost at 38 ms, delivered from 41 ms), so the next gesture
/// waits out <see cref="WuxAuto.Core.PhoneSettings.TapSettleMs"/> from the lift (MuMu 2026-10-29: the gesture queued behind a held
/// press went out at once and was lost).
/// </summary>
public static class LiftSettle
{
    /// <summary>The longest settle honoured (a bad setting must not stall the worker).</summary>
    public const int MaxSettleMs = 2000;

    /// <summary>How long to wait now (ms, 0 = go): <paramref name="settleMs"/> after <paramref name="liftedAt"/> (0 = no lift yet), times in ms.</summary>
    public static int WaitMs(long liftedAt, int settleMs, long now)
    {
        if (liftedAt <= 0)
            return 0;
        long wait = liftedAt + Math.Clamp(settleMs, 0, MaxSettleMs) - now;
        return wait > 0 ? (int)wait : 0;
    }
}
