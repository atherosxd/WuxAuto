using WuxAuto.Core;

namespace WuxAuto.Phone;

/// <summary>
/// Where the phone game's on-screen buttons are on the current frame (found by the phone's vision). Loop thread only:
/// the pilot's vision loop refreshes and reads it, nothing else may.
/// </summary>
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

    /// <summary>Forgets every position (a size change, a new screen); the next lookup searches everything again.</summary>
    void Invalidate();

    /// <summary>Forgets only this action's shortcut (its press missed); the next lookup searches for it again.</summary>
    void Invalidate(GameAction action);

    /// <summary>Forgets only this skill button (its press missed).</summary>
    void InvalidateSkill(string skill);

    /// <summary>Forgets only the joystick (a walk found none).</summary>
    void InvalidateJoystick();

    /// <summary>Forgets only the ☰ button (a menu press found none).</summary>
    void InvalidateMenu();

    /// <summary>
    /// A press of the action found no shortcut. Default: <see cref="Invalidate(GameAction)"/>. A source that knows a
    /// shortcut was searched for and is simply not there may ignore it and look again on its own schedule (a skill spam
    /// on an absent button must not cost a search per press).
    /// </summary>
    void Missed(GameAction action) => Invalidate(action);

    /// <summary>A press of the skill button found none. Default: <see cref="InvalidateSkill"/> (see <see cref="Missed(GameAction)"/>).</summary>
    void MissedSkill(string skill) => InvalidateSkill(skill);

    /// <summary>
    /// The button a skill press (tap or hold) uses. Default: <see cref="Skill"/>(<paramref name="skill"/>). The phone's
    /// <see cref="ShortcutMap"/> ignores the name and returns the first attack skill icon found on the hotbar or the skill
    /// wheel (user, 2026-10-29: "instead of relying on Key1, use the attack icons").
    /// </summary>
    PixelRect? AttackSkill(string skill) => Skill(skill);

    /// <summary>A skill press found no button (<see cref="AttackSkill"/> null). Default: <see cref="MissedSkill"/>.</summary>
    void MissedAttackSkill(string skill) => MissedSkill(skill);
}
