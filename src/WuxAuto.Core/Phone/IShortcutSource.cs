using WuxAuto.Core;

namespace WuxAuto.Phone;

/// <summary>Where the phone game's on-screen buttons are on the current frame (found by the phone's vision).</summary>
public interface IShortcutSource
{
    /// <summary>The frame size the positions below belong to (0×0 before the first frame).</summary>
    (int Width, int Height) FrameSize { get; }

    /// <summary>Last known position of the action's shortcut icon for the current frame size; null = not found.</summary>
    PixelRect? Find(GameAction action);

    /// <summary>The skill wheel button "1" / "2" / "3".</summary>
    PixelRect? Skill(string skill);

    /// <summary>The walk joystick: its centre and how far the stick moves.</summary>
    (int Cx, int Cy, int Radius)? Joystick { get; }

    /// <summary>The ☰ menu button.</summary>
    PixelRect? Menu { get; }

    /// <summary>Forgets the positions (after a miss or a size change); the next lookup searches again.</summary>
    void Invalidate();
}
