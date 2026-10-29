using System.Drawing;
using Emgu.CV;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto Laby, in town before the teleport (user, 2026-10-29: "check if the regular war is turned on in the
/// autohunt, disable it if enabled, as it may get teleported to the war when the autohunt is on while
/// attacking the boss"): S opens the autohunt settings, a ticked "Regular War" box is clicked until it reads
/// empty, S closes the window, then the teleport. The frame draws the gold box left of the label (OCR fake);
/// S toggles the window, a click inside the box toggles the tick (unless the box is "stuck").
/// </summary>
[Collection("AppPaths")] // AppPaths is static state
public class RegularWarOffTests
{
    private const int Width = 1024;
    private const int Height = 768;
    private static readonly Rectangle Label = new(500, 300, 90, 14);
    private static readonly Rectangle Box = new(474, 300, 14, 14);

    private sealed class Scene
    {
        public required FakeGameInput Input { get; init; }
        public bool Ticked { get; set; }
        public bool Stuck { get; init; }           // the click never changes the box
        public bool NeverOpens { get; init; }      // S shows nothing (no settings window)
        public bool LabelLostAfterClick { get; init; }   // the label stops reading once the box was clicked
        public int BoxClicks { get; set; }
        private int _clicksSeen;

        public bool Open => !NeverOpens && Input.Calls.Count(c => c == "KeyTap S") % 2 == 1;
        public bool LabelReads => Open && !(LabelLostAfterClick && BoxClicks > 0);

        public void Update(Mat frame)
        {
            // clicks since the last tick that landed in the box toggle it
            for (; _clicksSeen < Input.Calls.Count; _clicksSeen++)
            {
                string c = Input.Calls[_clicksSeen];
                if (!c.StartsWith("Click ", StringComparison.Ordinal))
                    continue;
                var xy = c[6..].Split(' ')[0].Split(',');
                var p = new Point(int.Parse(xy[0]), int.Parse(xy[1]));
                if (Box.Contains(p))
                {
                    BoxClicks++;
                    if (!Stuck)
                        Ticked = !Ticked;
                }
            }

            frame.SetTo(new MCvScalar(0, 0, 0, 255));
            if (!Open)
                return;

            CvInvoke.Rectangle(frame, Box, new MCvScalar(55, 175, 212, 255), 2);   // gold frame (BGR)
            if (Ticked)
                CvInvoke.Rectangle(frame, new Rectangle(Box.X + 4, Box.Y + 4, 6, 6), new MCvScalar(240, 240, 240, 255), -1);
        }
    }

    [Fact]
    public async Task Laby_RegularWarTicked_IsUnticked_ThenTheSettingsClose()
    {
        var (settings, input, ocr, scene) = Harness(ticked: true);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.False(scene.Ticked);
        Assert.Equal(1, scene.BoxClicks);
        Assert.Equal(["KeyTap S", $"Click {Box.X + Box.Width / 2},{Box.Y + Box.Height / 2}", "KeyTap S"], input.Calls);
        Assert.False(scene.Open);
        Assert.Contains(run.Lines, l => l.Contains("Regular War unticked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Laby_RegularWarAlreadyOff_NothingClicked_TheSettingsClose()
    {
        var (settings, input, ocr, scene) = Harness(ticked: false);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(["KeyTap S", "KeyTap S"], input.Calls);
        Assert.False(scene.Open);
        Assert.Contains(run.Lines, l => l.Contains("Regular War is already off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Laby_BoxStaysTicked_TwoClicksAtMost_WarningAndCarriesOn()
    {
        var (settings, input, ocr, scene) = Harness(ticked: true, stuck: true);

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(2, scene.BoxClicks);   // even: a box that only reads wrong is left as it was
        Assert.Equal("KeyTap S", input.Calls[^1]);
        Assert.False(scene.Open);
        Assert.Contains(run.Lines, l => l.Contains("still reads ticked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Laby_SettingsNeverShow_TwoPresses_WarningAndCarriesOn()
    {
        var (settings, input, ocr, scene) = Harness(ticked: true, neverOpens: true);

        var run = await RunAsync(settings, input, ocr, scene, timeoutMs: 20000);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        // even: a window that opened but never read is shut again by the second press
        Assert.Equal(["KeyTap S", "KeyTap S"], input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lod_NoRegularWarCheck()
    {
        var (settings, input, ocr, scene) = Harness(ticked: true);
        settings.Sequence.Mode = RunMode.AutoLod;

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.DoesNotContain("KeyTap S", input.Calls);
        Assert.True(scene.Ticked);
    }

    [Fact]
    public async Task SwitchOff_NoRegularWarCheck()
    {
        var (settings, input, ocr, scene) = Harness(ticked: true);
        settings.Sequence.RegularWarOffForLaby = false;

        var run = await RunAsync(settings, input, ocr, scene);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.DoesNotContain("KeyTap S", input.Calls);
    }

    [Fact]
    public async Task Laby_LabelLostAfterTheClick_NoBlindKeyPress()
    {
        var (settings, input, ocr, scene) = Harness(ticked: true, labelLost: true);

        var run = await RunAsync(settings, input, ocr, scene, timeoutMs: 20000);

        Assert.Equal(PilotPhase.OpenInventory, run.Phase);
        Assert.Equal(["KeyTap S", $"Click {Box.X + Box.Width / 2},{Box.Y + Box.Height / 2}"], input.Calls);
        Assert.Contains(run.Lines, l => l.Contains("went out of view", StringComparison.Ordinal));
    }

    private static (AppSettings, FakeGameInput, FakeOcrEngine, Scene) Harness(bool ticked, bool stuck = false, bool neverOpens = false,
        bool labelLost = false)
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        settings.Sequence.AutohuntOffInTown = false;
        settings.Sequence.RunSkillCheck = false;
        settings.Sequence.FontSizePercent = 0;
        settings.Sequence.TabTargetDefault = false;

        var input = new FakeGameInput();
        var scene = new Scene { Input = input, Ticked = ticked, Stuck = stuck, NeverOpens = neverOpens, LabelLostAfterClick = labelLost };
        var ocr = new FakeOcrEngine
        {
            Scene = () => scene.LabelReads
                ?
                [
                    FakeOcrEngine.Line("[Piercing City]", 700, 10),
                    FakeOcrEngine.Line("Regular War", Label.X, Label.Y, Label.Width, Label.Height),
                ]
                : [FakeOcrEngine.Line("[Piercing City]", 700, 10)],
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
