using System.Drawing;
using Emgu.CV;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// In town, before the autohunt check (user, 2026-10-29: "check if the regular war is turned on in the autohunt, disable
/// it if enabled, as it may get teleported to the war when the autohunt is on while attacking the boss"; 2026-11-02: "so
/// before trying to turn off auto hunt check if regular war and smart farm is off first"): S opens the autohunt settings,
/// a ticked "Regular War" or "Smart Farm" box is clicked until it reads empty, S closes the window, then the autohunt
/// check. The frame draws the gold boxes left of the labels (OCR fake); S toggles the window, a click inside a box toggles
/// its tick (unless the box is "stuck"); a box may show its change late (live 2026-11-01 16:51: two clicks 0.9 s apart
/// and Regular War still read ticked).
/// </summary>
[Collection("AppPaths")] // AppPaths is static state
public class RegularWarOffTests
{
    private const int Width = 1024;
    private const int Height = 768;
    private static readonly Rectangle WarLabel = new(500, 300, 90, 14);
    private static readonly Rectangle WarBox = new(474, 300, 14, 14);
    private static readonly Rectangle FarmLabel = new(500, 330, 85, 14);
    private static readonly Rectangle FarmBox = new(474, 330, 14, 14);
    private static readonly string WarClick = $"Click {WarBox.X + WarBox.Width / 2},{WarBox.Y + WarBox.Height / 2}";
    private static readonly string FarmClick = $"Click {FarmBox.X + FarmBox.Width / 2},{FarmBox.Y + FarmBox.Height / 2}";

    private sealed class Row
    {
        public required Rectangle Box { get; init; }
        public bool Ticked { get; set; }
        public bool Stuck { get; init; }            // the click never changes the box
        public int Clicks { get; set; }
        public bool ShownTicked { get; set; }       // what the screen shows (late by ShowLagMs)
        public long ChangedAt { get; set; }
    }

    private sealed class Scene
    {
        public required FakeGameInput Input { get; init; }
        public required Row War { get; init; }
        public required Row Farm { get; init; }
        public bool FarmReads { get; init; } = true;
        public bool NeverOpens { get; init; }      // S shows nothing (no settings window)
        public bool LabelLostAfterClick { get; init; }   // the labels stop reading once a box was clicked
        public int ShowLagMs { get; init; }        // a click shows on the screen this much later
        private int _clicksSeen;

        public bool Open => !NeverOpens && Input.Calls.Count(c => c == "KeyTap S") % 2 == 1;
        public bool LabelsRead => Open && !(LabelLostAfterClick && War.Clicks + Farm.Clicks > 0);

        public void Update(Mat frame)
        {
            long now = Environment.TickCount64;
            for (; _clicksSeen < Input.Calls.Count; _clicksSeen++)
            {
                string c = Input.Calls[_clicksSeen];
                if (!c.StartsWith("Click ", StringComparison.Ordinal))
                    continue;
                var xy = c[6..].Split(' ')[0].Split(',');
                var p = new Point(int.Parse(xy[0]), int.Parse(xy[1]));
                foreach (var row in new[] { War, Farm })
                {
                    if (!row.Box.Contains(p))
                        continue;
                    row.Clicks++;
                    if (!row.Stuck)
                    {
                        row.Ticked = !row.Ticked;
                        row.ChangedAt = now;
                    }
                }
            }

            foreach (var row in new[] { War, Farm })
                if (now - row.ChangedAt >= ShowLagMs)
                    row.ShownTicked = row.Ticked;

            frame.SetTo(new MCvScalar(0, 0, 0, 255));
            if (!Open)
                return;

            foreach (var row in new[] { War, Farm })
            {
                CvInvoke.Rectangle(frame, row.Box, new MCvScalar(55, 175, 212, 255), 2);   // gold frame (BGR)
                if (row.ShownTicked)
                    CvInvoke.Rectangle(frame, new Rectangle(row.Box.X + 4, row.Box.Y + 4, 6, 6), new MCvScalar(240, 240, 240, 255), -1);
            }
        }
    }

    [Fact]
    public async Task Laby_RegularWarTicked_IsUnticked_ThenTheSettingsClose()
    {
        var (settings, input, ocr, scene) = Harness(war: true);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.False(scene.War.Ticked);
        Assert.Equal(1, scene.War.Clicks);
        Assert.Equal(["KeyTap S", WarClick, "KeyTap S"], input.Calls);
        Assert.False(scene.Open);
        Assert.Contains(run.Lines, l => l.Contains("Regular War unticked", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("Smart Farm is already off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothTicked_BothUnticked_ThenTheSettingsClose()
    {
        var (settings, input, ocr, scene) = Harness(war: true, farm: true);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.False(scene.War.Ticked);
        Assert.False(scene.Farm.Ticked);
        Assert.Equal(["KeyTap S", WarClick, FarmClick, "KeyTap S"], input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("Smart Farm unticked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SmartFarmTicked_IsUnticked_RegularWarLeftAlone()
    {
        var (settings, input, ocr, scene) = Harness(war: false, farm: true);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(["KeyTap S", FarmClick, "KeyTap S"], input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("Regular War is already off", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("Smart Farm unticked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothAlreadyOff_NothingClicked_TheSettingsClose()
    {
        var (settings, input, ocr, scene) = Harness(war: false);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(["KeyTap S", "KeyTap S"], input.Calls);
        Assert.False(scene.Open);
        Assert.Contains(run.Lines, l => l.Contains("Regular War is already off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBoxShowsItsChangeLate_OneClickOnly()
    {
        // Live 2026-11-01 16:51: clicked 0.9 s apart, both reads still ticked - a second click on a box that has already
        // changed ticks it again. The pilot waits for the change before clicking again.
        var (settings, input, ocr, scene) = Harness(war: true, showLagMs: 1500);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(1, scene.War.Clicks);
        Assert.False(scene.War.Ticked);
        Assert.Contains(run.Lines, l => l.Contains("Regular War unticked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoxStaysTicked_TwoClicksAtMost_WarningAndCarriesOn()
    {
        var (settings, input, ocr, scene) = Harness(war: true, warStuck: true);

        var run = await RunAsync(settings, input, ocr, scene, timeoutMs: 20000);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(2, scene.War.Clicks);   // even: a box that only reads wrong is left as it was
        Assert.Equal("KeyTap S", input.Calls[^1]);
        Assert.False(scene.Open);
        Assert.Contains(run.Lines, l => l.Contains("still reads ticked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SmartFarmUnread_RegularWarDone_WarningAndCarriesOn()
    {
        var (settings, input, ocr, scene) = Harness(war: true, farmReads: false);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(["KeyTap S", WarClick, "KeyTap S"], input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("Smart Farm", StringComparison.Ordinal) && l.Contains("not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsNeverShow_TwoPresses_WarningAndCarriesOn()
    {
        var (settings, input, ocr, scene) = Harness(war: true, neverOpens: true);

        var run = await RunAsync(settings, input, ocr, scene, timeoutMs: 20000);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        // even: a window that opened but never read is shut again by the second press
        Assert.Equal(["KeyTap S", "KeyTap S"], input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("could not be read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RunMode.AutoLod)]
    [InlineData(RunMode.AutoBlossom)]
    public async Task LodAndBlossom_AreCheckedToo(RunMode mode)
    {
        // User, 2026-11-02: every town visit, before the autohunt check - Smart Farm pulls the character back to a
        // hunting ground when 'J' is pressed in town, whatever the mode.
        var (settings, input, ocr, scene) = Harness(war: true, farm: true);
        settings.Sequence.Mode = mode;

        var run = await RunAsync(settings, input, ocr, scene, timeoutMs: 20000);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.False(scene.War.Ticked);
        Assert.False(scene.Farm.Ticked);
    }

    [Fact]
    public async Task BeforeTheAutohuntCheck()
    {
        var (settings, input, ocr, scene) = Harness(war: true, farm: true);
        settings.Sequence.AutohuntOffInTown = true;

        using var frame = Blank.Bgra(Height, Width);
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.CheckZone,
            (pilot, _) => pilot.Phase is PilotPhase.AutohuntOff or PilotPhase.OpenInventory or PilotPhase.Failed, 15000,
            beforeTick: () => scene.Update(frame));

        Assert.Equal(PilotPhase.AutohuntOff, run.Phase);
        Assert.Equal(["KeyTap S", WarClick, FarmClick, "KeyTap S"], input.Calls);   // no 'J' yet
        Assert.False(scene.Open);
    }

    [Fact]
    public async Task SwitchOff_NoCheck()
    {
        var (settings, input, ocr, scene) = Harness(war: true);
        settings.Sequence.RegularWarOffForLaby = false;

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.DoesNotContain("KeyTap S", input.Calls);
    }

    [Fact]
    public async Task LabelLostAfterTheClick_NoBlindKeyPress()
    {
        var (settings, input, ocr, scene) = Harness(war: true, labelLost: true);

        var run = await RunAsync(settings, input, ocr, scene, timeoutMs: 20000);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(["KeyTap S", WarClick], input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("went out of view", StringComparison.Ordinal));
    }

    private static (AppSettings, FakeGameInput, FakeOcrEngine, Scene) Harness(bool war, bool farm = false, bool warStuck = false,
        bool neverOpens = false, bool labelLost = false, bool farmReads = true, int showLagMs = 0)
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        settings.Sequence.AutohuntOffInTown = false;
        settings.Sequence.RunSkillCheck = false;
        settings.Sequence.FontSizePercent = 0;
        settings.Sequence.TabTargetDefault = false;
        settings.Blossom.TabTargetOption = string.Empty;

        var input = new FakeGameInput();
        var scene = new Scene
        {
            Input = input,
            War = new Row { Box = WarBox, Ticked = war, ShownTicked = war, Stuck = warStuck },
            Farm = new Row { Box = FarmBox, Ticked = farm, ShownTicked = farm },
            FarmReads = farmReads,
            NeverOpens = neverOpens,
            LabelLostAfterClick = labelLost,
            ShowLagMs = showLagMs,
        };
        var ocr = new FakeOcrEngine
        {
            Scene = () =>
            {
                var lines = new List<OcrWord> { FakeOcrEngine.Line("[Piercing City]", 700, 10) };
                if (scene.LabelsRead)
                {
                    lines.Add(FakeOcrEngine.Line("Regular War", WarLabel.X, WarLabel.Y, WarLabel.Width, WarLabel.Height));
                    if (scene.FarmReads)
                        lines.Add(FakeOcrEngine.Line("Smart Farm", FarmLabel.X, FarmLabel.Y, FarmLabel.Width, FarmLabel.Height));
                }

                return lines;
            },
        };
        return (settings, input, ocr, scene);
    }

    private static async Task<(PilotPhase Phase, List<string> Lines)> RunAsync(AppSettings settings, FakeGameInput input,
        FakeOcrEngine ocr, Scene scene, int timeoutMs = 15000)
    {
        using var frame = Blank.Bgra(Height, Width);
        return await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.CheckZone,
            (pilot, _) => pilot.Phase is PilotPhase.OpenInventory or PilotPhase.Failed, timeoutMs,
            beforeTick: () => scene.Update(frame));
    }
}
