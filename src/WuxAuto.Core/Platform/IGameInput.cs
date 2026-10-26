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
}
