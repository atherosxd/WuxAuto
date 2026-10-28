namespace WuxAuto.Core;

/// <summary>Raw touch gestures on the phone's screen (client pixels); the phone's <see cref="IGameControls"/> maps actions onto these.</summary>
public interface ITouchInput
{
    bool IsDryRun { get; }
    bool IsGameInFront { get; }
    bool BringGameToFront();
    bool Tap(int x, int y, int holdMs);
    bool Swipe(int x1, int y1, int x2, int y2, int durationMs);
    /// <summary>
    /// Finger down at the start, moves to the end over <paramref name="durationMs"/>, stays there for
    /// <paramref name="endHoldMs"/>, lifts. The game reads the finger at its frame rate, so a lift right after the move
    /// loses part of it (MuMu 2026-10-28: the camera turns more with a 100 ms hold). <see cref="Swipe"/> has no hold.
    /// </summary>
    bool Drag(int x1, int y1, int x2, int y2, int durationMs, int endHoldMs);

    bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs);

    /// <summary>One joystick step; blocks until released.</summary>
    bool Joystick(int cx, int cy, int dx, int dy, int durationMs);

    /// <summary>Long press until <see cref="Release"/>.</summary>
    bool PressDown(int x, int y);

    /// <summary>Ends any held press; no-op when none.</summary>
    void Release();
}
