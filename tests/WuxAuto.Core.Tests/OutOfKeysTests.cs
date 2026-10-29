using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto LOD out of Den of Rebirth entries (the LOD entry missing on its teleport-list page): the desktop switches to Auto
/// Laby as before; the phone (M4 ruling, until M5 ports Laby and Auto War) stops in town with a message instead.
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
    public async Task Phone_LodEntriesOut_StopsInTown_NoModeSwitch_NoTouch()
    {
        var settings = PhonePilot.PhoneLodSettings();
        settings.Lod.ListPage = 1;                          // this page is where the entry would be
        using var phone = new PhonePilot(TempRoot(), settings);
        phone.Ocr.Scene = () => ListWithoutLod(PhoneFrames.Width);
        using var frame = PhoneFrames.Grey();
        bool modeChanged = false;
        phone.Pilot.ModeChanged += () => modeChanged = true;

        await phone.RunAsync(() => frame, PilotPhase.FindDestination, () => false, timeoutMs: 5000);

        Assert.False(phone.Pilot.IsRunning);
        Assert.Equal(RunMode.AutoLod, settings.Sequence.Mode);
        Assert.False(modeChanged);
        Assert.Empty(phone.Touch.Calls);
        Assert.Contains(phone.Lines, l => l.Contains(AutomationPilot.PhoneOutOfKeysText, StringComparison.Ordinal));
        Assert.Contains(phone.Lines, l => l == "Pilot stopped: out of Den of Rebirth entries.");
        Assert.DoesNotContain(phone.Lines, l => l.Contains("switching to", StringComparison.Ordinal) || l.Contains("turning the game's auto war on", StringComparison.Ordinal));
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
}
