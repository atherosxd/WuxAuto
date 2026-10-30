using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto LOD out of Den of Rebirth entries (the LOD entry missing on its teleport-list page): the desktop and, since M5,
/// the phone switch to Auto Laby and tap the Labyrinth on the same page. Both kinds out: the desktop turns the game's
/// Regular War on; the phone (no Auto War there yet) stops in town with a message.
/// </summary>
public class OutOfKeysTests : AppPathsScope
{
    /// <summary>A teleport list page without the LOD entry (the Labyrinth is on it).</summary>
    private static IReadOnlyList<OcrWord> ListWithoutLod(int width) =>
    [
        FakeOcrEngine.Line("[The Labyrinth (Adept)]", width / 2 - 150, 300, 300, 16),
        FakeOcrEngine.Line("[Bohai Outpost (Adept)]", width / 2 - 150, 340, 300, 16),
    ];

    [Fact]
    public async Task Phone_LodEntriesOut_SwitchesToAutoLaby_TapsTheLabyrinth()
    {
        var settings = PhonePilot.PhoneLodSettings();
        settings.Lod.ListPage = 1;                          // this page is where the entry would be
        using var phone = new PhonePilot(TempRoot(), settings);
        phone.Ocr.Scene = () => ListWithoutLod(PhoneFrames.Width);
        using var frame = PhoneFrames.Grey();
        bool modeChanged = false;
        phone.Pilot.ModeChanged += () => modeChanged = true;

        await phone.RunAsync(() => frame, PilotPhase.FindDestination, () => phone.Touch.Taps.Count > 0, timeoutMs: 5000);

        Assert.Equal(RunMode.AutoLaby, settings.Sequence.Mode);
        Assert.True(modeChanged);
        Assert.Contains(phone.Lines, l => l.EndsWith("out of Den of Rebirth entries - switching to AutoLaby.", StringComparison.Ordinal));
        var tap = Assert.Single(phone.Touch.Taps);
        Assert.InRange(tap.Y, 300, 316);                    // the Labyrinth's row
    }

    [Fact]
    public void Phone_BothKindsOut_StopsInTown_NoRegularWar()
    {
        using var phone = new PhonePilot(TempRoot());
        phone.Pilot.Start(PilotPhase.FindDestination);

        Assert.True(phone.Pilot.OutOfKeys(labyrinth: false, "LOD: out of Den of Rebirth entries"));   // -> Laby
        Assert.True(phone.Pilot.IsRunning);
        Assert.False(phone.Pilot.OutOfKeys(labyrinth: true, "Laby: out of Temple of Fiends Keys"));

        Assert.False(phone.Pilot.IsRunning);
        Assert.Contains(phone.Lines, l => l.Contains(AutomationPilot.PhoneOutOfKeysText, StringComparison.Ordinal));
        Assert.Contains(phone.Lines, l => l == "Pilot stopped: out of Temple of Fiends Keys and Den of Rebirth entries.");
        Assert.DoesNotContain(phone.Lines, l => l.Contains("turning the game's auto war on", StringComparison.Ordinal));
        Assert.NotEqual(PilotPhase.RegularWarOn, phone.Pilot.Phase);
        Assert.Empty(phone.Touch.Calls);
    }

    [Fact]
    public async Task Desktop_LodEntriesOut_SwitchesToAutoLaby_AsBefore()
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLod;
        settings.Lod.ListPage = 1;
        var ocr = new FakeOcrEngine { Scene = () => ListWithoutLod(1024) };
        using var frame = Blank.Bgra(768, 1024);

        var (_, lines) = await PilotRun.RunAsync(settings, new FakeGameInput(), ocr, frame, PilotPhase.FindDestination,
            (_, log) => log.Any(l => l.Contains("switching to", StringComparison.Ordinal)), timeoutMs: 5000);

        Assert.Equal(RunMode.AutoLaby, settings.Sequence.Mode);
        Assert.Contains(lines, l => l.EndsWith("out of Den of Rebirth entries - switching to AutoLaby.", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(AutomationPilot.PhoneOutOfKeysText, StringComparison.Ordinal));
    }

    /// <summary>The PC item list at the bottom right: three timed lines, a Temple key, no Den of Rebirth entry.</summary>
    private static IReadOnlyList<OcrWord> ItemListWithoutDen(int width, int height) =>
    [
        FakeOcrEngine.Line("[Temple of Fiends Key] 12 (time(s))", (int)(width * 0.70), (int)(height * 0.60), 300, 16),
        FakeOcrEngine.Line("[Gold Scroll] 3 (time(s))", (int)(width * 0.70), (int)(height * 0.64), 300, 16),
        FakeOcrEngine.Line("[Rage Elixir] 30 (min(s))", (int)(width * 0.70), (int)(height * 0.68), 300, 16),
    ];

    [Fact]
    public async Task ItemListCheck_Desktop_SwitchesMode_Phone_NeverRuns()
    {
        // Desktop: the (off-by-default) item-list check sees no Den of Rebirth line and switches to Auto Laby.
        var desktop = new AppSettings();
        desktop.Sequence.Mode = RunMode.AutoLod;
        desktop.Sequence.CheckKeys = true;
        var desktopOcr = new FakeOcrEngine { Scene = () => ItemListWithoutDen(1024, 768) };
        using var desktopAnalyzer = new ScreenAnalyzer(desktop, desktopOcr);
        var desktopPilot = new AutomationPilot(desktop, desktopAnalyzer, new KeyboardMouseControls(desktop, new FakeGameInput()));
        using var desktopFrame = Blank.Bgra(768, 1024);
        Assert.True(await desktopPilot.CheckKeysAsync(desktopFrame));
        Assert.Equal(RunMode.AutoLaby, desktop.Sequence.Mode);

        // Phone: the same list never switches the mode (Laby / War are not ported; keys live in Durations, M6).
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.CheckKeys = true;
        using var phone = new PhonePilot(TempRoot(), settings);
        phone.Ocr.Scene = () => ItemListWithoutDen(PhoneFrames.Width, PhoneFrames.Height);
        using var frame = PhoneFrames.Grey();
        bool modeChanged = false;
        phone.Pilot.ModeChanged += () => modeChanged = true;

        Assert.False(await phone.Pilot.CheckKeysAsync(frame));
        Assert.Equal(RunMode.AutoLod, settings.Sequence.Mode);
        Assert.False(modeChanged);
        Assert.Empty(phone.Touch.Calls);
    }
}
