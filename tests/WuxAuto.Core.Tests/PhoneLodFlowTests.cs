using System.Drawing;
using Emgu.CV;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Phone;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The pilot's phone branches for Auto LOD (<see cref="PlatformProfile.Phone"/>, <see cref="TouchControls"/> over a
/// recording touch, scripted shortcut positions at 1920x1080 as Task 1 measured them): the teleport by the shortcut, the
/// arrival pinch and the missing south beam, sword targeting and the skill spam without Ctrl, loot and buff by their
/// shortcuts, the walk up by the joystick, the town checks (Run orb, Settings visit) and the hand-off of frames without
/// a zone title (reconnect, the 2-minute limit).
/// </summary>
public class PhoneLodFlowTests : AppPathsScope
{
    // Task 1 / Task 4 boxes at 1920x1080.
    private static readonly PixelRect Sword = new(1595, 755, 120, 120);
    private static readonly PixelRect AutoBuff = new(1804, 630, 64, 64);
    private static readonly PixelRect Skill1 = new(1455, 607, 80, 80);
    private static readonly PixelRect PickUp = new(76, 664, 48, 48);
    private static readonly PixelRect TeleportScroll = new(44, 724, 44, 38);
    private static readonly PixelRect Menu = new(1158, 2, 62, 54);
    private static readonly PixelRect RunOrb = new(1388, 718, 84, 84);
    private const string Boss = "Bai Gu Great";

    private static Point Centre(PixelRect r) => new(r.CenterX, r.CenterY);

    /// <summary>The minimap's zone title in the phone's minimap region: the zone guard holds every LOD phase without it.</summary>
    private static OcrWord Title => FakeOcrEngine.Line("[Ultimate Den of Rebirth]", 1600, 70, 280, 16);

    /// <summary>The boss in the target bar.</summary>
    private static OcrWord Target => FakeOcrEngine.Line("[LOD] " + Boss, 900, 40, 200, 16);

    // ---------------------------------------------------------------- teleport

    [Fact]
    public async Task PhoneTeleport_TapsShortcutNotInventory()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Shortcuts.Icons[GameAction.TeleportScroll] = TeleportScroll;
        phone.Shortcuts.Menu = Menu;                      // the inventory (behind ☰) is there too
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.OpenInventory, () => phone.Pilot.Phase == PilotPhase.FindDestination);

        Assert.Equal(PilotPhase.FindDestination, phone.Pilot.Phase);
        Assert.Equal([Centre(TeleportScroll)], phone.Touch.Taps);
        Assert.Contains(phone.Lines, l => l == "Tapped the Teleport Scroll shortcut.");
    }

    [Fact]
    public async Task PhoneTeleport_NoShortcut_RetriesThenFails_NeverOpensTheMenu()
    {
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.MaxRetries = 1;
        settings.Sequence.RetryDelayMs = 50;
        settings.Sequence.RestartOnError = false;
        using var phone = new PhonePilot(TempRoot(), settings);
        phone.Shortcuts.Menu = Menu;
        using var frame = PhoneFrames.Grey();

        var phase = await phone.RunAsync(() => frame, PilotPhase.OpenInventory, () => false);

        Assert.Equal(PilotPhase.Failed, phase);
        Assert.Empty(phone.Touch.Calls);
        Assert.Contains(phone.Lines, l => l.StartsWith("The Teleport Scroll shortcut on the ring not found", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- arrival

    [Fact]
    public async Task PhoneArrival_PinchesToZoomOut()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Shortcuts.Icons[GameAction.TargetNearest] = Sword;
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[Ultimate Den of Rebirth]", 1600, 70, 280, 16)]; // the minimap title (phone region)
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.ZoomOut, () => phone.Pilot.Phase == PilotPhase.LodRun);

        // 15 notches = 3 pinches (720 -> 192 px apart) at (0.5 w, 0.4 h), nothing else before them.
        var pinches = phone.Touch.Calls.Where(c => c.StartsWith("Pinch", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, pinches.Count);
        Assert.All(pinches, p => Assert.Equal("Pinch 960,432 720->192", p));
        Assert.StartsWith("Pinch", phone.Touch.Calls[0]);
        Assert.Contains(phone.Lines, l => l == "Zoomed out (3 pinch(es)).");
    }

    [Fact]
    public async Task PhoneLod_NoSouthBeamPicture_SkipsTheTurnAndTabsInPlace()
    {
        // No LOD frame from the phone yet, so no phone_flashlightlod crop: no turn drags, no walk - Tab (the sword) only.
        using var phone = new PhonePilot(TempRoot());
        phone.Shortcuts.Icons[GameAction.TargetNearest] = Sword;
        phone.Shortcuts.Joystick = (262, 816, 106);
        Assert.True(phone.Settings.Lod.WalkToBossSpot);
        phone.Ocr.Scene = () =>
        [
            FakeOcrEngine.Line("[Ultimate Den of Rebirth]", 1600, 70, 280, 16),
            FakeOcrEngine.Line("1199 , 1500", 1600, 95, 200, 16),
        ];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.ZoomOut,
            () => phone.Touch.Taps.Count(t => t == Centre(Sword)) >= 2, timeoutMs: 8000);

        Assert.Contains(phone.Lines, l => l.StartsWith("No picture of the LOD minimap's south beam on the phone yet", StringComparison.Ordinal));
        Assert.DoesNotContain(PilotPhase.FaceNorth, phone.Phases);
        Assert.DoesNotContain(phone.Touch.Calls, c => c.StartsWith("Drag", StringComparison.Ordinal) || c.StartsWith("Joy", StringComparison.Ordinal));
        Assert.True(phone.Touch.Taps.Count(t => t == Centre(Sword)) >= 2, phone.Log);
        Assert.Equal(PilotPhase.LodRun, phone.Pilot.Phase);
    }

    // ---------------------------------------------------------------- the fight

    [Fact]
    public async Task PhoneLod_TargetsWithSwordAndSpamsSkillTap()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Tap;
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Settings.Lod.ApproachBoss = false;
        phone.Settings.Lod.SkillEveryMs = 100;
        Assert.True(phone.Settings.Dungeon.CtrlSkill);    // on, but the phone has no Ctrl
        phone.Shortcuts.Icons[GameAction.TargetNearest] = Sword;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        phone.Shortcuts.Skills["1"] = Skill1;
        // The boss is in the target bar once the sword was tapped.
        phone.Ocr.Scene = () => phone.Touch.Taps.Contains(Centre(Sword)) ? [Title, Target] : [Title];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => phone.Touch.Taps.Count(t => t == Centre(Skill1)) >= 3);

        var taps = phone.Touch.Taps;
        Assert.Equal(PilotPhase.LodAttack, phone.Pilot.Phase);
        Assert.Equal(Centre(AutoBuff), taps[0]);          // the buff by its shortcut, on arrival
        Assert.Equal(Centre(Sword), taps[1]);             // TargetNearest = the sword
        Assert.True(taps.Skip(2).All(t => t == Centre(Skill1) || t == Centre(Sword)), string.Join(" ", phone.Touch.Calls));
        Assert.True(taps.Count(t => t == Centre(Skill1)) >= 3);
        Assert.DoesNotContain(phone.Touch.Calls, c => c.StartsWith("Down", StringComparison.Ordinal) || c == "Release");
        Assert.Contains(phone.Lines, l => l.Contains("spamming skill '1' (", StringComparison.Ordinal)); // no "+ Ctrl+1"
    }

    [Fact]
    public async Task PhoneLod_HoldMode_HoldsTheSkillForTheFight()
    {
        // AttackMode Hold (user, MuMu 2026-10-29: holding the icon attacks): one held press for the whole fight, whatever
        // Hold skill (LOD) says; no Ctrl let-go on the phone.
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Hold;
        phone.Settings.Lod.HoldSkill = false;
        phone.Settings.Lod.SkillEveryMs = 100;
        phone.Settings.Lod.ApproachBoss = false;
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Ocr.Scene = () => [Title, Target];
        using var frame = PhoneFrames.Grey();
        long until = Environment.TickCount64 + 1500;

        await phone.RunAsync(() => frame, PilotPhase.LodAttack, () => Environment.TickCount64 > until);

        Assert.Equal(PilotPhase.LodAttack, phone.Pilot.Phase);
        Assert.Equal([$"Down {Skill1.CenterX},{Skill1.CenterY}"], phone.Touch.Calls);
    }

    [Fact]
    public async Task PhoneLod_HoldMode_BuffMidFight_LiftsTapsThenPressesAgain()
    {
        // MuMu LOD 2026-10-30: 15 buff presses in the log, no buff on screen. The held skill is let go before the AUTO BUFF
        // tap and pressed again after it; the tap is logged with its position.
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Hold;
        phone.Settings.Lod.ApproachBoss = false;
        phone.Settings.Lod.BuffEveryMs = 1500;
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        phone.Ocr.Scene = () => [Title, Target];
        using var frame = PhoneFrames.Grey();
        string down = $"Down {Skill1.CenterX},{Skill1.CenterY}";

        await phone.RunAsync(() => frame, PilotPhase.LodAttack, () =>
            phone.Touch.Taps.Count(t => t == Centre(AutoBuff)) >= 2 && phone.Touch.Calls[^1] == down);

        var calls = phone.Touch.Calls;
        int tap = calls.FindLastIndex(c => c.StartsWith("Tap ", StringComparison.Ordinal));
        Assert.True(tap >= 2, string.Join(" | ", calls));
        Assert.True(calls[tap - 1] == "Release", string.Join(" | ", calls)); // let go first ...
        Assert.Equal(down, calls[tap + 1]);             // ... pressed again after
        Assert.Contains(phone.Lines, l => l == $"Auto buff tapped at {AutoBuff.CenterX},{AutoBuff.CenterY}.");
    }

    [Fact]
    public async Task PhoneLod_BuffTimersRenewed_Took()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.PhoneBuffCheckDelayMs = 100;
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        // The timers row under the HP bar: 3m before the tap, 6m after it.
        phone.Ocr.Scene = () => [Title, FakeOcrEngine.Line(phone.Touch.Taps.Contains(Centre(AutoBuff)) ? "6m" : "3m", 160, 160, 24, 14)];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => phone.Lines.Any(l => l.StartsWith("Auto buff took", StringComparison.Ordinal)), 5000);

        Assert.Contains(phone.Lines, l => l == "Auto buff took (timers 6m, buff icons 0.0% changed).");
        Assert.Equal(1, phone.Touch.Taps.Count(t => t == Centre(AutoBuff)));
    }

    [Fact]
    public async Task PhoneLod_BuffTimersUnchanged_TapsOnceMoreThenWarns()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.PhoneBuffCheckDelayMs = 100;
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        phone.Ocr.Scene = () => [Title, FakeOcrEngine.Line("2m", 160, 160, 24, 14)];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => phone.Lines.Any(l => l.StartsWith("Auto buff did not take", StringComparison.Ordinal)), 5000);

        Assert.Equal(2, phone.Touch.Taps.Count(t => t == Centre(AutoBuff)));
        Assert.Contains(phone.Lines, l => l.StartsWith("Auto buff: nothing changed under the HP bar (timers 2m, buff icons 0.0% changed) - buffing once more.", StringComparison.Ordinal));
        Assert.Contains(phone.Lines, l => l.StartsWith("Auto buff did not take (tapped twice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneLod_BuffRefusedByTheGame_WarnsOnce()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Settings.Lod.BuffEveryMs = 1200;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        var refusal = FakeOcrEngine.Line("Force 90% and over required", 760, 420, 400, 18);
        phone.Ocr.Scene = () => phone.Touch.Taps.Contains(Centre(AutoBuff)) ? [Title, refusal] : [Title];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => phone.Touch.Taps.Count(t => t == Centre(AutoBuff)) >= 3, 6000);

        Assert.Single(phone.Lines, l => l.StartsWith("Auto buff refused by the game: Force below 90%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneLod_NoBuffButton_WarnsOnce()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Settings.Lod.BuffEveryMs = 200;
        phone.Ocr.Scene = () => [Title];
        using var frame = PhoneFrames.Grey();
        long until = Environment.TickCount64 + 1200;

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => Environment.TickCount64 > until);

        Assert.Single(phone.Lines, l => l.StartsWith("Auto buff: the AUTO BUFF button is not on screen", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("3m 4m 4m 3m 5m", "6m 6m 6m 1m 6m", true)]   // the MuMu buff frames, before / after one tap
    [InlineData("1m 2m", "1m 2m", false)]
    [InlineData("", "6m 6m", true)]                          // arrival: nothing before
    [InlineData("", "2m", false)]
    [InlineData("6m 5m", "6m 5m", null)]                     // still fresh: cannot tell
    [InlineData("3m", "", null)]                             // nothing reads after
    [InlineData("11d 2m", "11d 2m", false)]                  // an item's days are not a buff
    public void BuffTook_FromTheTimers(string before, string after, bool? took)
    {
        var b = AutomationPilot.BuffTimers(before.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var a = AutomationPilot.BuffTimers(after.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(took, AutomationPilot.BuffTook(before.Length == 0 ? null : b, a));
    }

    [Theory]
    [InlineData("Force 90% and over required", true)]
    [InlineData("F0rce 90% and over required", true)]
    [InlineData("Attack succeeded!Normal damage(+120)", false)]
    [InlineData("[LOD] Bai Gu Great", false)]
    public void IsBuffRefusal(string line, bool refused) => Assert.Equal(refused, AutomationPilot.IsBuffRefusal(line));

    [Fact]
    public void BuffRowChange_StillRowIsZero_NewIconsCount()
    {
        using var before = PhoneFrames.Grey();
        using var same = before.Clone();
        using var renewed = before.Clone();
        // Two new buff icons in the first icon row under the HP bar (0,125 340x55 at 1920x1080).
        PhoneFrames.Paste(renewed, "phone_autohunt", 200, 140);
        PhoneFrames.Paste(renewed, "phone_autohunt", 240, 140);
        using var a = AutomationPilot.BuffRowGrey(before);
        using var b = AutomationPilot.BuffRowGrey(same);
        using var c = AutomationPilot.BuffRowGrey(renewed);

        Assert.Equal(new System.Drawing.Size(340, 55), a.Size);
        Assert.Equal(0, AutomationPilot.BuffRowChange(a, b));
        Assert.True(AutomationPilot.BuffRowChange(a, c) >= AutomationPilot.BuffRowChangedShare);
    }

    [Fact]
    public async Task PhoneLod_BuffIconsStill_TimersUnread_OnlyLogged_NoSecondTap()
    {
        // The timers do not read (Windows OCR read none on the MuMu buff frames) and the icons did not change: the pixels
        // alone are no proof (a second tap with full buffs changes nothing either) - logged, not tapped again.
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.PhoneBuffCheckDelayMs = 100;
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        phone.Ocr.Scene = () => [Title];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => phone.Lines.Any(l => l.StartsWith("Auto buff: cannot tell", StringComparison.Ordinal)), 5000);
        long until = Environment.TickCount64 + 600;
        while (Environment.TickCount64 < until)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(20);
        }

        Assert.Equal(1, phone.Touch.Taps.Count(t => t == Centre(AutoBuff)));
        Assert.Contains(phone.Lines, l => l == "Auto buff: cannot tell whether it took (buff icons 0.0% changed) - not tapped again.");
        Assert.DoesNotContain(phone.Lines, l => l.StartsWith("Auto buff did not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneLod_BuffTimersUnchanged_IconsChanged_OnlyLogged()
    {
        // Timers that did not go up, but icons that changed: the two disagree - logged, no second tap.
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.PhoneBuffCheckDelayMs = 100;
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        phone.Ocr.Scene = () => [Title, FakeOcrEngine.Line("2m", 160, 160, 24, 14)];
        using var plain = PhoneFrames.Grey();
        using var renewed = plain.Clone();
        PhoneFrames.Paste(renewed, "phone_autohunt", 200, 140);
        PhoneFrames.Paste(renewed, "phone_autohunt", 240, 140);

        await phone.RunAsync(() => phone.Touch.Taps.Contains(Centre(AutoBuff)) ? renewed : plain, PilotPhase.LodRun,
            () => phone.Lines.Any(l => l.StartsWith("Auto buff: cannot tell", StringComparison.Ordinal)), 5000);

        Assert.Equal(1, phone.Touch.Taps.Count(t => t == Centre(AutoBuff)));
        Assert.DoesNotContain(phone.Lines, l => l.StartsWith("Auto buff did not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneLod_HoldMode_BuffRetryMidFight_GoesThroughTheRebuff()
    {
        // The "once more" buff in a Hold fight: the usual rebuff (skill let go, stop-attack step, 400 ms), not a bare tap.
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.PhoneBuffCheckDelayMs = 1500; // the skill is held by then
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Hold;
        phone.Settings.Lod.ApproachBoss = false;
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        phone.Ocr.Scene = () => [Title, Target, FakeOcrEngine.Line("2m", 160, 160, 24, 14)];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodAttack, () => phone.Touch.Taps.Count(t => t == Centre(AutoBuff)) >= 2, 8000);

        Assert.Equal(PilotPhase.LodAttack, phone.Pilot.Phase);
        Assert.Contains(phone.Lines, l => l.StartsWith("Auto buff: nothing changed under the HP bar", StringComparison.Ordinal));
        int retry = phone.Lines.FindIndex(l => l.StartsWith("Auto buff: nothing changed under the HP bar", StringComparison.Ordinal));
        Assert.Contains(phone.Lines.Skip(retry), l => l.StartsWith("Re-buffed (", StringComparison.Ordinal));
        var calls = phone.Touch.Calls;
        int second = calls.FindLastIndex(c => c == $"Tap {AutoBuff.CenterX},{AutoBuff.CenterY}");
        Assert.Equal("Release", calls[second - 1]);
    }

    [Fact]
    public async Task PhoneLod_DryRun_NoBuffCheck()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Touch.DryRun = true;
        phone.Pilot.PhoneBuffCheckDelayMs = 100;
        phone.Settings.Lod.WalkToBossSpot = false;
        phone.Shortcuts.Icons[GameAction.Buff] = AutoBuff;
        phone.Ocr.Scene = () => [Title, FakeOcrEngine.Line("2m", 160, 160, 24, 14)];
        using var frame = PhoneFrames.Grey();
        long until = Environment.TickCount64 + 1200;

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => Environment.TickCount64 > until);

        Assert.Contains(phone.Lines, l => l.StartsWith("Auto buff tapped", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.StartsWith("Auto buff took", StringComparison.Ordinal)
                                                || l.StartsWith("Auto buff:", StringComparison.Ordinal)
                                                || l.StartsWith("Auto buff did not take", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneTown_AutohuntOffCheck_NotOnThePhone()
    {
        // HUNT on the ring, in town: the desktop's autohunt-off check (J + combat-log read) is not run on the phone.
        using var phone = new PhonePilot(TempRoot());
        var hunt = new PixelRect(36, 900, 44, 44);
        phone.Shortcuts.Icons[GameAction.ToggleAutohunt] = hunt;
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[Piercing City]", 1600, 70, 200, 16)];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.CheckZone, () => phone.Pilot.Phase != PilotPhase.CheckZone, 5000);

        Assert.DoesNotContain(PilotPhase.AutohuntOff, phone.Phases);
        Assert.DoesNotContain(Centre(hunt), phone.Touch.Taps);
    }

    [Fact]
    public async Task PhoneLod_HoldMode_HoldLiftedByAnotherTouch_PressedAgain()
    {
        // The next gesture lifts a held finger (AndroidTouchInput); the pilot presses the hold again at once.
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Hold;
        phone.Settings.Lod.ApproachBoss = false;
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Ocr.Scene = () => [Title, Target];
        phone.Touch.Alive = true;
        using var frame = PhoneFrames.Grey();
        long lifted = Environment.TickCount64 + 600;
        long until = lifted + 900;
        bool liftedOnce = false;

        await phone.RunAsync(() =>
        {
            if (!liftedOnce && Environment.TickCount64 > lifted)
            {
                liftedOnce = true;
                phone.Touch.Alive = false; // e.g. a Tab tap went out
            }
            else if (liftedOnce && phone.Touch.Calls.Count(c => c.StartsWith("Down ")) >= 2)
                phone.Touch.Alive = true;

            return frame;
        }, PilotPhase.LodAttack, () => Environment.TickCount64 > until);

        string down = $"Down {Skill1.CenterX},{Skill1.CenterY}";
        Assert.Equal([down, down], phone.Touch.Calls);
        Assert.Contains("lifted by another touch", phone.Log);
    }

    [Fact]
    public async Task PhoneLod_HoldMode_ReleaseAll_NextTickPressesAgain()
    {
        // M4 review #1: the engine lifts every touch on a pause (the floating panel, the game behind, a loop wait); the
        // fight's next tick must press the skill down again - HoldAlive cannot tell (null), the hold is simply gone.
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Hold;
        phone.Settings.Lod.ApproachBoss = false;
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Ocr.Scene = () => [Title, Target];
        using var frame = PhoneFrames.Grey();
        string down = $"Down {Skill1.CenterX},{Skill1.CenterY}";

        await phone.RunAsync(() => frame, PilotPhase.LodAttack, () => phone.Touch.Calls.Contains(down));
        Assert.Equal([down], phone.Touch.Calls);

        phone.Controls.ReleaseAll();                       // what the engine does on the pause
        long deadline = Environment.TickCount64 + 3000;
        while (phone.Touch.Calls.Count(c => c == down) < 2 && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(20);
        }

        Assert.Equal([down, "Release", down], phone.Touch.Calls);
        Assert.Contains("lifted by another touch", phone.Log);
    }

    [Theory]
    [InlineData(PhoneAttackMode.Tap, "'1' tapped x10 (100 ms apart; attack mode Tap)")]
    [InlineData(PhoneAttackMode.Hold, "'1' held 3000 ms, then tapped x10 (300 ms apart; attack mode Hold)")]
    public async Task PhoneStage5_ManualAttackLog_SaysWhatThePhoneDoes(PhoneAttackMode mode, string presses)
    {
        // Review minor 10: in Tap mode there is no held press - the log must not say "held 3000 ms".
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        settings.Dungeon.Stages = [];                     // stage index 0 = past the last stage = the final boss
        settings.Phone.AttackMode = mode;
        using var phone = new PhonePilot(TempRoot(), settings);
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 70, 280, 16)];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.SelectBoss,
            () => phone.Lines.Any(l => l.StartsWith("Attacking ", StringComparison.Ordinal)), timeoutMs: 5000);

        Assert.Contains(phone.Lines, l => l.StartsWith("Attacking ", StringComparison.Ordinal) && l.Contains($"): {presses};", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PhoneLod_TapMode_NoHold_OneTapPerAttackTapEveryMs(bool holdSkill)
    {
        // AttackMode Tap (user, MuMu 2026-10-29: "no hold", "100ms taps"; the default is Hold since). Hold skill (LOD) on or off, the phone
        // taps the attack icon at most once per PhoneSettings.AttackTapEveryMs and never presses it down.
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Tap;
        phone.Settings.Lod.HoldSkill = holdSkill;
        phone.Settings.Lod.SkillEveryMs = 100;
        phone.Settings.Lod.ApproachBoss = false;
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Ocr.Scene = () => [Title, Target];
        using var frame = PhoneFrames.Grey();
        long start = Environment.TickCount64;
        long until = start + 2000;

        await phone.RunAsync(() => frame, PilotPhase.LodAttack, () => Environment.TickCount64 > until);
        long elapsed = Environment.TickCount64 - start;

        Assert.Equal(PilotPhase.LodAttack, phone.Pilot.Phase);
        Assert.DoesNotContain(phone.Touch.Calls, c => c.StartsWith("Down "));
        int skillTaps = phone.Touch.Taps.Count(t => t.X == Skill1.CenterX && t.Y == Skill1.CenterY);
        Assert.InRange(skillTaps, 2, (int)(elapsed / phone.Settings.Phone.AttackTapEveryMs) + 1);
    }

    [Fact]
    public async Task PhoneLod_Loot_SpamsThePickUpShortcut()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Lod.LootMs = 700;
        phone.Shortcuts.Icons[GameAction.Loot] = PickUp;
        phone.Ocr.Scene = () => [Title];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodLoot, () => phone.Pilot.Phase != PilotPhase.LodLoot);

        Assert.True(phone.Touch.Taps.Count >= 3, string.Join(" ", phone.Touch.Calls));
        Assert.All(phone.Touch.Taps, t => Assert.Equal(Centre(PickUp), t));
        Assert.Equal(PilotPhase.LodRun, phone.Pilot.Phase);
    }

    [Fact]
    public async Task PhoneLod_WalkUp_PushesTheJoystickTowardsTheRing()
    {
        // The first hit of a fight: stop (nothing to press here) and walk to the pink ring round the boss's feet - on the
        // phone a joystick step in the ring's direction (TouchControls.WalkTo), not a Ctrl+click.
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Phone.AttackMode = PhoneAttackMode.Tap;
        phone.Settings.Lod.ApproachWaitMs = 100;
        phone.Settings.Lod.RingCheckEveryMs = 0;
        phone.Settings.Lod.SkillEveryMs = 100;
        phone.Shortcuts.Skills["1"] = Skill1;
        phone.Shortcuts.Joystick = (262, 816, 106);
        phone.Ocr.Scene = () =>
        {
            int taps = phone.Touch.Taps.Count(t => t == Centre(Skill1));
            return
            [
                Title,
                Target,
                // the combat log (phone region, top left): a new hit with every skill tap
                .. Enumerable.Range(0, 6).Select(i =>
                    FakeOcrEngine.Line($"Attack succeeded!Normal damage(+{100 + i + taps}) Attribu", 20, 230 + i * 30, 400, 16)),
            ];
        };
        using var frame = PhoneFrames.Grey();
        // A pink ring up and to the left of the character (0.5 w, 0.54 h = 960,583).
        CvInvoke.Ellipse(frame, new Point(700, 400), new Size(150, 55), 0, 0, 360, new MCvScalar(230, 120, 255, 255), 10);

        await phone.RunAsync(() => frame, PilotPhase.LodAttack,
            () => phone.Touch.Calls.Any(c => c.StartsWith("Joy", StringComparison.Ordinal)));

        var joy = phone.Touch.Calls.First(c => c.StartsWith("Joy", StringComparison.Ordinal));
        var parts = joy.Split(' ')[2].Split(',');
        int dx = int.Parse(parts[0]), dy = int.Parse(parts[1]);
        Assert.True(dx < 0 && dy < 0, $"expected a push up-left towards the ring, got {joy}\n{phone.Log}");
        Assert.Contains(phone.Lines, l => l.StartsWith("First hit on", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Touch.Calls, c => c.StartsWith("Down", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- town checks

    [Fact]
    public async Task PhoneRunSkill_NoIconOnBar_SkipsWithoutAnyTap()
    {
        using var phone = new PhonePilot(TempRoot());
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.RunSkill, () => phone.Pilot.Phase != PilotPhase.RunSkill);

        Assert.Empty(phone.Touch.Calls);
        Assert.Contains(phone.Lines, l => l.StartsWith("The Run skill orb is not on screen - not checked", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.Contains("pressed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneRunSkill_OnlyTheHotbarCopy_IsNotTapped()
    {
        // The hotbar copy (slot 7, 1157,1022) never shows the ring: it would read "off" and get tapped (Task 4 concern 4).
        using var phone = new PhonePilot(TempRoot());
        phone.Shortcuts.Icons[GameAction.RunSkillKey] = new PixelRect(1121, 986, 72, 72);
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.RunSkill, () => phone.Pilot.Phase != PilotPhase.RunSkill);

        Assert.Empty(phone.Touch.Calls);
        Assert.Contains(phone.Lines, l => l.StartsWith("The Run skill orb is not on screen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneRunSkill_OrbWithoutRing_TapsTheOrb()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Shortcuts.Icons[GameAction.RunSkillKey] = RunOrb;
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.RunSkill, () => phone.Touch.Taps.Count >= 1);

        Assert.Equal([Centre(RunOrb)], phone.Touch.Taps);
        Assert.Contains(phone.Lines, l => l.StartsWith("The Run skill is off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneRunSkill_OrbWithItsGoldRing_IsOn_NoTap()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Shortcuts.Icons[GameAction.RunSkillKey] = RunOrb;
        using var frame = PhoneFrames.Grey();
        // Task 1: four gold arcs at r 66-72 round the orb's centre (1430,760), RGB 185,161,107.
        var gold = new MCvScalar(107, 161, 185, 255);
        foreach (int start in new[] { 10, 100, 190, 280 })
            CvInvoke.Ellipse(frame, new Point(1430, 760), new Size(69, 69), 0, start, start + 60, gold, 6);

        await phone.RunAsync(() => frame, PilotPhase.RunSkill, () => phone.Pilot.Phase != PilotPhase.RunSkill);

        Assert.Empty(phone.Touch.Calls);
        Assert.Contains(phone.Lines, l => l.StartsWith("The Run skill is on (ring", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneSettingsVisit_IsSkippedWithOneLine()
    {
        using var phone = new PhonePilot(TempRoot());
        Assert.True(phone.Settings.Sequence.FontSizePercent > 0);
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.RunSkill, () => phone.Pilot.Phase == PilotPhase.OpenInventory);

        Assert.Equal(PilotPhase.OpenInventory, phone.Pilot.Phase);
        Assert.DoesNotContain(PilotPhase.FontSize, phone.Phases);
        Assert.Single(phone.Lines, l => l.StartsWith("Font size / TAB target: not checked on the phone", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- frames without a zone title

    [Fact]
    public async Task PhoneNoTitle_LoginScreen_StartsTheReconnect()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("SERVERS", 900, 100, 120, 20)];
        using var frame = PhoneFrames.Grey();
        phone.Pilot.Start(PilotPhase.LodRun);

        await phone.Pilot.TickWithoutTitleAsync(frame, CancellationToken.None);

        Assert.Equal(PilotPhase.Reconnect, phone.Pilot.Phase);
        Assert.True(PhoneLoopGate.RunsWithoutTitle(phone.Pilot.Phase)); // from here the loop ticks the pilot fully
        Assert.Empty(phone.Touch.Calls);
    }

    [Fact]
    public async Task PhoneNoTitle_HeldTooLongWithTheGameInFront_FailsIntoTheRestart()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.NoTitleLimitMs = 200;
        using var frame = PhoneFrames.Grey();
        phone.Pilot.Start(PilotPhase.LodRun);

        await phone.Pilot.TickWithoutTitleAsync(frame, CancellationToken.None);
        Assert.Equal(PilotPhase.LodRun, phone.Pilot.Phase);
        await Task.Delay(300);
        await phone.Pilot.TickWithoutTitleAsync(frame, CancellationToken.None);

        Assert.Contains(phone.Lines, l => l.StartsWith("The minimap's zone title has not read for 0s with the game in front", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.Focus, phone.Pilot.Phase); // Restart on error: from the top
        Assert.True(phone.Pilot.IsRunning);
        Assert.Empty(phone.Touch.Calls);
    }

    [Fact]
    public async Task PhoneNoTitle_ClockCountsOnlyUnbrokenNoTitlePasses_InTheGatesOrder()
    {
        // Review #1: the game behind / the panel open are decided before the title, so the pilot is not called on those
        // passes - the engine's hand-off (PhoneLoopGate.HandOffAsync) restarts the clock instead. Driven pass by pass
        // through the gate's own decisions, as MobileEngine does.
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.NoTitleLimitMs = 300;
        using var frame = PhoneFrames.Grey();
        phone.Pilot.Start(PilotPhase.LodRun);
        var gate = new PhoneLoopGate();
        var kinds = new List<LoopWait>();

        async Task Pass(bool front, bool panel)
        {
            long now = FrameGate.NowTicks();
            var decision = gate.Decide(now, new CaptureState(true, now), front, panel, phone.Pilot.Phase);
            kinds.Add(decision.Kind);
            if (decision.Tick)
                await phone.Pilot.TickAsync(frame, CancellationToken.None);
            else
                await PhoneLoopGate.HandOffAsync(decision, phone.Pilot, frame, CancellationToken.None);
        }

        bool failed() => phone.Lines.Any(l => l.StartsWith("The minimap's zone title has not read", StringComparison.Ordinal));

        // No title, then the game behind for 400 ms, then no title again: the behind passes restarted the clock.
        await Pass(front: true, panel: false);
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(100);
            await Pass(front: false, panel: false);
        }

        await Pass(front: true, panel: false);
        Assert.False(failed(), phone.Log);

        // The same with the floating panel open.
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(100);
            await Pass(front: true, panel: true);
        }

        await Pass(front: true, panel: false);
        Assert.False(failed(), phone.Log);
        Assert.Contains(LoopWait.NotInFront, kinds);
        Assert.Contains(LoopWait.PanelOpen, kinds);
        Assert.Contains(LoopWait.NoTitle, kinds);
        Assert.Equal(PilotPhase.LodRun, phone.Pilot.Phase);

        // An unbroken run of no-title passes past the limit: the zone guard's fail -> restart from the top.
        for (int i = 0; i < 5 && !failed(); i++)
        {
            await Task.Delay(100);
            await Pass(front: true, panel: false);
        }

        Assert.True(failed(), phone.Log);
        Assert.Equal(PilotPhase.Focus, phone.Pilot.Phase);
        Assert.Empty(phone.Touch.Calls);
    }

    [Fact]
    public async Task PhoneNoTitle_AFullTickInBetween_RestartsTheClock()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.NoTitleLimitMs = 200;
        using var frame = PhoneFrames.Grey();
        phone.Pilot.Start(PilotPhase.LodRun);

        await phone.Pilot.TickWithoutTitleAsync(frame, CancellationToken.None);
        await Task.Delay(150);
        await phone.Pilot.TickAsync(frame, CancellationToken.None);  // the title read again
        await Task.Delay(150);
        await phone.Pilot.TickWithoutTitleAsync(frame, CancellationToken.None);

        Assert.DoesNotContain(phone.Lines, l => l.StartsWith("The minimap's zone title has not read", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.LodRun, phone.Pilot.Phase);
    }

    [Fact]
    public async Task PhoneReconnect_ReadAndClickOnly_NoGuessedButtons()
    {
        // Review #10: no phone login frame yet - the desktop's guessed LOG IN (gold button, the "Remember me" - BACK
        // gap) is off on the phone; a LOG IN that reads is still tapped.
        using var phone = new PhonePilot(TempRoot());
        phone.Settings.Sequence.ReconnectStepDelayMs = 50;
        bool logInReads = false;
        phone.Ocr.Scene = () =>
        {
            var words = new List<OcrWord>
            {
                FakeOcrEngine.Line("ACCOUNT NAME", 800, 300, 200, 20),
                FakeOcrEngine.Line("Remember me", 800, 420, 160, 20),
                FakeOcrEngine.Line("BACK", 900, 700, 80, 20),
            };
            if (logInReads)
                words.Add(FakeOcrEngine.Line("LOG IN", 900, 560, 100, 24));
            return words;
        };
        using var frame = PhoneFrames.Grey();
        // A gold "button" where the desktop's colour fallback would find one.
        CvInvoke.Rectangle(frame, new Rectangle(860, 540, 200, 50), new MCvScalar(40, 150, 220, 255), -1);
        phone.Pilot.Start(PilotPhase.LodRun);
        await phone.Pilot.TickWithoutTitleAsync(frame, CancellationToken.None);
        Assert.Equal(PilotPhase.Reconnect, phone.Pilot.Phase);

        long until = Environment.TickCount64 + 1200;
        while (Environment.TickCount64 < until)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(20);
        }

        Assert.Empty(phone.Touch.Calls);

        logInReads = true;
        until = Environment.TickCount64 + 1000;
        while (Environment.TickCount64 < until && phone.Touch.Taps.Count == 0)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(20);
        }

        Assert.Equal([new Point(950, 572)], phone.Touch.Taps);
    }

    [Fact]
    public async Task PhoneRunSkill_OrbStillOff_TappedOncePerStart()
    {
        // Review #3: the phone's ring band is unverified - a ring it cannot see must not have the orb toggled on and off.
        using var phone = new PhonePilot(TempRoot());
        phone.Shortcuts.Icons[GameAction.RunSkillKey] = RunOrb;
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.RunSkill, () => phone.Pilot.Phase != PilotPhase.RunSkill);

        Assert.Equal([Centre(RunOrb)], phone.Touch.Taps);
        Assert.Contains(phone.Lines, l => l.StartsWith("The Run skill is off (ring 0.", StringComparison.Ordinal));
        Assert.Contains(phone.Lines, l => l.StartsWith("The Run skill still reads off (ring 0.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneDryRun_Teleport_WouldTap_ThenStopsCleanly_NoRestart()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Touch.DryRun = true;
        phone.Shortcuts.Icons[GameAction.TeleportScroll] = TeleportScroll;
        using var frame = PhoneFrames.Grey();

        var phase = await phone.RunAsync(() => frame, PilotPhase.OpenInventory, () => false);

        Assert.False(phone.Pilot.IsRunning, phone.Log);
        Assert.Equal(PilotPhase.Idle, phase);
        Assert.Contains(phone.Lines, l => l == "Dry run - would tap the Teleport Scroll shortcut.");
        Assert.Contains(phone.Lines, l => l.StartsWith("Dry run stops here: FindChevron needs the real tap", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.StartsWith("Restarting", StringComparison.Ordinal));
        Assert.Empty(phone.Touch.Calls);
    }

    [Theory]
    [InlineData(0, 100, 5000, 0)]          // no lift yet
    [InlineData(1000, 100, 1000, 100)]     // right after the lift: the whole settle
    [InlineData(1000, 100, 1060, 40)]
    [InlineData(1000, 100, 1100, 0)]
    [InlineData(1000, 100, 2000, 0)]
    [InlineData(1000, 99999, 1000, LiftSettle.MaxSettleMs)]
    [InlineData(1000, -5, 1000, 0)]
    public void LiftSettle_WaitsOutTheSettleFromTheLift(long liftedAt, int settle, long now, int expected) =>
        // B8: the touch worker's gap between a lift and the next gesture (AndroidTouchInput.SettleAfterLift).
        Assert.Equal(expected, LiftSettle.WaitMs(liftedAt, settle, now));
}
