using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

public class PlatformProfileTests : AppPathsScope
{
    [Fact]
    public void Desktop_IsTheDefault()
    {
        var settings = new AppSettings();
        using var analyzer = new ScreenAnalyzer(settings, new FakeOcrEngine());
        var pilot = new AutomationPilot(settings, analyzer, new KeyboardMouseControls(settings, new CountingInput()));
        Assert.Same(PlatformProfile.Desktop, pilot.Profile);
        Assert.True(pilot.Profile.SupportsCtrlSkill);
        Assert.True(pilot.Profile.SupportsAutoConfirm);
        Assert.False(pilot.Profile.TeleportViaShortcut);
    }

    [Fact]
    public void Pilot_TakesAGivenProfile()
    {
        var settings = new AppSettings();
        using var analyzer = new ScreenAnalyzer(settings, new FakeOcrEngine());
        var pilot = new AutomationPilot(settings, analyzer, new KeyboardMouseControls(settings, new CountingInput()), PlatformProfile.Phone);
        Assert.Same(PlatformProfile.Phone, pilot.Profile);
        Assert.True(pilot.Profile.TeleportViaShortcut);
        Assert.False(pilot.Profile.SupportsCtrlSkill);
    }

    [Fact]
    public void KeyboardMouseControls_HasNoTeleportScroll()
    {
        var settings = new AppSettings();
        var input = new CountingInput();
        var controls = new KeyboardMouseControls(settings, input);
        Assert.False(controls.Has(GameAction.TeleportScroll));
        Assert.False(controls.Press(GameAction.TeleportScroll));
        Assert.Equal(0, input.Sent);
    }

    [Fact]
    public void PhoneSettings_LoadsDefaultsFromAnOldFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(AppPaths.SettingsFile, "{ \"Version\": 21 }");
            var p = SettingsStore.Load().Phone;
            Assert.Equal(60, p.TapHoldMs);
            Assert.Equal(100, p.TapSettleMs);
            Assert.Equal(900, p.JoystickStepMs);
            Assert.Equal(0.5, p.SwipePxPerTurnMs);
            Assert.Equal(720, p.PinchFromGap);
            Assert.Equal(192, p.PinchToGap);
            Assert.Equal(400, p.PinchMs);
            Assert.Equal(170, p.ListSwipePx);
            Assert.Equal(0.40, p.FloatingX);
            Assert.Equal(0.03, p.FloatingY);

            var s = new AppSettings();
            s.Phone.FloatingX = 0.5;
            SettingsStore.Save(s);
            Assert.Equal(0.5, SettingsStore.Load().Phone.FloatingX);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Normalize_ClampsEveryPhoneSetting_DefaultsUnchanged()
    {
        // M4 review minor 8: a hand-edited file must not divide by zero (TurnDragPxPerMs 0) or send empty gestures.
        var defaults = new AppSettings();
        SettingsStore.Normalize(defaults);
        var d = new PhoneSettings();
        var n = defaults.Phone;
        Assert.Equal((d.TapHoldMs, d.AttackTapEveryMs, d.AttackMode, d.TapSettleMs, d.JoystickStepMs, d.SwipePxPerTurnMs),
            (n.TapHoldMs, n.AttackTapEveryMs, n.AttackMode, n.TapSettleMs, n.JoystickStepMs, n.SwipePxPerTurnMs));
        Assert.Equal((d.TurnDragMinPx, d.TurnDragEndHoldMs, d.TurnDragPxPerMs, d.TurnDragMinMs, d.PinchFromGap, d.PinchToGap),
            (n.TurnDragMinPx, n.TurnDragEndHoldMs, n.TurnDragPxPerMs, n.TurnDragMinMs, n.PinchFromGap, n.PinchToGap));
        Assert.Equal((d.PinchMs, d.ListSwipePx, d.FloatingX, d.FloatingY), (n.PinchMs, n.ListSwipePx, n.FloatingX, n.FloatingY));

        var s = new AppSettings();
        var p = s.Phone;
        p.TapHoldMs = 0;
        p.AttackTapEveryMs = 5;
        p.AttackMode = (PhoneAttackMode)42;
        p.TapSettleMs = -1;
        p.JoystickStepMs = 0;
        p.SwipePxPerTurnMs = double.NaN;
        p.TurnDragMinPx = 0;
        p.TurnDragEndHoldMs = -5;
        p.TurnDragPxPerMs = 0;
        p.TurnDragMinMs = 0;
        p.PinchFromGap = 0;
        p.PinchToGap = -3;
        p.PinchMs = 0;
        p.ListSwipePx = 0;
        p.FloatingX = 7;
        p.FloatingY = double.NaN;

        SettingsStore.Normalize(s);

        Assert.Equal(10, p.TapHoldMs);
        Assert.Equal(50, p.AttackTapEveryMs);
        Assert.Equal(PhoneAttackMode.Hold, p.AttackMode);
        Assert.Equal(0, p.TapSettleMs);
        Assert.Equal(100, p.JoystickStepMs);
        Assert.Equal(0.5, p.SwipePxPerTurnMs);
        Assert.Equal(1, p.TurnDragMinPx);
        Assert.Equal(0, p.TurnDragEndHoldMs);
        Assert.True(p.TurnDragPxPerMs > 0);
        Assert.Equal(10, p.TurnDragMinMs);
        Assert.Equal(20, p.PinchFromGap);
        Assert.Equal(1, p.PinchToGap);
        Assert.Equal(50, p.PinchMs);
        Assert.Equal(10, p.ListSwipePx);
        Assert.Equal(1, p.FloatingX);
        Assert.Equal(0.03, p.FloatingY);

        p.AttackTapEveryMs = 99_999;
        SettingsStore.Normalize(s);
        Assert.Equal(2000, p.AttackTapEveryMs);
    }

    /// <summary>Counts what reaches the input; every call succeeds.</summary>
    private sealed class CountingInput : IGameInput
    {
        public int Sent { get; private set; }
        public bool IsDryRun => false;
        public bool IsTargetForeground => true;
        public bool TryFocus(int timeoutMs = 1200) { Sent++; return true; }
        public bool KeyTap(string keyName) { Sent++; return true; }
        public bool KeyDown(string keyName) { Sent++; return true; }
        public bool KeyUp(string keyName) { Sent++; return true; }
        public bool Click(int clientX, int clientY, MouseButton button = MouseButton.Left) { Sent++; return true; }
        public bool CtrlClick(int clientX, int clientY) { Sent++; return true; }
        public bool CtrlKeyTap(string keyName) { Sent++; return true; }
        public bool CtrlWheel(int clientX, int clientY, int notches) { Sent++; return true; }
        public bool Drag(int fromX, int fromY, int toX, int toY, int durationMs = 260) { Sent++; return true; }
        public bool Wheel(int clientX, int clientY, int notches) { Sent++; return true; }
    }
}
