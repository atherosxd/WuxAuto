using System.Drawing;
using System.Text.Json;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Phone;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The phone templates (src\WuxAuto.Mobile\Assets\phone, cropped from the MuMu frames at 1920x1080) pasted onto a
/// synthetic frame where the game puts them, at 1920x1080 and scaled to the Xiaomi's 2608x1200 (the HUD scales with
/// the height: left-hand items keep their distance from the left edge, right-hand ones from the right edge).
/// </summary>
public class ShortcutMapTests : AppPathsScope
{
    // Where each crop was cut from (top-left at 1920x1080) and which edge it sits on.
    static readonly (string Template, int X, int Y, PhoneAnchor Anchor)[] Hud =
    [
        ("phone_teleportscroll", 44, 724, PhoneAnchor.Left),
        ("phone_pickup", 76, 664, PhoneAnchor.Left),
        ("phone_joystick", 187, 741, PhoneAnchor.Left),
        ("phone_autobuff", 1804, 630, PhoneAnchor.Right),
        ("phone_sword", 1595, 755, PhoneAnchor.Right),
        ("phone_runskill", 1388, 718, PhoneAnchor.Right),
        ("phone_skill1", 1455, 607, PhoneAnchor.Right),
        ("phone_menu", 1158, 2, PhoneAnchor.Centre),
        ("phone_autohunt", 137, 977, PhoneAnchor.Left), // HUNT, the yellow swords on the ring (cropped 2026-10-30)
    ];

    static string PhoneAssets => Path.Combine(AppContext.BaseDirectory, "Assets", "phone");

    ScreenAnalyzer NewAnalyzer()
    {
        AppPaths.Configure(TempRoot(), PhoneAssets);
        return new ScreenAnalyzer(new AppSettings(), new FakeOcrEngine());
    }

    /// <summary>A noisy grey frame with every HUD crop pasted at its place for this size, moved by <paramref name="shiftX"/> px.</summary>
    static (Mat Frame, Dictionary<string, PixelRect> Placed) HudFrame(int w, int h, int shiftX = 0)
    {
        var frame = new Mat(h, w, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(110, 110, 110, 255));
        using (var noise = new Mat(h, w, DepthType.Cv8U, 4))
        {
            CvInvoke.Randu(noise, new MCvScalar(0, 0, 0, 0), new MCvScalar(40, 40, 40, 0));
            CvInvoke.Add(frame, noise, frame);
        }

        double s = h / 1080.0;
        var placed = new Dictionary<string, PixelRect>();
        foreach (var (name, x, y, anchor) in Hud)
        {
            using var crop = CvInvoke.Imread(Path.Combine(PhoneAssets, name + ".png"), ImreadModes.ColorBgr);
            var size = new Size((int)Math.Round(crop.Width * s), (int)Math.Round(crop.Height * s));
            using var scaled = new Mat();
            CvInvoke.Resize(crop, scaled, size, 0, 0, Inter.Area);
            using var bgra = new Mat();
            CvInvoke.CvtColor(scaled, bgra, ColorConversion.Bgr2Bgra);

            var box = PhoneLayout.ToPixels(x, y, crop.Width, crop.Height, anchor, w, h);
            box = box with { X = box.X + shiftX, Width = size.Width, Height = size.Height };
            using (var target = new Mat(frame, new Rectangle(box.X, box.Y, box.Width, box.Height)))
                bgra.CopyTo(target);
            placed[name] = box;
        }

        return (frame, placed);
    }

    static void AssertAt(PixelRect expected, PixelRect? found, string what)
    {
        Assert.True(found.HasValue, $"{what} not found");
        var f = found!.Value;
        Assert.True(Math.Abs(f.CenterX - expected.CenterX) <= 4 && Math.Abs(f.CenterY - expected.CenterY) <= 4,
            $"{what}: expected near {expected}, found {f}");
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2608, 1200)]
    public void FindsEachShortcutOnRingFrame(int w, int h)
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (frame, placed) = HudFrame(w, h);
        using (frame)
            map.Refresh(frame);

        Assert.Equal((w, h), map.FrameSize);
        AssertAt(placed["phone_teleportscroll"], map.Find(GameAction.TeleportScroll), "TeleportScroll");
        AssertAt(placed["phone_pickup"], map.Find(GameAction.Loot), "Loot");
        AssertAt(placed["phone_autobuff"], map.Find(GameAction.Buff), "Buff");
        AssertAt(placed["phone_sword"], map.Find(GameAction.TargetNearest), "TargetNearest");
        AssertAt(placed["phone_runskill"], map.Find(GameAction.RunSkillKey), "RunSkillKey");
        AssertAt(placed["phone_menu"], map.Menu, "Menu");
        AssertAt(placed["phone_skill1"], map.Skill("1"), "Skill 1");

        var joy = placed["phone_joystick"];
        Assert.NotNull(map.Joystick);
        var (cx, cy, radius) = map.Joystick!.Value;
        Assert.InRange(cx, joy.CenterX - 4, joy.CenterX + 4);
        Assert.InRange(cy, joy.CenterY - 4, joy.CenterY + 4);
        // 106 px at 1080 (Task 1), scaled with the height.
        Assert.InRange(radius, (int)(106 * h / 1080.0) - 4, (int)(106 * h / 1080.0) + 4);

        // HUNT is on these frames (cropped 2026-10-30); no template yet for the rest (not on the bar / no skill there).
        AssertAt(placed["phone_autohunt"], map.Find(GameAction.ToggleAutohunt), "ToggleAutohunt");
        Assert.Null(map.Find(GameAction.ShuraKey));
        Assert.Null(map.Find(GameAction.StopAttack));
        Assert.Null(map.Skill("2"));
        Assert.Null(map.Skill("3"));
        Assert.Null(map.Skill("4"));
    }

    /// <summary>Pastes the crop scaled by <paramref name="s"/> with its top-left at (x, y); <paramref name="grey"/> = drawn without colour.</summary>
    static PixelRect PasteScaled(Mat frame, string template, int x, int y, double s, bool grey = false)
    {
        using var crop = CvInvoke.Imread(Path.Combine(PhoneAssets, template + ".png"), ImreadModes.ColorBgr);
        var size = new Size((int)Math.Round(crop.Width * s), (int)Math.Round(crop.Height * s));
        using var scaled = new Mat();
        CvInvoke.Resize(crop, scaled, size, 0, 0, Inter.Area);
        if (grey)
        {
            using var g = new Mat();
            CvInvoke.CvtColor(scaled, g, ColorConversion.Bgr2Gray);
            CvInvoke.CvtColor(g, scaled, ColorConversion.Gray2Bgr);
        }

        using var bgra = new Mat();
        CvInvoke.CvtColor(scaled, bgra, ColorConversion.Bgr2Bgra);
        using (var target = new Mat(frame, new Rectangle(x, y, size.Width, size.Height)))
            bgra.CopyTo(target);
        return new PixelRect(x, y, size.Width, size.Height);
    }

    [Fact]
    public void XiaomiLayout_2608x1200_FindsEveryShortcutWhereThePhoneDrawsIt()
    {
        // Top-left corners measured on the owner's Xiaomi 17 Pro Max town frame (2026-10-30); the HUD scales with the height
        // (every hit there was at x1.11 = 1200/1080). The ☰ keeps its distance from the right edge, not the middle.
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        using var frame = new Mat(1200, 2608, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(110, 110, 110, 255));
        double s = 1200 / 1080.0;
        var scroll = PasteScaled(frame, "phone_teleportscroll", 225, 663, s);
        var pickup = PasteScaled(frame, "phone_pickup", 85, 738, s);
        var joystick = PasteScaled(frame, "phone_joystick", 209, 823, s);
        var hunt = PasteScaled(frame, "phone_autohunt", 151, 1084, s);
        PasteScaled(frame, "phone_autohunt", 94, 1039, s, grey: true); // the white AUTO BUFF copy on the ring: same swords, no yellow
        var buff = PasteScaled(frame, "phone_autobuff", 2479, 700, s);
        var sword = PasteScaled(frame, "phone_sword", 2247, 839, s);
        var run = PasteScaled(frame, "phone_runskill", 2018, 797, s);
        var menu = PasteScaled(frame, "phone_menu", 1761, 4, s);

        map.Refresh(frame);

        AssertAt(scroll, map.Find(GameAction.TeleportScroll), "TeleportScroll");
        AssertAt(pickup, map.Find(GameAction.Loot), "Loot");
        AssertAt(hunt, map.Find(GameAction.ToggleAutohunt), "ToggleAutohunt (the yellow swords, not the white ones)");
        AssertAt(buff, map.Find(GameAction.Buff), "Buff");
        AssertAt(sword, map.Find(GameAction.TargetNearest), "TargetNearest");
        AssertAt(run, map.Find(GameAction.RunSkillKey), "RunSkillKey");
        AssertAt(menu, map.Menu, "Menu");
        Assert.NotNull(map.Joystick);
        Assert.InRange(map.Joystick!.Value.Cx, joystick.CenterX - 4, joystick.CenterX + 4);
    }

    [Fact]
    public void Autohunt_OnlyTheWhiteSwords_IsNotHunt()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        using var frame = new Mat(1080, 1920, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(110, 110, 110, 255));
        PasteScaled(frame, "phone_autohunt", 137, 977, 1.0, grey: true);

        map.Refresh(frame);

        Assert.Null(map.Find(GameAction.ToggleAutohunt));
    }

    [Fact]
    public void MenuZone_IsAnchoredToTheRightEdge()
    {
        // 1920x1080: x 600-1500; 2608x1200: the same distance from the right edge, scaled (1141-2141).
        var mumu = ShortcutMap.ZonesFor(ShortcutMap.MenuTemplate, 1920, 1080).Single();
        var xiaomi = ShortcutMap.ZonesFor(ShortcutMap.MenuTemplate, 2608, 1200).Single();
        Assert.Equal(new PixelRect(600, 0, 900, 110), mumu);
        Assert.Equal(2608 - (int)Math.Round(1320 * 1200 / 1080.0), xiaomi.X);
        Assert.Contains(1794, Enumerable.Range(xiaomi.X, xiaomi.Width)); // the ☰'s centre on the Xiaomi
    }

    [Fact]
    public void SameSize_RefreshIsANoOp()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (first, _) = HudFrame(1920, 1080);
        using (first)
            map.Refresh(first);
        var before = map.Find(GameAction.Buff);

        var (moved, _) = HudFrame(1920, 1080, shiftX: -30);
        using (moved)
            map.Refresh(moved);

        Assert.Equal(before, map.Find(GameAction.Buff));
    }

    [Fact]
    public void SizeChange_ForgetsPositionsAndSearchesAgain()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (small, _) = HudFrame(1920, 1080);
        using (small)
            map.Refresh(small);
        Assert.NotNull(map.Find(GameAction.Buff));

        var (big, placed) = HudFrame(2608, 1200);
        using (big)
            map.Refresh(big);

        Assert.Equal((2608, 1200), map.FrameSize);
        AssertAt(placed["phone_autobuff"], map.Find(GameAction.Buff), "Buff after the size change");
        AssertAt(placed["phone_teleportscroll"], map.Find(GameAction.TeleportScroll), "TeleportScroll after the size change");
    }

    [Fact]
    public void SizeChange_WithTheIconsGone_ForgetsTheOldPositions()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (hud, _) = HudFrame(1920, 1080);
        using (hud)
            map.Refresh(hud);
        Assert.NotNull(map.Find(GameAction.Buff));

        using var blank = new Mat(1200, 2608, DepthType.Cv8U, 4);
        blank.SetTo(new MCvScalar(110, 110, 110, 255));
        map.Refresh(blank);

        Assert.Null(map.Find(GameAction.Buff));
        Assert.Null(map.Joystick);
        Assert.Null(map.Menu);
    }

    [Fact]
    public void Invalidate_SearchesAgainOnNextRefresh()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (first, _) = HudFrame(1920, 1080);
        using (first)
            map.Refresh(first);
        Assert.NotNull(map.Find(GameAction.Buff));

        map.Invalidate();
        Assert.Null(map.Find(GameAction.Buff));

        var (moved, placed) = HudFrame(1920, 1080, shiftX: -30);
        using (moved)
            map.Refresh(moved);

        AssertAt(placed["phone_autobuff"], map.Find(GameAction.Buff), "Buff after Invalidate");
    }

    [Fact]
    public void MissOfOne_KeepsOtherPositions()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (hud, _) = HudFrame(1920, 1080);
        using (hud)
        {
            map.Refresh(hud);
            var joystick = map.Joystick;
            var loot = map.Find(GameAction.Loot);
            var menu = map.Menu;
            int searches = map.SearchCount;

            map.InvalidateSkill("1");
            Assert.Null(map.Skill("1"));
            Assert.Equal(joystick, map.Joystick);
            Assert.Equal(loot, map.Find(GameAction.Loot));
            Assert.Equal(menu, map.Menu);

            map.Refresh(hud);

            Assert.NotNull(map.Skill("1"));
            Assert.Equal(searches + 1, map.SearchCount); // only skill 1 was searched again
            Assert.Equal(joystick, map.Joystick);
            Assert.Equal(loot, map.Find(GameAction.Loot));
            Assert.Equal(menu, map.Menu);
        }
    }

    [Fact]
    public void JoystickAndMenuMisses_SearchOnlyThem()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (hud, _) = HudFrame(1920, 1080);
        using (hud)
        {
            map.Refresh(hud);
            int searches = map.SearchCount;

            map.InvalidateJoystick();
            map.InvalidateMenu();
            Assert.Null(map.Joystick);
            Assert.Null(map.Menu);
            Assert.NotNull(map.Find(GameAction.Buff));

            map.Refresh(hud);

            Assert.NotNull(map.Joystick);
            Assert.NotNull(map.Menu);
            Assert.Equal(searches + 2, map.SearchCount);
        }
    }

    /// <summary>A frame with the HUD minus one crop (painted over with the background grey).</summary>
    static Mat HudFrameWithout(string template, out PixelRect place)
    {
        var (frame, placed) = HudFrame(1920, 1080);
        place = placed[template];
        using var hole = new Mat(frame, new Rectangle(place.X, place.Y, place.Width, place.Height));
        hole.SetTo(new MCvScalar(110, 110, 110, 255));
        return frame;
    }

    [Fact]
    public void AbsentTemplate_NotSearchedAgainWithinCooldown()
    {
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer, clock: () => now);
        using (var without = HudFrameWithout("phone_autobuff", out _))
            map.Refresh(without);
        Assert.Null(map.Find(GameAction.Buff));
        int searches = map.SearchCount;

        var (hud, _) = HudFrame(1920, 1080);
        using (hud)
        {
            now += ShortcutMap.AbsentRetryMs - 1;
            map.Refresh(hud);
        }

        Assert.Null(map.Find(GameAction.Buff));
        Assert.Equal(searches, map.SearchCount);
        Assert.NotNull(map.Find(GameAction.Loot));
    }

    [Fact]
    public void AbsentTemplate_SearchedAgainAfterCooldown()
    {
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer, clock: () => now);
        using (var without = HudFrameWithout("phone_autobuff", out _))
            map.Refresh(without);
        Assert.Null(map.Find(GameAction.Buff));
        int searches = map.SearchCount;
        var loot = map.Find(GameAction.Loot);

        var (hud, placed) = HudFrame(1920, 1080);
        using (hud)
        {
            now += ShortcutMap.AbsentRetryMs;
            map.Refresh(hud);
        }

        AssertAt(placed["phone_autobuff"], map.Find(GameAction.Buff), "Buff after the cool-down");
        Assert.Equal(searches + 1, map.SearchCount); // only the absent one
        Assert.Equal(loot, map.Find(GameAction.Loot));
    }

    [Fact]
    public void AbsentTemplate_SearchedAgainAtOnceWhenInvalidated()
    {
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer, clock: () => now);
        using (var without = HudFrameWithout("phone_autobuff", out _))
            map.Refresh(without);
        Assert.Null(map.Find(GameAction.Buff));

        map.Invalidate(GameAction.Buff);
        var (hud, placed) = HudFrame(1920, 1080);
        using (hud)
            map.Refresh(hud);

        AssertAt(placed["phone_autobuff"], map.Find(GameAction.Buff), "Buff after Invalidate(Buff)");
    }

    [Fact]
    public void RepeatedSkillTapOnAbsentButton_NoExtraSearchesWithinCooldown()
    {
        // Task 7 carry: an LOD skill spam (a tap every 0.3 s) on a skill button that is not on screen must not cost a
        // template search per press - the absent cool-down decides when it is looked for again.
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer, clock: () => now);
        var touch = new RecordingTouch();
        var controls = new TouchControls(new AppSettings(), touch, map, sleep: _ => { });
        using var frame = HudFrameWithout("phone_skill1", out _);
        map.Refresh(frame);
        Assert.Null(map.Skill("1"));
        int searches = map.SearchCount;

        for (int i = 0; i < 10; i++)
        {
            Assert.False(controls.SkillTap("1"));
            now += 300;
            map.Refresh(frame);
        }

        Assert.Equal(searches, map.SearchCount);
        Assert.Empty(touch.Calls);

        now += ShortcutMap.AbsentRetryMs;
        map.Refresh(frame);
        Assert.Equal(searches + 1, map.SearchCount); // the cool-down still brings it back
    }

    [Fact]
    public void Missed_ShortcutNotKnownAbsent_IsSearchedAgain()
    {
        // A miss on a shortcut that is not recorded as absent (found before, forgotten since) is looked for at once.
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer, clock: () => now);
        var (hud, placed) = HudFrame(1920, 1080);
        using (hud)
        {
            map.Refresh(hud);
            int searches = map.SearchCount;

            map.Missed(GameAction.Buff);
            map.Refresh(hud);

            Assert.Equal(searches + 1, map.SearchCount);
            AssertAt(placed["phone_autobuff"], map.Find(GameAction.Buff), "Buff after the miss");
        }
    }

    [Fact]
    public void MissingFile_NeverSearched()
    {
        long now = 1_000_000;
        using var analyzer = NewAnalyzer();
        Assert.Null(analyzer.Library.Get("phone_shura"));
        var map = new ShortcutMap(analyzer, clock: () => now);
        var (hud, _) = HudFrame(1920, 1080);
        using (hud)
        {
            map.Refresh(hud);
            int expected = ShortcutMap.ActionTemplates.Values.Concat(ShortcutMap.SkillTemplates.Values)
                .Append(ShortcutMap.JoystickTemplate).Append(ShortcutMap.MenuTemplate)
                .Count(t => analyzer.Library.Get(t) != null);
            Assert.Equal(expected, map.SearchCount);

            map.Invalidate(GameAction.ShuraKey);
            map.InvalidateSkill("2");
            now += ShortcutMap.AbsentRetryMs * 3;
            map.Refresh(hud);
            Assert.Equal(expected, map.SearchCount);
        }

        Assert.Null(map.Find(GameAction.ShuraKey));
    }

    [Fact]
    public void Matches_IsASnapshot()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);
        var (hud, _) = HudFrame(1920, 1080);
        using (hud)
            map.Refresh(hud);

        var before = map.Matches;
        int count = before.Count;
        map.Invalidate();

        Assert.Equal(count, before.Count);
        Assert.Empty(map.Matches);
    }

    [Fact]
    public void BeforeTheFirstFrame_NothingIsKnown()
    {
        using var analyzer = NewAnalyzer();
        var map = new ShortcutMap(analyzer);

        Assert.Equal((0, 0), map.FrameSize);
        Assert.Null(map.Find(GameAction.TeleportScroll));
        Assert.Null(map.Joystick);
        Assert.Null(map.Menu);
    }

    static string RegionsJson(AppSettings s) => JsonSerializer.Serialize(s.Regions);

    [Fact]
    public void DesktopProfile_RegionsUnchanged()
    {
        string defaults = RegionsJson(new AppSettings());
        var d = new DungeonSettings();

        foreach (var reset in new Action<AppSettings>[]
                 {
                     s => s.ResetPositions(),
                     s => s.ResetPositions(PlatformProfile.Desktop),
                     s => s.ResetPositions(PlatformProfile.Desktop, 2608, 1200),
                 })
        {
            var settings = new AppSettings();
            settings.Regions.Minimap = new NormRect(0.1, 0.1, 0.1, 0.1);
            settings.Regions.AttackMessage = new NormRect(0.2, 0.2, 0.2, 0.2);
            var g = settings.Dungeon;
            g.BossSelectX = 0.9;
            g.BossSelectY = 0.9;
            g.BossSelectStep = 0.5;
            g.MapWheelX = 0.1;
            g.MapWheelY = 0.1;
            g.MapRowChevronX = 0.1;
            g.CameraTiltPx = 999;

            reset(settings);

            Assert.Equal(defaults, RegionsJson(settings));
            // The click fractions are [JsonIgnore]d, so they are compared one by one.
            Assert.Equal(
                (d.BossSelectX, d.BossSelectY, d.BossSelectStep, d.MapWheelX, d.MapWheelY, d.MapRowChevronX, d.CameraTiltPx),
                (g.BossSelectX, g.BossSelectY, g.BossSelectStep, g.MapWheelX, g.MapWheelY, g.MapRowChevronX, g.CameraTiltPx));
        }
    }

    [Fact]
    public void PhoneProfile_AppliesThePhoneRegions()
    {
        var settings = new AppSettings();
        settings.ResetPositions(PlatformProfile.Phone, 1920, 1080);

        var expected = new AppSettings();
        PhoneRegions.Apply(expected, 1920, 1080);
        Assert.Equal(RegionsJson(expected), RegionsJson(settings));
        Assert.NotEqual(RegionsJson(new AppSettings()), RegionsJson(settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pilot_FirstTick_SetsTheRegionsForItsProfileAndFrameSize(bool phone)
    {
        AppPaths.Configure(TempRoot(), PhoneAssets);
        var settings = new AppSettings();
        settings.Input.FocusOnStart = false;
        using var analyzer = new ScreenAnalyzer(settings, new FakeOcrEngine());
        var profile = phone ? PlatformProfile.Phone : PlatformProfile.Desktop;
        var pilot = new AutomationPilot(settings, analyzer, new KeyboardMouseControls(settings, new FakeGameInput()), profile);
        using var frame = Blank.Bgra(1200, 2608);

        pilot.Start();
        await pilot.TickAsync(frame, CancellationToken.None);
        pilot.Stop("test over");

        var expected = new AppSettings();
        if (phone)
            PhoneRegions.Apply(expected, 2608, 1200);
        Assert.Equal(RegionsJson(expected), RegionsJson(settings));
    }

    [Fact]
    public void PhoneRegions_At1920x1080_MatchTheMeasuredBoxes()
    {
        var s = new AppSettings();
        PhoneRegions.Apply(s, 1920, 1080);

        // Task 1 (MuMu, 1920x1080): minimap title + coordinates lines 1560-1900 x 55-125 (M0 crop), system / combat lines 20-540 x 225-470,
        // hotbar slots y 1022 x 680-1236, target / pet panel 1282-1550 x 60-175, teleport list 760-1160 x 155-960.
        AssertCovers(s.Regions.Minimap, 1920, 1080, 1560, 55, 1900, 125);
        Assert.True(s.Regions.Minimap.ToPixels(1920, 1080).Bottom < 160, "the minimap band is the title + coordinates lines only");
        AssertCovers(s.Regions.AttackMessage, 1920, 1080, 20, 225, 540, 470);
        AssertCovers(s.Regions.Hotbar, 1920, 1080, 646, 988, 1270, 1056);
        AssertCovers(s.Regions.TargetBox, 1920, 1080, 1282, 60, 1550, 175);
        AssertCovers(s.Regions.TeleportList, 1920, 1080, 760, 155, 1160, 960);
        AssertCovers(s.Regions.Ocr, 1920, 1080, 760, 155, 1160, 960);
        Assert.Equal(JsonSerializer.Serialize(new NormRect(0, 0, 1, 1)), JsonSerializer.Serialize(s.Regions.BossName));

        // The combat log is on the left on the phone (M0), never past the middle.
        Assert.True(s.Regions.AttackMessage.X + s.Regions.AttackMessage.W < 0.5);
    }

    [Fact]
    public void PhoneRegions_WiderScreen_KeepTheRightHandPanelsOnTheRightEdge()
    {
        var s = new AppSettings();
        PhoneRegions.Apply(s, 2608, 1200);

        // The minimap lines keep their distance from the right edge (scaled with the height): 1920 - 1560 = 360 px at 1080.
        var minimap = s.Regions.Minimap.ToPixels(2608, 1200);
        Assert.True(minimap.X <= 2608 - 360 * 1200 / 1080, $"minimap {minimap}");
        Assert.True(minimap.Right >= 2608 - 20 * 1200 / 1080 - 2, $"minimap {minimap}");
        // The combat log stays at the left edge.
        Assert.True(s.Regions.AttackMessage.ToPixels(2608, 1200).X <= 20 * 1200 / 1080);
    }

    [Fact]
    public void PhoneRegions_NoSizeYet_UsesTheMeasuredScreen()
    {
        var unknown = new AppSettings();
        PhoneRegions.Apply(unknown, 0, 0);
        var measured = new AppSettings();
        PhoneRegions.Apply(measured, 1920, 1080);

        Assert.Equal(RegionsJson(measured), RegionsJson(unknown));
    }

    static void AssertCovers(NormRect region, int w, int h, int left, int top, int right, int bottom)
    {
        var px = region.ToPixels(w, h);
        Assert.True(px.X <= left && px.Y <= top && px.Right >= right && px.Bottom >= bottom,
            $"{px} does not cover {left},{top}-{right},{bottom}");
    }
}
