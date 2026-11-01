using WuxAuto.Input;

namespace WuxAuto.Core;

/// <summary>Keyboard/mouse input into the game; the platform supplies the sender.</summary>
public interface IGameInput
{
    bool IsDryRun { get; }
    bool IsTargetForeground { get; }
    bool TryFocus(int timeoutMs = 1200);
    bool KeyTap(string keyName);
    bool KeyDown(string keyName);
    bool KeyUp(string keyName);
    bool Click(int clientX, int clientY, MouseButton button = MouseButton.Left);
    bool CtrlClick(int clientX, int clientY);
    bool CtrlKeyTap(string keyName);
    bool CtrlWheel(int clientX, int clientY, int notches);
    bool Drag(int fromX, int fromY, int toX, int toY, int durationMs = 260);
    bool Wheel(int clientX, int clientY, int notches);

    /// <summary>
    /// The key name maps to a key this platform can press (a name that does not is refused on every press, which reads
    /// like the game being behind another window). A platform that cannot tell says yes.
    /// </summary>
    bool IsKnownKey(string keyName) => true;

    /// <summary>
    /// Moves the pointer to the point (no click) and returns which cursor the game shows there, as a number that only
    /// means something compared with another: the game shows a sword over a monster (user, 2026-11-01). 0 = this platform
    /// cannot tell (no pointer, a dry run, the game behind another window).
    /// </summary>
    long CursorAt(int clientX, int clientY) => 0;
}
