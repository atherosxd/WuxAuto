using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

[Collection("AppPaths")]
public class PlatformProfileTests
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
            Assert.Equal(0.02, p.FloatingX);
            Assert.Equal(0.4, p.FloatingY);

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
