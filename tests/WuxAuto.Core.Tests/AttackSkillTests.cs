using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Phone;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The phone's skill presses use the attack skill icons (phone_attack1..7, the user's crops, 2026-10-29) wherever they
/// are: a hotbar slot or a round button of the skill wheel round the sword. The skill name is ignored; the first icon
/// in list order wins; none = nothing sent, and a skill spam on none costs no search per press.
/// </summary>
public class AttackSkillTests : AppPathsScope
{
    // A hotbar slot (the bar's slots are ~80 px apart from x 646, y 988 at 1920x1080) and a wheel button
    // (the round buttons round the sword, ~115 px across; this one is the upper left of the sword, measured on
    // skillwheel_slot1_1920x1080.png: centre ~1608,588).
    const int SlotX = 726, SlotY = 986;
    const int WheelCx = 1608, WheelCy = 588, WheelDiameter = 114;

    ScreenAnalyzer NewAnalyzer()
    {
        AppPaths.Configure(TempRoot(), PhonePilot.PhoneAssets);
        return new ScreenAnalyzer(new AppSettings(), new FakeOcrEngine());
    }

    /// <summary>
    /// The crop as the wheel draws it (skillwheel_slot1: the hotbar picture x1.66, centred, cut to a circle): only the
    /// pixels inside the circle, the rest stays background.
    /// </summary>
    static PixelRect PasteRound(Mat frame, string template, int cx, int cy, int diameter)
    {
        using var crop = CvInvoke.Imread(Path.Combine(PhonePilot.PhoneAssets, template + ".png"), ImreadModes.ColorBgr);
        using var big = new Mat();
        CvInvoke.Resize(crop, big, new Size((int)Math.Round(crop.Width * 1.66), (int)Math.Round(crop.Height * 1.66)), 0, 0, Inter.Cubic);
        diameter = Math.Min(diameter, Math.Min(big.Width, big.Height));
        using var scaled = new Mat(big, new Rectangle((big.Width - diameter) / 2, (big.Height - diameter) / 2, diameter, diameter));
        using var bgra = new Mat();
        CvInvoke.CvtColor(scaled, bgra, ColorConversion.Bgr2Bgra);
        using var mask = new Mat(diameter, diameter, DepthType.Cv8U, 1);
        mask.SetTo(new MCvScalar(0));
        CvInvoke.Circle(mask, new Point(diameter / 2, diameter / 2), diameter / 2, new MCvScalar(255), -1);
        var box = new PixelRect(cx - diameter / 2, cy - diameter / 2, diameter, diameter);
        using (var target = new Mat(frame, new Rectangle(box.X, box.Y, box.Width, box.Height)))
            bgra.CopyTo(target, mask);
        return box;
    }

    /// <summary>A grey frame with the rest of the HUD where the game draws it at 1920x1080 (no attack icon), so only the attack icons can be absent.</summary>
    static Mat Hud()
    {
        var frame = PhoneFrames.Grey();
        foreach (var (template, x, y) in new[]
                 {
                     ("phone_teleportscroll", 44, 724), ("phone_pickup", 76, 664), ("phone_joystick", 187, 741),
                     ("phone_autobuff", 1804, 630), ("phone_sword", 1595, 755), ("phone_runskill", 1388, 718),
                     ("phone_skill1", 1455, 607), ("phone_menu", 1158, 2),
                 })
            PhoneFrames.Paste(frame, template, x, y);
        return frame;
    }

    static void AssertAt(PixelRect expected, PixelRect? found, string what)
    {
        Assert.True(found.HasValue, $"{what} not found");
        var f = found!.Value;
        Assert.True(Math.Abs(f.CenterX - expected.CenterX) <= 5 && Math.Abs(f.CenterY - expected.CenterY) <= 5,
            $"{what}: expected near {expected}, found {f}");
    }

    [Theory]
    [InlineData("phone_attack1")]
    [InlineData("phone_attack4")]
    [InlineData("phone_attack7")]
    public void OnTheHotbar_SkillTapTapsIt_WhateverTheSkillName(string template)
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var touch = new RecordingTouch();
        var controls = new TouchControls(new AppSettings(), touch, map, sleep: _ => { });
        using var frame = Hud();
        var placed = PhoneFrames.Paste(frame, template, SlotX, SlotY);

        map.Refresh(frame);

        AssertAt(placed, map.AttackSkill("1"), template);
        Assert.Equal(map.AttackSkill("1"), map.AttackSkill("2")); // the name is ignored
        Assert.True(controls.SkillTap("3"));
        Assert.Single(touch.Taps);
        Assert.InRange(touch.Taps[0].X, placed.CenterX - 5, placed.CenterX + 5);
        Assert.InRange(touch.Taps[0].Y, placed.CenterY - 5, placed.CenterY + 5);
    }

    [Theory]
    [InlineData("phone_attack1")]
    [InlineData("phone_attack3")]
    [InlineData("phone_attack6")]
    public void OnTheWheel_RoundButton_SkillDownTapsIt(string template)
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var touch = new RecordingTouch();
        var tapMode = new AppSettings { Phone = { AttackMode = PhoneAttackMode.Tap } };
        var controls = new TouchControls(tapMode, touch, map, sleep: _ => { });
        using var frame = Hud();
        var placed = PasteRound(frame, template, WheelCx, WheelCy, WheelDiameter);

        map.Refresh(frame);

        AssertAt(placed, map.AttackSkill("1"), template);
        Assert.True(controls.SkillDown("1")); // no hold on the phone: one plain tap
        Assert.Single(touch.Taps);
        Assert.InRange(touch.Taps[0].X, placed.CenterX - 5, placed.CenterX + 5);
        Assert.True(controls.SkillUp("1"));
        Assert.DoesNotContain(touch.Calls, c => c.StartsWith("Down "));
    }

    [Fact]
    public void Several_TheFirstInListOrderWins()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        using var frame = Hud();
        PhoneFrames.Paste(frame, "phone_attack5", SlotX, SlotY);
        var second = PasteRound(frame, "phone_attack2", WheelCx, WheelCy, WheelDiameter);
        PhoneFrames.Paste(frame, "phone_attack7", SlotX + 160, SlotY);

        map.Refresh(frame);

        AssertAt(second, map.AttackSkill("1"), "phone_attack2 (before 5 and 7)");
    }

    [Fact]
    public void None_SkillTapSendsNothing_AndASpamCostsNoSearchPerPress()
    {
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer, clock: () => now);
        var touch = new RecordingTouch();
        var controls = new TouchControls(new AppSettings(), touch, map, sleep: _ => { });
        using var frame = Hud();
        map.Refresh(frame);
        Assert.Null(map.AttackSkill("1"));
        int searches = map.SearchCount;

        // An LOD skill spam: a press every 0.3 s for 9 s, a Refresh between presses.
        for (int i = 0; i < 30; i++)
        {
            Assert.False(controls.SkillTap("1"));
            Assert.False(controls.SkillDown("1"));
            now += 300;
            map.Refresh(frame);
        }

        Assert.Empty(touch.Calls);
        Assert.Equal(searches, map.SearchCount);

        // Past the cool-down, without a press: nothing is searched in the background.
        now += ShortcutMap.AbsentRetryMs;
        map.Refresh(frame);
        Assert.Equal(searches, map.SearchCount);

        // A press past the cool-down looks once more for every attack icon - once, not per press.
        Assert.False(controls.SkillTap("1"));
        map.Refresh(frame);
        Assert.False(controls.SkillTap("1"));
        map.Refresh(frame);
        Assert.Equal(searches + ShortcutMap.AttackTemplates.Count, map.SearchCount);
    }

    [Fact]
    public void None_ThenPutOnTheBar_FoundByThePressAfterTheCooldown()
    {
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer, clock: () => now);
        var touch = new RecordingTouch();
        var controls = new TouchControls(new AppSettings(), touch, map, sleep: _ => { });
        using (var empty = Hud())
            map.Refresh(empty);

        using var frame = Hud();
        var placed = PhoneFrames.Paste(frame, "phone_attack3", SlotX, SlotY);
        now += ShortcutMap.AbsentRetryMs;
        Assert.False(controls.SkillTap("1"));
        map.Refresh(frame);

        Assert.True(controls.SkillTap("1"));
        Assert.InRange(touch.Taps.Single().X, placed.CenterX - 5, placed.CenterX + 5);
    }

    [Fact]
    public void AttackIconsAreLookedForOnTheHotbarAndTheWheelOnly()
    {
        foreach (string t in ShortcutMap.AttackTemplates)
        {
            var zones = ShortcutMap.ZonesFor(t, 1920, 1080);
            Assert.Equal(2, zones.Count);
            Assert.Contains(zones, z => z.Y >= 960 && z.X > 500 && z.Right < 1400); // the hotbar
            Assert.Contains(zones, z => z.X >= 1200 && z.Right == 1920);           // the cluster round the sword
        }

        Assert.Equal(["phone_attack1", "phone_attack2", "phone_attack3", "phone_attack4", "phone_attack5", "phone_attack6", "phone_attack7"],
            ShortcutMap.AttackTemplates);
    }

    [Fact]
    public void ScriptedSource_WithoutAttackIcons_KeepsTheNamedSkill()
    {
        // The interface default (a source without attack icons, e.g. the tests' fakes) is the named skill button.
        IShortcutSource source = new ScriptedShortcuts { Skills = { ["1"] = new PixelRect(10, 10, 20, 20) } };
        Assert.Equal(new PixelRect(10, 10, 20, 20), source.AttackSkill("1"));
        Assert.Null(source.AttackSkill("2"));
    }
}
