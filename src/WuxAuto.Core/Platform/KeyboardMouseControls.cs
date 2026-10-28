using WuxAuto.Input;

namespace WuxAuto.Core;

/// <summary>
/// The desktop's <see cref="IGameControls"/>: every action is the key from the settings (read at each call, so a
/// changed setting applies at once), clicks and drags go straight to <see cref="IGameInput"/>.
/// </summary>
public sealed class KeyboardMouseControls(AppSettings settings, IGameInput input) : IGameControls
{
    /// <summary>Between a skill's plain tap and its Ctrl tap: the game sees the plain key go up first.</summary>
    public const int CtrlSkillGapMs = 60;

    public bool IsDryRun => input.IsDryRun;
    public bool IsGameInFront => input.IsTargetForeground;
    public bool TryBringGameToFront() => input.TryFocus();

    public bool Has(GameAction action) => action != GameAction.TeleportScroll && !string.IsNullOrWhiteSpace(KeyOf(action));
    public bool IsKnownKey(string keyName) => input.IsKnownKey(keyName);

    /// <summary>The desktop has no teleport shortcut (it opens the inventory and right-clicks the scroll): <see cref="GameAction.TeleportScroll"/> sends nothing.</summary>
    public bool Press(GameAction action) => action != GameAction.TeleportScroll && input.KeyTap(KeyOf(action));
    public bool ActionDown(GameAction action)
    {
        string key = KeyOf(action);
        if (!input.KeyDown(key))
            return false;

        lock (_held)
            _held[action] = key;
        return true;
    }

    /// <summary>Lets go of exactly the key <see cref="ActionDown"/> pressed (a setting changed mid-hold must not leave it stuck); nothing held, nothing sent.</summary>
    public void ActionUp(GameAction action)
    {
        string? key;
        lock (_held)
        {
            if (!_held.Remove(action, out key))
                return;
        }

        input.KeyUp(key);
    }

    // The key each held action went down with. Locked: Stop / F10 can let go from the UI thread.
    private readonly Dictionary<GameAction, string> _held = [];

    public bool Tap(int x, int y) => input.Click(x, y);
    public bool UseItem(int x, int y) => input.Click(x, y, MouseButton.Right);

    public bool SkillTap(string skill)
    {
        if (!input.KeyTap(skill))
            return false;

        if (settings.Dungeon.CtrlSkill)
        {
            // Let the game see the plain key go up before the same key goes down with Ctrl.
            Thread.Sleep(CtrlSkillGapMs);
            input.CtrlKeyTap(skill);
        }

        return true;
    }

    public bool SkillCtrlTap(string skill) => input.CtrlKeyTap(skill);
    public bool SkillDown(string skill) => input.KeyDown(skill);
    public bool SkillUp(string skill) => input.KeyUp(skill);

    public bool WalkTo(int x, int y, bool forceWalk) => forceWalk ? input.CtrlClick(x, y) : input.Click(x, y);

    public bool Turn(TurnDirection direction, int ms)
    {
        string key = TurnKey(direction);
        if (!input.KeyDown(key))
            return false;

        Thread.Sleep(ms);
        input.KeyUp(key);
        return true;
    }

    public bool BeginTurn(TurnDirection direction) => input.KeyDown(TurnKey(direction));
    public void EndTurn(TurnDirection direction) => input.KeyUp(TurnKey(direction));

    public bool TurnByDrag(int x, int y, int pixels, int durationMs) => input.Drag(x, y, x + pixels, y, durationMs);
    public bool TiltCamera(int x, int y, int pixels, int durationMs) => input.Drag(x, y, x, y + pixels, durationMs);
    public bool ZoomOut(int x, int y, int notches) => input.CtrlWheel(x, y, -notches);
    public bool ScrollList(int x, int y, int notches) => input.Wheel(x, y, notches);

    /// <summary>The key a turn holds: left = <see cref="DungeonSettings.RotateLeftKey"/>, right = <see cref="InputSettings.RotateKey"/>.</summary>
    private string TurnKey(TurnDirection direction) =>
        direction == TurnDirection.Left ? settings.Dungeon.RotateLeftKey : settings.Input.RotateKey;

    /// <summary>The settings key behind an action.</summary>
    private string KeyOf(GameAction action) => action switch
    {
        GameAction.ToggleAutohunt => settings.Dungeon.AutohuntKey,
        GameAction.Buff => settings.Lod.BuffKey,
        GameAction.TargetNearest => settings.Dungeon.TabKey,
        GameAction.Loot => settings.Lod.LootKey,
        GameAction.OpenInventory => settings.Input.InventoryKey,
        GameAction.ToggleSettings => "Escape",
        GameAction.OpenAutohuntSettings => settings.Sequence.AutohuntSettingsKey,
        GameAction.RunSkillKey => settings.Sequence.RunSkillKey,
        GameAction.ShuraKey => settings.Lod.ShuraKey,
        GameAction.StopAttack => settings.Lod.StopAttackKey,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
