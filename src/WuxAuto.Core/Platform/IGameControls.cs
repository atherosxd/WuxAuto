namespace WuxAuto.Core;

/// <summary>A game command the pilot gives without naming how it reaches the game (desktop: a key from the settings).</summary>
public enum GameAction
{
    ToggleAutohunt,
    Buff,
    TargetNearest,
    Loot,
    OpenInventory,
    ToggleSettings,
    OpenAutohuntSettings,
    RunSkillKey,
    ShuraKey,
    StopAttack,
}

/// <summary>Which way the camera turns.</summary>
public enum TurnDirection
{
    Left,
    Right,
}

/// <summary>
/// The game as the pilot drives it: actions, not keys, so a platform without a keyboard (Android) can map the same
/// actions to its own on-screen buttons. Every call that sends something returns false when it was refused (the game
/// not in front, a dry run's no-op still reports true), like <see cref="IGameInput"/>. Coordinates are client pixels.
/// </summary>
public interface IGameControls
{
    bool IsDryRun { get; }
    bool IsGameInFront { get; }
    bool TryBringGameToFront();

    /// <summary>The action can be given at all (desktop: its key is not blank).</summary>
    bool Has(GameAction action);

    /// <summary>The platform can press the key named in a setting (desktop: <see cref="IGameInput.IsKnownKey"/>).</summary>
    bool IsKnownKey(string keyName);

    bool Press(GameAction action);

    /// <summary>Holds an action down until <see cref="ActionUp"/> (Auto War's Shura hold); <see cref="ActionUp"/> lets go of what went down, not of what the setting says now.</summary>
    bool ActionDown(GameAction action);
    void ActionUp(GameAction action);

    bool Tap(int x, int y);

    /// <summary>Uses the item at the point (desktop: right click).</summary>
    bool UseItem(int x, int y);

    /// <summary>One manual skill press: the skill, then (with <see cref="DungeonSettings.CtrlSkill"/>) Ctrl + skill 60 ms later. False only when the plain press was refused.</summary>
    bool SkillTap(string skill);

    /// <summary>Only the Ctrl + skill half of a press (after a held skill is let go).</summary>
    bool SkillCtrlTap(string skill);

    bool SkillDown(string skill);
    bool SkillUp(string skill);

    /// <summary>Walks to the point; <paramref name="forceWalk"/> walks even onto a monster (desktop: Ctrl + click) instead of attacking it.</summary>
    bool WalkTo(int x, int y, bool forceWalk);

    /// <summary>Turns for <paramref name="ms"/> milliseconds (blocks that long).</summary>
    bool Turn(TurnDirection direction, int ms);

    bool BeginTurn(TurnDirection direction);
    void EndTurn(TurnDirection direction);

    /// <summary>Turns by dragging sideways from the point by <paramref name="pixels"/> (negative = left).</summary>
    bool TurnByDrag(int x, int y, int pixels, int durationMs);

    /// <summary>Tilts the camera by dragging vertically from the point by <paramref name="pixels"/> (negative = up).</summary>
    bool TiltCamera(int x, int y, int pixels, int durationMs);

    bool ZoomOut(int x, int y, int notches);

    /// <summary>Scrolls the list under the point (negative notches = down).</summary>
    bool ScrollList(int x, int y, int notches);
}
