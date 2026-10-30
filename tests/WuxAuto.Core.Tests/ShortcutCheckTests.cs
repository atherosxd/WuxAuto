using System.Drawing;
using Emgu.CV;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Phone;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The phone's shortcut check (spec §6.1, Review Focus 4) on a fake shortcut ring drawn from the real crops: the padlock
/// (phone_ringlock / phone_ringunlock), (+) on empty slots only while unlocked (phone_ringplus), filled slots with their
/// shortcut's crop. A tap on the padlock toggles it, a tap on a (+) of the unlocked ring opens the picker, a tap on a
/// picker entry's icon fills the slot (and the scripted shortcut source then "finds" it, as ShortcutMap would).
/// </summary>
public class ShortcutCheckTests : AppPathsScope
{
    /// <summary>
    /// The ring flows wait on the real clock (two misses >= 1.5 s apart, settle waits): 6-10 s alone, past 15 s under the
    /// whole suite's load (Auto Blossom's tests). A generous limit; each run still ends as soon as its condition holds.
    /// </summary>
    private const int RingTimeoutMs = 40000;

    // ---------------------------------------------------------------- Review Focus 4

    [Fact]
    public async Task MissingAndNoFreeSlot_StopsAndNamesIt()
    {
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true };
        ring.Fill(0, "Teleport Scroll");
        ring.Fill(1, "Gold Scroll");
        ring.Fill(2, "Gold Scroll");
        ring.Fill(3, "Gold Scroll");                     // every slot taken; Pick up is missing

        var phase = await phone.RunAsync(ring.Render, PilotPhase.Focus, () => false, timeoutMs: RingTimeoutMs);

        Assert.False(phone.Pilot.IsRunning, phone.Log);
        Assert.Equal(PilotPhase.Failed, phase);
        Assert.Contains(phone.Lines, l => l.StartsWith("Add the 'Pick up' shortcut to the ring - no free (+) slot", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.StartsWith("Restarting", StringComparison.Ordinal)); // no restart loop
        // Unlocked to look, nothing else tapped, locked again.
        Assert.Equal(2, phone.Touch.Taps.Count);
        Assert.All(phone.Touch.Taps, t => AssertNear(FakeRing.PadlockCentre, t));
        Assert.True(ring.Locked);
    }

    [Fact]
    public async Task MissingWithFreeSlot_AddsItAndLocksTheRing()
    {
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true };
        ring.Fill(0, "Teleport Scroll");
        ring.Fill(1, "Gold Scroll");                     // slots 2 and 3 empty

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => phone.Pilot.Phase == PilotPhase.CheckZone, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Equal(["Pick up"], ring.Picked);
        Assert.Equal("Pick up", ring.Entry(2));          // the top-most free slot
        Assert.True(ring.Locked, phone.Log);
        Assert.Contains(phone.Lines, l => l.StartsWith("Added the 'Pick up' shortcut to the ring.", StringComparison.Ordinal));
        // padlock (unlock), the (+), the entry's icon, padlock (lock)
        var taps = phone.Touch.Taps;
        Assert.Equal(4, taps.Count);
        AssertNear(FakeRing.PadlockCentre, taps[0]);
        AssertNear(FakeRing.Slots[2], taps[1]);
        AssertNear(FakeRing.PadlockCentre, taps[3]);
        Assert.Empty(ring.StrayTaps);
    }

    [Fact]
    public async Task MissingTwo_AddsBothInFreeSlots_NeverTapsAFilledSlot()
    {
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true, PlusLookalikeWhileLocked = true };
        ring.Fill(0, "Gold Scroll");
        ring.Fill(2, "Gold Scroll");                     // slots 1 and 3 empty; Teleport Scroll and Pick up missing

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => phone.Pilot.Phase == PilotPhase.CheckZone, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Equal(["Teleport Scroll", "Pick up"], ring.Picked);
        Assert.Equal("Gold Scroll", ring.Entry(0));
        Assert.Equal("Gold Scroll", ring.Entry(2));
        Assert.True(ring.Locked);
        // The locked ring's (+) look-alike is never tapped: the padlock comes first, and no tap lands on a filled slot.
        AssertNear(FakeRing.PadlockCentre, phone.Touch.Taps[0]);
        Assert.Empty(ring.FilledSlotTaps);
        Assert.Empty(ring.StrayTaps);
    }

    [Fact]
    public async Task PlusOverAShortcutAlreadyFound_IsNotAFreeSlot()
    {
        // A (+) hit on a slot where a shortcut was found (a look-alike) is not free: stop rather than tap it.
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = false };
        ring.Fill(0, "Teleport Scroll");                 // slot 1 shows a (+) ...
        phone.Shortcuts.Icons[GameAction.Buff] = new PixelRect(FakeRing.Slots[1].X - 30, FakeRing.Slots[1].Y - 30, 60, 60); // ... where AUTO BUFF was found
        ring.Fill(2, "Gold Scroll");
        ring.Fill(3, "Gold Scroll");

        var phase = await phone.RunAsync(ring.Render, PilotPhase.Focus, () => false, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.Failed, phase);
        Assert.Contains(phone.Lines, l => l.StartsWith("Add the 'Pick up' shortcut to the ring", StringComparison.Ordinal));
        Assert.Empty(phone.Touch.Taps);                  // found unlocked: nothing to undo
    }

    [Fact]
    public async Task EntryNotInThePicker_ClosesThePickerLocksTheRingAndStops()
    {
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true, PickerWithout = "Pick up" };
        ring.Fill(0, "Teleport Scroll");

        var phase = await phone.RunAsync(ring.Render, PilotPhase.Focus, () => false, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.Failed, phase);
        Assert.Contains(phone.Lines, l => l.StartsWith("Add the 'Pick up' shortcut to the ring - 'Pick up' was not found in the shortcut picker", StringComparison.Ordinal));
        Assert.False(ring.PickerOpen);
        Assert.True(ring.Locked);
        Assert.Empty(ring.Picked);
        Assert.Empty(ring.StrayTaps);
    }

    [Fact]
    public async Task MissingTwoAndNoFreeSlot_TheStopNamesBoth()
    {
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true };
        for (int i = 0; i < FakeRing.Slots.Length; i++)
            ring.Fill(i, "Gold Scroll");

        var phase = await phone.RunAsync(ring.Render, PilotPhase.Focus, () => false, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.Failed, phase);
        Assert.Contains(phone.Lines, l => l.StartsWith("Add the 'Teleport Scroll', 'Pick up' shortcuts to the ring - no free (+) slot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PickerNotRead_NoEntryTapped()
    {
        // Review #2: entry labels without the picker's own "MENU" / "CLEAR" are not its entries - never tapped.
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true, PickerUnmarked = true };
        ring.Fill(0, "Teleport Scroll");

        var phase = await phone.RunAsync(ring.Render, PilotPhase.Focus, () => false, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.Failed, phase);
        Assert.Contains(phone.Lines, l => l.StartsWith("Add the 'Pick up' shortcut to the ring - the shortcut picker did not open", StringComparison.Ordinal));
        Assert.Empty(ring.Picked);
        Assert.Empty(ring.StrayTaps);
        Assert.True(ring.Locked);
        Assert.Equal(3, phone.Touch.Taps.Count);         // padlock, (+), padlock - nothing in the picker, no close X
    }

    [Fact]
    public async Task StopWhileTheGameIsBehind_RelocksOnceItIsBack()
    {
        // Review #5: a blocked tap while unwinding is retried, not skipped.
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true };
        for (int i = 0; i < FakeRing.Slots.Length; i++)
            ring.Fill(i, i == 0 ? "Teleport Scroll" : "Gold Scroll");
        long behindSince = 0;
        phone.Touch.OnTapAfter = () =>
        {
            if (behindSince == 0)
            {
                behindSince = Environment.TickCount64;
                phone.Touch.InFront = false;      // the unlock went out; then another app comes in front
            }
        };

        var phase = await phone.RunAsync(ring.Render, PilotPhase.Focus, () =>
        {
            if (behindSince > 0 && !phone.Touch.InFront && Environment.TickCount64 - behindSince > 1500)
                phone.Touch.InFront = true;
            return false;
        }, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.Failed, phase);
        Assert.True(ring.Locked, phone.Log);
        Assert.Equal(2, phone.Touch.Taps.Count);
    }

    [Fact]
    public async Task AllPresent_GoesToCheckZone()
    {
        // The templates without phone_pickup: a shortcut without a picture is told, not checked. HUNT is not needed (it is
        // used where found, never added - review of 2026-10-30), so it is neither told nor checked.
        using var phone = new PhonePilot(TempRoot(), assets: AssetsWithout("phone_pickup"));
        var ring = new FakeRing(phone) { Locked = true };
        ring.Fill(0, "Teleport Scroll");

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => phone.Pilot.Phase == PilotPhase.CheckZone, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Contains(PilotPhase.ShortcutCheck, phone.Phases);
        Assert.Empty(phone.Touch.Calls);
        // No crop of the shortcut: told, not checked (adding it would put a copy on the ring every Start).
        Assert.Contains(phone.Lines, l => l.StartsWith("Shortcut check: no picture of the 'Pick up' shortcut yet", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.Contains("'HUNT'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutoLaby_DoesNotNeedPickUp()
    {
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        using var phone = new PhonePilot(TempRoot(), settings);
        var ring = new FakeRing(phone) { Locked = true };
        ring.Fill(0, "Teleport Scroll");

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => phone.Pilot.Phase == PilotPhase.CheckZone, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Empty(phone.Touch.Calls);
    }

    [Fact]
    public async Task DryRun_ReportsTheMissingShortcut_TapsNothing()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Touch.DryRun = true;
        var ring = new FakeRing(phone) { Locked = true };
        ring.Fill(0, "Teleport Scroll");

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => phone.Pilot.Phase == PilotPhase.CheckZone, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Empty(phone.Touch.Calls);
        Assert.Contains(phone.Lines, l => l.StartsWith("Dry run - shortcut(s) 'Pick up' not on the ring", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DesktopProfile_ShortcutCheckNeverRuns()
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLod;
        settings.Input.FocusSettleMs = 0;
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();
        using var frame = Blank.Bgra(1080, 1920);
        var phases = new List<PilotPhase>();

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.Focus, (pilot, _) =>
        {
            phases.Add(pilot.Phase);
            return pilot.Phase is not (PilotPhase.Focus or PilotPhase.Idle);
        });

        Assert.Equal(PilotPhase.CheckZone, run.Phase);
        Assert.DoesNotContain(PilotPhase.ShortcutCheck, phases);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Shortcut check", StringComparison.Ordinal));
    }

    /// <summary>A copy of the phone templates without <paramref name="template"/> (a shortcut with no crop yet).</summary>
    private string AssetsWithout(string template)
    {
        string dir = Path.Combine(TempRoot(), "assets");
        Directory.CreateDirectory(dir);
        foreach (string file in Directory.GetFiles(PhonePilot.PhoneAssets, "*.png"))
        {
            if (!Path.GetFileNameWithoutExtension(file).Equals(template, StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }

        return dir;
    }

    // ---------------------------------------------------------------- fresh looks (Xiaomi 2026-10-30)

    [Fact]
    public void Begin_ForgetsEveryPositionAndAbsence()
    {
        using var phone = new PhonePilot(TempRoot());
        var check = new ShortcutCheck(phone.Analyzer, phone.Controls, phone.Shortcuts, (_, _) => { });

        check.Begin(lod: true);

        Assert.Equal(["all"], phone.Shortcuts.Invalidated);
    }

    [Fact]
    public async Task EveryStartAndRestart_ForgetsTheShortcuts()
    {
        // The pilot's first tick of a run (Start, or a restart after a failure / re-login) clears positions and absences.
        using var phone = new PhonePilot(TempRoot());
        phone.Ocr.Scene = () => [FakeRing.Title];
        using var frame = PhoneFrames.Grey();

        await phone.RunAsync(() => frame, PilotPhase.LodRun, () => phone.Shortcuts.Invalidated.Count > 0, 3000);

        Assert.Equal("all", phone.Shortcuts.Invalidated[0]);
    }

    [Fact]
    public async Task NotFoundOnceThenFound_NothingTapped()
    {
        // The re-login case: the first look still holds the login screen's "not there"; the next search finds them.
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true };
        ring.Fill(0, "Teleport Scroll");
        ring.Fill(1, "Pick up");
        var hidden = new Dictionary<GameAction, PixelRect>(phone.Shortcuts.Icons);
        phone.Shortcuts.Icons.Remove(GameAction.TeleportScroll);
        phone.Shortcuts.Icons.Remove(GameAction.Loot);
        phone.Shortcuts.OnInvalidate = action =>
        {
            if (action is { } a && hidden.TryGetValue(a, out var box))
                phone.Shortcuts.Icons[a] = box;          // searched again: there
        };

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => phone.Pilot.Phase == PilotPhase.CheckZone, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Empty(phone.Touch.Calls);
        Assert.Contains("action:TeleportScroll", phone.Shortcuts.Invalidated);
        Assert.DoesNotContain(phone.Lines, l => l.Contains("not found on two searches", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoZoneTitle_NothingIsDecidedMissing_UntilItReads()
    {
        // A loading / login frame: the title does not read - no padlock tap while it does not.
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true, TitleReads = false };
        ring.Fill(0, "Teleport Scroll");                 // Pick up missing, slots free
        int ticks = 0;

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () =>
        {
            if (++ticks == 8)
            {
                Assert.Empty(phone.Touch.Calls);         // still waiting for the title
                ring.TitleReads = true;
            }

            return phone.Pilot.Phase == PilotPhase.CheckZone;
        }, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Equal(["Pick up"], ring.Picked);
        Assert.Contains(phone.Lines, l => l.StartsWith("Shortcut check: 'Pick up' not found on two searches in a row.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ZoneTitleNeverReads_StopsWithoutTouchingTheRing()
    {
        // 20 looks without the game screen: a stop that says so - never "missing", never a padlock tap.
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true, TitleReads = false };
        ring.Fill(0, "Teleport Scroll");

        var phase = await phone.RunAsync(ring.Render, PilotPhase.Focus, () => false, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.Failed, phase);
        Assert.Empty(phone.Touch.Calls);
        Assert.Contains(phone.Lines, l => l.Contains(ShortcutCheck.NoGameScreenText, StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.Contains("not found on two searches", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTwoMisses_AreAtLeast1500MsApart()
    {
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true };
        ring.Fill(0, "Teleport Scroll");                 // Pick up missing
        long firstLook = 0, decided = 0;
        phone.Shortcuts.OnInvalidate = action =>
        {
            if (action == GameAction.Loot && firstLook == 0)
                firstLook = Environment.TickCount64;     // the first miss asks for a search again
        };
        phone.Pilot.Log += (_, text) =>
        {
            if (text.Contains("not found on two searches", StringComparison.Ordinal) && decided == 0)
                decided = Environment.TickCount64;
        };

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => decided != 0, timeoutMs: RingTimeoutMs);

        Assert.True(firstLook > 0 && decided - firstLook >= ShortcutCheck.MinLookGapMs - 20, $"first miss {firstLook}, decided {decided}");
    }

    [Fact]
    public async Task HuntNotOnAFullRing_IsNoStop()
    {
        // HUNT is optional: a full ring without it starts the run, nothing tapped.
        using var phone = new PhonePilot(TempRoot());
        var ring = new FakeRing(phone) { Locked = true };
        for (int i = 0; i < FakeRing.Slots.Length; i++)
            ring.Fill(i, i == 0 ? "Teleport Scroll" : i == 1 ? "Pick up" : "Gold Scroll");
        phone.Shortcuts.Icons.Remove(GameAction.ToggleAutohunt);

        await phone.RunAsync(ring.Render, PilotPhase.Focus, () => phone.Pilot.Phase == PilotPhase.CheckZone, timeoutMs: RingTimeoutMs);

        Assert.Equal(PilotPhase.CheckZone, phone.Pilot.Phase);
        Assert.Empty(phone.Touch.Calls);
    }

    private static void AssertNear(Point expected, Point actual) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= 6 && Math.Abs(expected.Y - actual.Y) <= 6, $"expected a tap near {expected}, got {actual}");

    // ---------------------------------------------------------------- the picker's labels

    [Fact]
    public void FindEntry_ReadsAWordRunInsideAMergedRowLine()
    {
        // A picker row read as one line: "CLEAR HUNT (J: sta... AUTO BUFF Pick up Gold Scroll".
        var words = FakeOcrEngine.Words("CLEAR HUNT (J: sta AUTO BUFF Pick up Gold Scroll", 720, 147, 500, 16).ToList();

        var pick = ShortcutCheck.FindEntry(words, "Pick up");
        var buff = ShortcutCheck.FindEntry(words, "AUTO BUFF");

        Assert.NotNull(pick);
        Assert.Equal(words.First(w => w.Text == "Pick").Bounds.X, pick!.Value.X);
        Assert.Equal(words.First(w => w.Text == "up").Bounds.Right, pick.Value.Right);
        Assert.NotNull(buff);
        Assert.Equal(words.First(w => w.Text == "AUTO").Bounds.X, buff!.Value.X);
    }

    [Fact]
    public void FindEntry_Hunt_IsNotAutoHunt()
    {
        var autoHunt = FakeOcrEngine.Words("Auto-Hunt (J)", 900, 600, 90, 14).ToList();
        var hunt = FakeOcrEngine.Words("HUNT (J: sta", 820, 147, 90, 14).ToList();

        Assert.Null(ShortcutCheck.FindEntry(autoHunt, "HUNT"));
        Assert.NotNull(ShortcutCheck.FindEntry(hunt, "HUNT"));
        // OCR split "Auto-" and "Hunt (J)" into two words.
        var split = FakeOcrEngine.Words("Auto- Hunt (J)", 900, 600, 90, 14).ToList();
        Assert.Null(ShortcutCheck.FindEntry(split, "HUNT"));
    }

    [Fact]
    public void FindEntry_OneLetterOff_FromSixLetters()
    {
        Assert.NotNull(ShortcutCheck.FindEntry(FakeOcrEngine.Words("Teleport Scrol1", 0, 0).ToList(), "Teleport Scroll"));
        Assert.NotNull(ShortcutCheck.FindEntry(FakeOcrEngine.Words("Pick uP", 0, 0).ToList(), "Pick up"));
        Assert.Null(ShortcutCheck.FindEntry(FakeOcrEngine.Words("Return Scroll", 0, 0).ToList(), "Teleport Scroll"));
    }
}

/// <summary>
/// The shortcut ring as the game draws and handles it (Task 1 note, 1920x1080): four of its 14 slots, the padlock, and
/// the picker "MENU" with its labels 50 px under the icons. Taps on the padlock lock / unlock it; on a (+) of the
/// unlocked ring open the picker; on a picker icon fill the slot and close the picker.
/// </summary>
sealed class FakeRing
{
    public static readonly Point PadlockCentre = new(438, 928);
    public static readonly Point[] Slots = [new(216, 612), new(152, 640), new(286, 608), new(100, 688)];
    private const int SlotRadius = 28;

    // The picker (Task 1): columns x 768 ... 1139, icon rows y 97 / 210, labels about 50 px lower.
    private static readonly (string Entry, int X, int Y)[] Picker =
    [
        ("CLEAR", 768, 97), ("HUNT (J: sta", 861, 97), ("AUTO BUFF", 953, 97), ("Pick up", 1046, 97), ("Gold Scroll", 1139, 97),
        ("Camera lock", 768, 210), ("Rage Burst", 861, 210), ("Return Scroll", 953, 210), ("Teleport Scroll", 1046, 210),
        ("Settings (ESC)", 1139, 210),
    ];

    private static readonly Dictionary<string, (string Template, GameAction? Action)> Icons = new()
    {
        ["Pick up"] = ("phone_pickup", GameAction.Loot),
        ["Teleport Scroll"] = ("phone_teleportscroll", GameAction.TeleportScroll),
        ["Gold Scroll"] = ("phone_wheelpage1", null), // any other icon: a filled slot the pilot has no business with
    };

    private readonly PhonePilot _phone;
    private readonly string?[] _filled = new string?[Slots.Length];
    private int? _pickerFor;

    public FakeRing(PhonePilot phone)
    {
        _phone = phone;
        // AUTO BUFF: a fixed button of the right-hand cluster (1836,662), always there.
        phone.Shortcuts.Icons[GameAction.Buff] = new PixelRect(1804, 630, 64, 64);
        // HUNT (phone_autohunt, 2026-10-30): on the ring of every test here unless one takes it away.
        phone.Shortcuts.Icons[GameAction.ToggleAutohunt] = HuntBox;
        phone.Touch.OnTap = OnTap;
        phone.Ocr.Scene = Scene;
    }

    public bool Locked { get; set; }

    /// <summary>The minimap's zone title reads (the game screen, not a login or loading screen).</summary>
    public bool TitleReads { get; set; } = true;

    /// <summary>The town's zone title in the phone's minimap region (1560,55 340x70 at 1920x1080).</summary>
    public static OcrWord Title => FakeOcrEngine.Line("[Piercing City]", 1600, 70, 200, 16);

    /// <summary>HUNT, on the ring away from the four slots used here: present unless a test takes it away.</summary>
    public static readonly PixelRect HuntBox = new(36, 900, 44, 44);

    /// <summary>Draw a (+) on an empty slot even while locked: the 0.932 look-alike the probe saw on a locked ring.</summary>
    public bool PlusLookalikeWhileLocked { get; init; }

    /// <summary>The picker shows its entries but neither its title "MENU" nor "CLEAR" reads.</summary>
    public bool PickerUnmarked { get; init; }

    /// <summary>An entry the picker does not offer (the game changed its list).</summary>
    public string? PickerWithout { get; init; }

    public bool PickerOpen => _pickerFor != null;

    public List<string> Picked { get; } = [];
    public List<Point> StrayTaps { get; } = [];
    public List<Point> FilledSlotTaps { get; } = [];

    public string? Entry(int slot) => _filled[slot];

    public void Fill(int slot, string entry)
    {
        _filled[slot] = entry;
        if (Icons[entry].Action is { } action)
            _phone.Shortcuts.Icons[action] = new PixelRect(Slots[slot].X - 24, Slots[slot].Y - 24, 48, 48);
    }

    public Mat Render()
    {
        var frame = PhoneFrames.Grey();
        PhoneFrames.PasteCentred(frame, Locked ? "phone_ringlock" : "phone_ringunlock", PadlockCentre.X, PadlockCentre.Y);
        for (int i = 0; i < Slots.Length; i++)
        {
            if (_filled[i] is { } entry)
                PhoneFrames.PasteCentred(frame, Icons[entry].Template, Slots[i].X, Slots[i].Y);
            else if (!Locked || PlusLookalikeWhileLocked)
                PhoneFrames.PasteCentred(frame, "phone_ringplus", Slots[i].X, Slots[i].Y);
        }

        _last?.Dispose();
        _last = frame;
        return frame;
    }

    private Mat? _last;

    private IReadOnlyList<OcrWord> Scene() => _pickerFor == null
        ? TitleReads ? [Title] : []
        : Picker.Where(p => p.Entry != PickerWithout && !(PickerUnmarked && p.Entry == "CLEAR"))
            .SelectMany(p => FakeOcrEngine.Words(p.Entry, p.X - 45, p.Y + 43, 90, 14))
            .Concat(PickerUnmarked ? [] : [FakeOcrEngine.Line("MENU", 930, 20, 60, 18)])
            .ToList();

    private void OnTap(int x, int y)
    {
        var at = new Point(x, y);
        if (Near(at, PadlockCentre, 24))
        {
            Locked = !Locked;
            return;
        }

        if (_pickerFor is int slot)
        {
            if (Near(at, new Point(1164, 38), 20))
            {
                _pickerFor = null; // the picker's X
                return;
            }

            var hit = Picker.FirstOrDefault(p => p.Entry != PickerWithout && Math.Abs(p.X - x) <= 40 && Math.Abs(p.Y - y) <= 40);
            if (hit.Entry != null)
            {
                Picked.Add(hit.Entry);
                Fill(slot, hit.Entry);
                _pickerFor = null;
                return;
            }

            StrayTaps.Add(at);
            return;
        }

        for (int i = 0; i < Slots.Length; i++)
        {
            if (!Near(at, Slots[i], SlotRadius))
                continue;
            if (_filled[i] != null)
                FilledSlotTaps.Add(at);
            else if (!Locked)
                _pickerFor = i;
            else
                StrayTaps.Add(at);
            return;
        }

        StrayTaps.Add(at);
    }

    private static bool Near(Point a, Point b, int r) => Math.Abs(a.X - b.X) <= r && Math.Abs(a.Y - b.Y) <= r;
}
