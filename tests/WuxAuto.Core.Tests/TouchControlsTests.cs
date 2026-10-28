using WuxAuto.Phone;

namespace WuxAuto.Core.Tests;

/// <summary>
/// <see cref="TouchControls"/>: every game action maps to the touch gestures listed in Plan 2 Task 3 (a recording
/// <see cref="ITouchInput"/> lists them in order, the injected sleep among them). Frame 1000×800: the character
/// stands at (500, 432), turn swipes run through (500, 280).
/// </summary>
public sealed class TouchControlsTests
{
    private static readonly PixelRect Icon = new(100, 200, 40, 30);   // centre 120,215
    private static readonly PixelRect MenuIcon = new(900, 10, 50, 50); // centre 925,35
    private static readonly PixelRect SkillButton = new(800, 600, 60, 60); // centre 830,630

    // --- Press: shortcut icons -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(GameAction.TeleportScroll)]
    [InlineData(GameAction.ToggleAutohunt)]
    [InlineData(GameAction.Buff)]
    [InlineData(GameAction.TargetNearest)]
    [InlineData(GameAction.Loot)]
    [InlineData(GameAction.RunSkillKey)]
    [InlineData(GameAction.ShuraKey)]
    [InlineData(GameAction.StopAttack)]
    public void Press_ShortcutAction_TapsItsIconCentre(GameAction action)
    {
        var h = new Harness();
        h.Shortcuts.Icons[action] = Icon;
        Assert.True(h.Controls.Press(action));
        Assert.Equal(["Tap:120,215,60"], h.Touch.Calls);
    }

    // --- Press: actions behind the ☰ menu --------------------------------------------------------------------------

    [Theory]
    [InlineData(GameAction.OpenInventory)]
    [InlineData(GameAction.ToggleSettings)]
    [InlineData(GameAction.OpenAutohuntSettings)]
    public void Press_MenuAction_TapsTheMenuOnly(GameAction action)
    {
        var h = new Harness();
        h.Shortcuts.Menu = MenuIcon;
        Assert.True(h.Controls.Press(action));
        Assert.Equal(["Tap:925,35,60"], h.Touch.Calls);
    }

    // --- Has (provisional, Task 1 confirms) -------------------------------------------------------------------------

    [Theory]
    [InlineData(GameAction.TeleportScroll)]
    [InlineData(GameAction.ToggleAutohunt)]
    [InlineData(GameAction.Buff)]
    [InlineData(GameAction.TargetNearest)]
    [InlineData(GameAction.Loot)]
    [InlineData(GameAction.RunSkillKey)]  // decided: the shortcut icon was found
    [InlineData(GameAction.ShuraKey)]     // decided: the shortcut icon was found
    [InlineData(GameAction.StopAttack)]   // decided: the shortcut icon was found
    public void Has_ShortcutAction_IsIconFound(GameAction action)
    {
        var h = new Harness();
        h.Shortcuts.Menu = MenuIcon;
        Assert.False(h.Controls.Has(action));
        h.Shortcuts.Icons[action] = Icon;
        Assert.True(h.Controls.Has(action));
        Assert.Empty(h.Touch.Calls);
    }

    [Theory]
    [InlineData(GameAction.OpenInventory)]        // decided: the ☰ menu was found
    [InlineData(GameAction.ToggleSettings)]       // decided: the ☰ menu was found
    [InlineData(GameAction.OpenAutohuntSettings)] // decided: the ☰ menu was found
    public void Has_MenuAction_IsMenuFound(GameAction action)
    {
        var h = new Harness();
        h.Shortcuts.Icons[action] = Icon; // an icon of its own does not count
        Assert.False(h.Controls.Has(action));
        h.Shortcuts.Menu = MenuIcon;
        Assert.True(h.Controls.Has(action));
        Assert.Empty(h.Touch.Calls);
    }

    [Fact]
    public void IsKnownKey_IsAlwaysTrue()
    {
        var h = new Harness();
        Assert.True(h.Controls.IsKnownKey("Jj"));
        Assert.True(h.Controls.IsKnownKey(""));
    }

    // --- held actions -----------------------------------------------------------------------------------------------

    [Fact]
    public void ActionDownUp_Shura_HoldsTheShuraIcon()
    {
        var h = new Harness();
        h.Shortcuts.Icons[GameAction.ShuraKey] = Icon;
        Assert.True(h.Controls.ActionDown(GameAction.ShuraKey));
        h.Controls.ActionUp(GameAction.ShuraKey);
        h.Controls.ActionUp(GameAction.ShuraKey); // nothing held any more
        Assert.Equal(["Down:120,215", "Release"], h.Touch.Calls);
    }

    [Fact]
    public void ActionUp_OtherThanHeld_SendsNothing()
    {
        var h = new Harness();
        h.Shortcuts.Icons[GameAction.ShuraKey] = Icon;
        h.Controls.ActionDown(GameAction.ShuraKey);
        h.Controls.ActionUp(GameAction.Buff);
        h.Controls.SkillUp("1");
        Assert.Equal(["Down:120,215"], h.Touch.Calls);
    }

    [Fact]
    public void ActionDown_Refused_HoldsNothing()
    {
        var h = new Harness();
        h.Shortcuts.Icons[GameAction.ShuraKey] = Icon;
        h.Touch.Result = false;
        Assert.False(h.Controls.ActionDown(GameAction.ShuraKey));
        h.Controls.ActionUp(GameAction.ShuraKey);
        Assert.Equal(["Down:120,215"], h.Touch.Calls);
    }

    [Fact]
    public void ActionDown_MenuAction_IsRefused()
    {
        var h = new Harness();
        h.Shortcuts.Menu = MenuIcon;
        Assert.False(h.Controls.ActionDown(GameAction.OpenInventory));
        Assert.Empty(h.Touch.Calls);
    }

    [Fact]
    public void SkillDownUp_HoldsTheSkillButton()
    {
        var h = new Harness();
        h.Shortcuts.Skills["1"] = SkillButton;
        Assert.True(h.Controls.SkillDown("1"));
        Assert.True(h.Controls.SkillUp("1"));
        Assert.False(h.Controls.SkillUp("1"));
        Assert.Equal(["Down:830,630", "Release"], h.Touch.Calls);
    }

    [Fact]
    public void SecondHold_LetsGoOfTheFirst()
    {
        var h = new Harness();
        h.Shortcuts.Skills["1"] = SkillButton;
        h.Shortcuts.Icons[GameAction.ShuraKey] = Icon;
        h.Controls.SkillDown("1");
        h.Controls.ActionDown(GameAction.ShuraKey);
        h.Controls.SkillUp("1"); // no longer held
        h.Controls.ActionUp(GameAction.ShuraKey);
        Assert.Equal(["Down:830,630", "Release", "Down:120,215", "Release"], h.Touch.Calls);
    }

    [Fact]
    public void ReleaseAll_EndsHeldSkillAndJoystick()
    {
        var h = new Harness();
        h.Shortcuts.Skills["1"] = SkillButton;
        h.Controls.SkillDown("1");
        h.Controls.ReleaseAll();
        h.Controls.SkillUp("1"); // already let go
        Assert.Equal(["Down:830,630", "Release"], h.Touch.Calls);
    }

    [Fact]
    public void ReleaseAll_NothingHeld_StillLiftsTheFinger()
    {
        // A joystick step on the loop thread is a held finger the controls do not track: Stop / F10 must end it too.
        var h = new Harness();
        h.Controls.ReleaseAll();
        Assert.Equal(["Release"], h.Touch.Calls);
    }

    [Fact]
    public async Task ReleaseAll_HoldOnAnotherThread_WaitsUntilTheReleaseIsDone()
    {
        // ReleaseAll from the UI thread; the loop thread's SkillDown must not land between clearing the held press
        // and the Release (it would be lifted while still counted as held).
        var h = new Harness();
        h.Shortcuts.Skills["1"] = SkillButton;
        Task<bool>? hold = null;
        bool finishedDuringRelease = false;
        h.Touch.OnRelease = () =>
        {
            h.Touch.OnRelease = null;
            hold = Task.Run(() => h.Controls.SkillDown("1"));
            finishedDuringRelease = hold.Wait(150);
        };

        h.Controls.ReleaseAll();
        Assert.False(finishedDuringRelease);
        Assert.True(await hold!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(h.Controls.SkillUp("1")); // the hold that came after is still held - and can be let go
        Assert.Equal(["Release", "Down:830,630", "Release"], h.Touch.Calls);
    }

    [Fact]
    public void Release_WithTheGameBehind_StillLiftsTheFinger()
    {
        var h = new Harness();
        h.Shortcuts.Skills["1"] = SkillButton;
        h.Shortcuts.Icons[GameAction.ShuraKey] = Icon;
        h.Controls.SkillDown("1");
        h.Touch.InFront = false;
        Assert.True(h.Controls.SkillUp("1"));
        h.Touch.InFront = true;
        h.Controls.ActionDown(GameAction.ShuraKey);
        h.Touch.InFront = false;
        h.Controls.ActionUp(GameAction.ShuraKey);
        h.Controls.ReleaseAll();
        Assert.Equal(["Down:830,630", "Release", "Down:120,215", "Release", "Release"], h.Touch.Calls);
    }

    // --- taps and skills --------------------------------------------------------------------------------------------

    [Fact] public void Tap_TapsThePoint() => Assert.Equal(["Tap:10,20,60"], Run(c => c.Tap(10, 20)));
    [Fact] public void UseItem_TapsThePoint() => Assert.Equal(["Tap:10,20,60"], Run(c => c.UseItem(10, 20)));

    [Fact]
    public void Tap_UsesTapHoldMs()
    {
        var h = new Harness();
        h.Settings.Phone.TapHoldMs = 90;
        h.Controls.Tap(1, 2);
        Assert.Equal(["Tap:1,2,90"], h.Touch.Calls);
    }

    [Fact]
    public void Tap_ReturnsTheTouchResult()
    {
        var h = new Harness();
        h.Touch.Result = false;
        Assert.False(h.Controls.Tap(1, 2));
    }

    [Fact]
    public void SkillTap_TapsTheSkillButton()
    {
        var h = new Harness();
        h.Shortcuts.Skills["1"] = SkillButton;
        Assert.True(h.Controls.SkillTap("1"));
        Assert.Equal(["Tap:830,630,60"], h.Touch.Calls);
    }

    [Fact]
    public void SkillCtrlTap_IsAPlainSkillTap()
    {
        // No Ctrl on the phone (PlatformProfile.SupportsCtrlSkill false).
        var h = new Harness();
        h.Shortcuts.Skills["2"] = SkillButton;
        Assert.True(h.Controls.SkillCtrlTap("2"));
        Assert.Equal(["Tap:830,630,60"], h.Touch.Calls);
    }

    [Fact]
    public void MissingShortcut_PressReturnsFalseAndInvalidates()
    {
        var h = new Harness();
        Assert.False(h.Controls.Press(GameAction.Buff));
        Assert.False(h.Controls.Press(GameAction.OpenInventory));
        Assert.False(h.Controls.SkillTap("1"));
        Assert.False(h.Controls.SkillDown("1"));
        Assert.False(h.Controls.ActionDown(GameAction.ShuraKey));
        Assert.Empty(h.Touch.Calls);
        Assert.Equal(5, h.Shortcuts.Invalidations);
    }

    // --- walking ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WalkTo_PushesTheJoystickThenSettles()
    {
        // Straight up from the character (500,432) -> stick (0,-r); then TapSettleMs (serialised touches).
        var h = new Harness();
        h.Shortcuts.Joystick = (150, 650, 80);
        Assert.True(h.Controls.WalkTo(500, 100, forceWalk: true));
        Assert.Equal(["Joy:150,650,0,-80,900", "Sleep:150"], h.Touch.Calls);
    }

    [Fact]
    public void Walk_UsesJoystickFoundForThisFrameSize()
    {
        var h = new Harness();
        h.Shortcuts.Joystick = (150, 650, 80);
        h.Controls.WalkTo(900, 432, forceWalk: false); // straight right
        h.Shortcuts.FrameSize = (2000, 1600);
        h.Shortcuts.Joystick = (300, 1300, 160);
        h.Controls.WalkTo(1000, 0, forceWalk: false);  // straight up from (1000,864)
        Assert.Equal(["Joy:150,650,80,0,900", "Sleep:150", "Joy:300,1300,0,-160,900", "Sleep:150"], h.Touch.Calls);
    }

    [Fact]
    public void WalkTo_NoJoystick_FailsAndInvalidates()
    {
        var h = new Harness();
        Assert.False(h.Controls.WalkTo(500, 100, forceWalk: false));
        Assert.Empty(h.Touch.Calls);
        Assert.Equal(1, h.Shortcuts.Invalidations);
    }

    [Fact]
    public void WalkTo_Refused_DoesNotSettle()
    {
        var h = new Harness();
        h.Shortcuts.Joystick = (150, 650, 80);
        h.Touch.Result = false;
        Assert.False(h.Controls.WalkTo(500, 100, forceWalk: false));
        Assert.Equal(["Joy:150,650,0,-80,900"], h.Touch.Calls);
    }

    // --- camera -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Turn_Left_FingerSwipesRight()
    {
        // Provisional (Task 1 confirms): the camera turns left when the finger moves right. 200 ms × 1.0 px/ms.
        Assert.Equal(["Swipe:400,280,600,280,200"], Run(c => c.Turn(TurnDirection.Left, 200)));
    }

    [Fact]
    public void Turn_Right_FingerSwipesLeft() => Assert.Equal(["Swipe:600,280,400,280,200"], Run(c => c.Turn(TurnDirection.Right, 200)));

    [Fact]
    public void Turn_ScalesBySwipePxPerTurnMs()
    {
        var h = new Harness();
        h.Settings.Phone.SwipePxPerTurnMs = 2.0;
        h.Controls.Turn(TurnDirection.Left, 100);
        Assert.Equal(["Swipe:400,280,600,280,100"], h.Touch.Calls);
    }

    [Fact]
    public void BeginTurn_SwipesTheSweepLength_EndTurnIsANoOp()
    {
        var h = new Harness();
        h.Settings.Dungeon.RotateLeftSweepMs = 300;
        Assert.True(h.Controls.BeginTurn(TurnDirection.Left));
        h.Controls.EndTurn(TurnDirection.Left);
        Assert.Equal(["Swipe:350,280,650,280,300"], h.Touch.Calls);
    }

    [Fact]
    public void Turn_NothingToTurn_SendsNothingAndReturnsTrue()
    {
        // A zero-length swipe is a tap on the ground (walk / attack): ms <= 0 or under a pixel sends nothing.
        var h = new Harness();
        Assert.True(h.Controls.Turn(TurnDirection.Left, 0));
        Assert.True(h.Controls.Turn(TurnDirection.Right, -5));
        Assert.True(h.Controls.BeginTurn(TurnDirection.Left)); // RotateLeftSweepMs 0
        h.Settings.Phone.SwipePxPerTurnMs = 0.001;
        Assert.True(h.Controls.Turn(TurnDirection.Left, 500)); // 0.5 px
        Assert.Empty(h.Touch.Calls);
    }

    [Fact]
    public void Turn_LongSwipe_StaysOnTheFrame()
    {
        // 2000 px on a 1000 px frame: the end points are clamped to 0 .. 999.
        Assert.Equal(["Swipe:0,280,999,280,2000"], Run(c => c.Turn(TurnDirection.Left, 2000)));
        Assert.Equal(["Swipe:999,280,0,280,2000"], Run(c => c.Turn(TurnDirection.Right, 2000)));
    }

    [Fact] public void TurnByDrag_SwipesSideways() => Assert.Equal(["Swipe:1,2,51,2,300"], Run(c => c.TurnByDrag(1, 2, 50, 300)));
    [Fact] public void TiltCamera_SwipesVertically() => Assert.Equal(["Swipe:1,2,1,-58,300"], Run(c => c.TiltCamera(1, 2, -60, 300)));

    [Fact]
    public void ZoomOut_OnePinchPerFiveNotches()
    {
        Assert.Equal(["Pinch:7,8,360,96,400", "Sleep:150", "Pinch:7,8,360,96,400", "Sleep:150", "Pinch:7,8,360,96,400"],
            Run(c => c.ZoomOut(7, 8, 15)));
    }

    [Fact] public void ZoomOut_FewNotches_PinchesOnce() => Assert.Equal(["Pinch:7,8,360,96,400"], Run(c => c.ZoomOut(7, 8, 3)));

    [Fact]
    public void ScrollList_Down_FingerSwipesUp()
    {
        // Provisional (Task 1 confirms): negative notches = the list moves down = the finger moves up. 3 × 300 / 3.
        Assert.Equal(["Swipe:7,500,7,200,300"], Run(c => c.ScrollList(7, 500, -3)));
    }

    [Fact] public void ScrollList_Up_FingerSwipesDown() => Assert.Equal(["Swipe:7,500,7,600,300"], Run(c => c.ScrollList(7, 500, 1)));

    // --- the game behind / dry run ----------------------------------------------------------------------------------

    [Fact]
    public void GameNotInFront_SendsNothingAndReturnsFalse()
    {
        var h = new Harness();
        h.Fill();
        h.Touch.InFront = false;
        foreach (var (name, call) in EverySendingMember())
            Assert.False(call(h.Controls), name);
        h.Controls.ActionUp(GameAction.ShuraKey);
        h.Controls.EndTurn(TurnDirection.Left);
        Assert.False(h.Controls.SkillUp("1")); // nothing held
        Assert.Empty(h.Touch.Calls);
    }

    [Fact]
    public void DryRun_SendsNothingAndReturnsTrue()
    {
        // WindowInput in a dry run: every send returns true before the foreground check and before the key is
        // resolved - so here too, even with the game behind and no shortcut known.
        var h = new Harness();
        h.Touch.DryRun = true;
        h.Touch.InFront = false;
        foreach (var (name, call) in EverySendingMember())
            Assert.True(call(h.Controls), name);
        Assert.Empty(h.Touch.Calls);
        Assert.Equal(0, h.Shortcuts.Invalidations);
    }

    [Fact]
    public void DryRun_LettingGo_ReturnsTrueAndSendsNothing()
    {
        // WindowInput.KeyUp returns true in a dry run; nothing went down here either.
        var h = new Harness();
        h.Fill();
        h.Touch.DryRun = true;
        Assert.True(h.Controls.SkillDown("1"));
        Assert.True(h.Controls.SkillUp("1"));
        Assert.True(h.Controls.SkillUp("2"));
        h.Controls.ActionDown(GameAction.ShuraKey);
        h.Controls.ActionUp(GameAction.ShuraKey);
        Assert.Empty(h.Touch.Calls);
    }

    [Fact]
    public void Queries_AskTheTouchInput()
    {
        var h = new Harness();
        h.Touch.InFront = false;
        Assert.False(h.Controls.IsGameInFront);
        Assert.False(h.Controls.TryBringGameToFront());
        Assert.False(h.Controls.IsDryRun);
        h.Touch.InFront = true;
        h.Touch.DryRun = true;
        Assert.True(h.Controls.IsGameInFront);
        Assert.True(h.Controls.TryBringGameToFront());
        Assert.True(h.Controls.IsDryRun);
        Assert.Equal(["Front", "Front"], h.Touch.Calls);
    }

    // --- JoystickMath -----------------------------------------------------------------------------------------------

    [Fact] public void JoystickMath_StraightUp_IsMinusRadiusY() => Assert.Equal((0, -80), JoystickMath.ToStick(1000, 800, 500, 100, 80));
    [Fact] public void JoystickMath_ZeroVector_IsZero() => Assert.Equal((0, 0), JoystickMath.ToStick(1000, 800, 500, 432, 80));
    [Fact] public void JoystickMath_Diagonal_HasLengthRadius() => Assert.Equal((-57, 57), JoystickMath.ToStick(1000, 800, 400, 532, 80));

    // --- helpers ----------------------------------------------------------------------------------------------------

    private static List<(string Name, Func<IGameControls, bool> Call)> EverySendingMember() =>
    [
        .. Enum.GetValues<GameAction>().Select(a => ($"Press({a})", (Func<IGameControls, bool>)(c => c.Press(a)))),
        .. Enum.GetValues<GameAction>().Select(a => ($"ActionDown({a})", (Func<IGameControls, bool>)(c => c.ActionDown(a)))),
        ("Tap", c => c.Tap(1, 2)),
        ("UseItem", c => c.UseItem(1, 2)),
        ("SkillTap", c => c.SkillTap("1")),
        ("SkillCtrlTap", c => c.SkillCtrlTap("1")),
        ("SkillDown", c => c.SkillDown("1")),
        ("WalkTo", c => c.WalkTo(500, 100, false)),
        ("Turn", c => c.Turn(TurnDirection.Left, 100)),
        ("BeginTurn", c => c.BeginTurn(TurnDirection.Left)),
        ("TurnByDrag", c => c.TurnByDrag(1, 2, 50, 300)),
        ("TiltCamera", c => c.TiltCamera(1, 2, 60, 300)),
        ("ZoomOut", c => c.ZoomOut(1, 2, 15)),
        ("ScrollList", c => c.ScrollList(1, 2, -3)),
    ];

    private static List<string> Run(Func<IGameControls, bool> act)
    {
        var h = new Harness();
        Assert.True(act(h.Controls));
        return h.Touch.Calls;
    }

    private sealed class Harness
    {
        public AppSettings Settings { get; } = new();
        public RecordingTouch Touch { get; }
        public FakeShortcuts Shortcuts { get; } = new();
        public TouchControls Controls { get; }

        public Harness()
        {
            Settings.Dungeon.RotateLeftSweepMs = 0;
            Touch = new RecordingTouch();
            Controls = new TouchControls(Settings, Touch, Shortcuts, ms => Touch.Calls.Add($"Sleep:{ms}"));
        }

        /// <summary>Every shortcut known.</summary>
        public void Fill()
        {
            foreach (var a in Enum.GetValues<GameAction>())
                Shortcuts.Icons[a] = Icon;
            Shortcuts.Menu = MenuIcon;
            Shortcuts.Skills["1"] = SkillButton;
            Shortcuts.Joystick = (150, 650, 80);
        }
    }

    /// <summary>Records every gesture ("Tap:x,y,ms"); returns <see cref="Result"/>.</summary>
    private sealed class RecordingTouch : ITouchInput
    {
        public List<string> Calls { get; } = [];
        public bool Result { get; set; } = true;
        public bool InFront { get; set; } = true;
        public bool DryRun { get; set; }

        public bool IsDryRun => DryRun;
        public bool IsGameInFront => InFront;

        public bool BringGameToFront()
        {
            Calls.Add("Front");
            return InFront;
        }

        public bool Tap(int x, int y, int holdMs) => Record($"Tap:{x},{y},{holdMs}");
        public bool Swipe(int x1, int y1, int x2, int y2, int durationMs) => Record($"Swipe:{x1},{y1},{x2},{y2},{durationMs}");
        public bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs) => Record($"Pinch:{cx},{cy},{fromGap},{toGap},{durationMs}");
        public bool Joystick(int cx, int cy, int dx, int dy, int durationMs) => Record($"Joy:{cx},{cy},{dx},{dy},{durationMs}");
        public bool PressDown(int x, int y) => Record($"Down:{x},{y}");
        public void Release()
        {
            Calls.Add("Release");
            OnRelease?.Invoke();
        }

        /// <summary>Runs inside <see cref="Release"/> (to race another thread against it).</summary>
        public Action? OnRelease { get; set; }

        private bool Record(string call)
        {
            Calls.Add(call);
            return Result;
        }
    }

    private sealed class FakeShortcuts : IShortcutSource
    {
        public Dictionary<GameAction, PixelRect> Icons { get; } = [];
        public Dictionary<string, PixelRect> Skills { get; } = [];
        public (int Cx, int Cy, int Radius)? Joystick { get; set; }
        public PixelRect? Menu { get; set; }
        public (int Width, int Height) FrameSize { get; set; } = (1000, 800);
        public int Invalidations { get; private set; }

        public PixelRect? Find(GameAction action) => Icons.TryGetValue(action, out var r) ? r : null;
        public PixelRect? Skill(string skill) => Skills.TryGetValue(skill, out var r) ? r : null;
        public void Invalidate() => Invalidations++;
    }
}
