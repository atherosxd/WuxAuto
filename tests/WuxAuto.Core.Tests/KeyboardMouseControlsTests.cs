using WuxAuto.Input;

namespace WuxAuto.Core.Tests;

/// <summary>
/// <see cref="KeyboardMouseControls"/>: every game action maps to exactly the raw key / mouse call the pilot sent before
/// the actions existed (a recording <see cref="IGameInput"/> lists them in order).
/// </summary>
public sealed class KeyboardMouseControlsTests
{
    [Fact] public void Press_ToggleAutohunt_TapsAutohuntKey() => Assert.Equal(["KeyTap:J"], Run(c => c.Press(GameAction.ToggleAutohunt)));
    [Fact] public void Press_Buff_TapsBuffKey() => Assert.Equal(["KeyTap:O"], Run(c => c.Press(GameAction.Buff)));
    [Fact] public void Press_TargetNearest_TapsTabKey() => Assert.Equal(["KeyTap:Tab"], Run(c => c.Press(GameAction.TargetNearest)));
    [Fact] public void Press_Loot_TapsLootKey() => Assert.Equal(["KeyTap:`"], Run(c => c.Press(GameAction.Loot)));
    [Fact] public void Press_OpenInventory_TapsInventoryKey() => Assert.Equal(["KeyTap:I"], Run(c => c.Press(GameAction.OpenInventory)));
    [Fact] public void Press_ToggleSettings_TapsEscape() => Assert.Equal(["KeyTap:Escape"], Run(c => c.Press(GameAction.ToggleSettings)));
    [Fact] public void Press_OpenAutohuntSettings_TapsAutohuntSettingsKey() => Assert.Equal(["KeyTap:S"], Run(c => c.Press(GameAction.OpenAutohuntSettings)));
    [Fact] public void Press_RunSkillKey_TapsRunSkillKey() => Assert.Equal(["KeyTap:W"], Run(c => c.Press(GameAction.RunSkillKey)));
    [Fact] public void Press_ShuraKey_TapsShuraKey() => Assert.Equal(["KeyTap:E"], Run(c => c.Press(GameAction.ShuraKey)));
    [Fact] public void Press_StopAttack_TapsStopAttackKey() => Assert.Equal(["KeyTap:Space"], Run(c => c.Press(GameAction.StopAttack)));

    [Fact]
    public void Press_UsesTheCurrentSetting()
    {
        var (settings, input, controls) = Harness();
        settings.Dungeon.AutohuntKey = "K";
        controls.Press(GameAction.ToggleAutohunt);
        Assert.Equal(["KeyTap:K"], input.Calls);
    }

    [Fact]
    public void Press_ReturnsTheInputResult()
    {
        var (_, input, controls) = Harness();
        input.Result = false;
        Assert.False(controls.Press(GameAction.ToggleAutohunt));
        input.Result = true;
        Assert.True(controls.Press(GameAction.ToggleAutohunt));
    }

    [Fact]
    public void ActionDown_ShuraKey_HoldsShuraKey() => Assert.Equal(["KeyDown:E", "KeyUp:E"], Run(c =>
    {
        c.ActionDown(GameAction.ShuraKey);
        c.ActionUp(GameAction.ShuraKey);
    }));

    [Fact]
    public void ActionUp_ReleasesTheKeyActionDownPressed_EvenAfterTheSettingChanged()
    {
        var (settings, input, controls) = Harness();
        controls.ActionDown(GameAction.ShuraKey);
        settings.Lod.ShuraKey = "R";
        controls.ActionUp(GameAction.ShuraKey);
        Assert.Equal(["KeyDown:E", "KeyUp:E"], input.Calls);
    }

    [Fact]
    public void ActionUp_WithoutActionDown_SendsNothing()
    {
        var (_, input, controls) = Harness();
        controls.ActionUp(GameAction.ShuraKey);
        Assert.Empty(input.Calls);
    }

    [Fact]
    public void ActionUp_SecondCall_SendsNothing() => Assert.Equal(["KeyDown:E", "KeyUp:E"], Run(c =>
    {
        c.ActionDown(GameAction.ShuraKey);
        c.ActionUp(GameAction.ShuraKey);
        c.ActionUp(GameAction.ShuraKey);
    }));

    [Fact]
    public void ActionDown_ReturnsTheInputResult()
    {
        var (_, input, controls) = Harness();
        input.Result = false;
        Assert.False(controls.ActionDown(GameAction.ShuraKey));
        controls.ActionUp(GameAction.ShuraKey); // nothing went down - nothing to let go
        Assert.Equal(["KeyDown:E"], input.Calls);
    }

    [Fact]
    public void ActionDown_TeleportScroll_IsRefusedAndSendsNothing()
    {
        // No teleport shortcut on the desktop (see Press): nothing to hold, and no key lookup that throws.
        var (_, input, controls) = Harness();
        Assert.False(controls.ActionDown(GameAction.TeleportScroll));
        controls.ActionUp(GameAction.TeleportScroll);
        Assert.Empty(input.Calls);
    }

    [Fact]
    public void Has_FalseForBlankKey()
    {
        var (settings, input, controls) = Harness();
        settings.Lod.LootKey = "";
        Assert.False(controls.Has(GameAction.Loot));
        settings.Lod.LootKey = "   ";
        Assert.False(controls.Has(GameAction.Loot));
        Assert.Empty(input.Calls);
    }

    [Fact]
    public void Has_TrueForEverySetKey()
    {
        var (_, input, controls) = Harness();
        // TeleportScroll is a phone-only shortcut: the desktop never has it (PlatformProfileTests).
        foreach (var action in Enum.GetValues<GameAction>().Where(a => a != GameAction.TeleportScroll))
            Assert.True(controls.Has(action), action.ToString());
        Assert.Empty(input.Calls);
    }

    [Fact]
    public void IsKnownKey_AsksTheInput()
    {
        var (_, input, controls) = Harness();
        input.KnownKeys = ["J"];
        Assert.True(controls.IsKnownKey("J"));
        Assert.False(controls.IsKnownKey("Jj"));
        Assert.Empty(input.Calls);
    }

    [Fact] public void Tap_LeftClicks() => Assert.Equal(["Click:10,20,Left"], Run(c => c.Tap(10, 20)));
    [Fact] public void UseItem_RightClicks() => Assert.Equal(["Click:10,20,Right"], Run(c => c.UseItem(10, 20)));
    [Fact] public void WalkTo_Plain_LeftClicks() => Assert.Equal(["Click:5,6,Left"], Run(c => c.WalkTo(5, 6, forceWalk: false)));
    [Fact] public void WalkTo_ForceWalk_UsesCtrlClick() => Assert.Equal(["CtrlClick:5,6"], Run(c => c.WalkTo(5, 6, forceWalk: true)));

    [Fact] public void SkillTap_WithCtrlSkill_TapsPlainThenCtrl() => Assert.Equal(["KeyTap:1", "CtrlKeyTap:1"], Run(c => c.SkillTap("1"), ctrlSkill: true));
    [Fact] public void SkillTap_WithoutCtrlSkill_TapsPlainOnly() => Assert.Equal(["KeyTap:1"], Run(c => c.SkillTap("1"), ctrlSkill: false));

    [Fact]
    public void SkillTap_PlainBlocked_SkipsCtrlAndFails()
    {
        var (_, input, controls) = Harness(ctrlSkill: true);
        input.Result = false;
        Assert.False(controls.SkillTap("1"));
        Assert.Equal(["KeyTap:1"], input.Calls);
    }

    [Fact]
    public void SkillTap_CtrlBlocked_StillCountsThePress()
    {
        var (_, input, controls) = Harness(ctrlSkill: true);
        input.FailOn = "CtrlKeyTap";
        Assert.True(controls.SkillTap("1"));
        Assert.Equal(["KeyTap:1", "CtrlKeyTap:1"], input.Calls);
    }

    [Fact]
    public void SkillTap_WaitsBeforeTheCtrlTap()
    {
        var (_, input, controls) = Harness(ctrlSkill: true);
        controls.SkillTap("1");
        Assert.True(input.At[1] - input.At[0] >= 55, $"gap {input.At[1] - input.At[0]:0} ms");
    }

    [Fact] public void SkillCtrlTap_TapsCtrlOnly() => Assert.Equal(["CtrlKeyTap:1"], Run(c => c.SkillCtrlTap("1"), ctrlSkill: true));

    [Fact]
    public void SkillUp_ReleasesTheHeldSkill() => Assert.Equal(["KeyDown:1", "KeyUp:1"], Run(c =>
    {
        c.SkillDown("1");
        c.SkillUp("1");
    }));

    [Fact] public void Turn_Left_HoldsRotateLeftKey() => Assert.Equal(["KeyDown:A", "KeyUp:A"], Run(c => c.Turn(TurnDirection.Left, 40)));
    [Fact] public void Turn_Right_HoldsRotateKey() => Assert.Equal(["KeyDown:D", "KeyUp:D"], Run(c => c.Turn(TurnDirection.Right, 40)));

    [Fact]
    public void Turn_HoldsForTheGivenTime()
    {
        var (_, input, controls) = Harness();
        Assert.True(controls.Turn(TurnDirection.Left, 80));
        Assert.True(input.At[1] - input.At[0] >= 75, $"held {input.At[1] - input.At[0]:0} ms");
    }

    [Fact]
    public void Turn_BlockedKeyDown_NoKeyUpAndFails()
    {
        var (_, input, controls) = Harness();
        input.Result = false;
        Assert.False(controls.Turn(TurnDirection.Left, 40));
        Assert.Equal(["KeyDown:A"], input.Calls);
    }

    [Fact]
    public void EndTurn_ReleasesTheKeyBeginTurnPressed() => Assert.Equal(["KeyDown:A", "KeyUp:A"], Run(c =>
    {
        c.BeginTurn(TurnDirection.Left);
        c.EndTurn(TurnDirection.Left);
    }));

    [Fact] public void TurnByDrag_DragsSideways() => Assert.Equal(["Drag:1,2,51,2,300"], Run(c => c.TurnByDrag(1, 2, 50, 300)));
    [Fact] public void TiltCamera_DragsVertically() => Assert.Equal(["Drag:1,2,1,-58,300"], Run(c => c.TiltCamera(1, 2, -60, 300)));
    [Fact] public void ZoomOut_SendsNegativeCtrlWheel() => Assert.Equal(["CtrlWheel:7,8,-15"], Run(c => c.ZoomOut(7, 8, 15)));
    [Fact] public void ScrollList_SendsWheel() => Assert.Equal(["Wheel:7,8,-3"], Run(c => c.ScrollList(7, 8, -3)));

    [Fact]
    public void IsDryRun_MirrorsTheSetting()
    {
        var (settings, _, controls) = Harness();
        Assert.False(controls.IsDryRun);
        settings.Sequence.DryRun = true;
        Assert.True(controls.IsDryRun);
    }

    [Fact]
    public void IsGameInFront_AndTryBringGameToFront_AskTheInput()
    {
        var (_, input, controls) = Harness();
        input.Foreground = false;
        Assert.False(controls.IsGameInFront);
        Assert.False(controls.TryBringGameToFront());
        input.Foreground = true;
        Assert.True(controls.IsGameInFront);
        Assert.True(controls.TryBringGameToFront());
        Assert.Equal(["TryFocus:1200", "TryFocus:1200"], input.Calls);
    }

    private static List<string> Run(Action<IGameControls> act, bool ctrlSkill = true)
    {
        var (_, input, controls) = Harness(ctrlSkill);
        act(controls);
        return input.Calls;
    }

    private static (AppSettings Settings, RecordingInput Input, KeyboardMouseControls Controls) Harness(bool ctrlSkill = true)
    {
        var settings = new AppSettings();
        settings.Dungeon.CtrlSkill = ctrlSkill;
        var input = new RecordingInput(settings);
        return (settings, input, new KeyboardMouseControls(settings, input));
    }

    /// <summary>Records every raw call ("KeyTap:J"), with the time it came; returns <see cref="Result"/> (or false for <see cref="FailOn"/>).</summary>
    private sealed class RecordingInput(AppSettings settings) : IGameInput
    {
        private static readonly long Start = System.Diagnostics.Stopwatch.GetTimestamp();

        public List<string> Calls { get; } = [];
        public List<double> At { get; } = [];
        public bool Result { get; set; } = true;
        public string? FailOn { get; set; }
        public bool Foreground { get; set; } = true;
        public HashSet<string>? KnownKeys { get; set; }

        public bool IsDryRun => settings.Sequence.DryRun;
        public bool IsTargetForeground => Foreground;

        public bool TryFocus(int timeoutMs = 1200)
        {
            Record($"TryFocus:{timeoutMs}");
            return Foreground;
        }

        public bool KeyTap(string keyName) => Record($"KeyTap:{keyName}");
        public bool KeyDown(string keyName) => Record($"KeyDown:{keyName}");
        public bool KeyUp(string keyName) => Record($"KeyUp:{keyName}");
        public bool Click(int clientX, int clientY, MouseButton button = MouseButton.Left) => Record($"Click:{clientX},{clientY},{button}");
        public bool CtrlClick(int clientX, int clientY) => Record($"CtrlClick:{clientX},{clientY}");
        public bool CtrlKeyTap(string keyName) => Record($"CtrlKeyTap:{keyName}");
        public bool CtrlWheel(int clientX, int clientY, int notches) => Record($"CtrlWheel:{clientX},{clientY},{notches}");
        public bool Drag(int fromX, int fromY, int toX, int toY, int durationMs = 260) => Record($"Drag:{fromX},{fromY},{toX},{toY},{durationMs}");
        public bool Wheel(int clientX, int clientY, int notches) => Record($"Wheel:{clientX},{clientY},{notches}");
        public bool IsKnownKey(string keyName) => KnownKeys?.Contains(keyName) ?? true;

        private bool Record(string call)
        {
            Calls.Add(call);
            At.Add(System.Diagnostics.Stopwatch.GetElapsedTime(Start).TotalMilliseconds);
            return FailOn != null && call.StartsWith(FailOn + ":", StringComparison.Ordinal) ? false : Result;
        }
    }
}
