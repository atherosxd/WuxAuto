namespace WuxAuto.Core;

/// <summary>Raw touch gestures on the phone's screen (client pixels); the phone's <see cref="IGameControls"/> maps actions onto these.</summary>
public interface ITouchInput
{
    bool IsDryRun { get; }
    bool IsGameInFront { get; }
    bool BringGameToFront();
    bool Tap(int x, int y, int holdMs);
    bool Swipe(int x1, int y1, int x2, int y2, int durationMs);
    bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs);

    /// <summary>One joystick step; blocks until released.</summary>
    bool Joystick(int cx, int cy, int dx, int dy, int durationMs);

    /// <summary>Long press until <see cref="Release"/>.</summary>
    bool PressDown(int x, int y);

    /// <summary>Ends any held press; no-op when none.</summary>
    void Release();
}
