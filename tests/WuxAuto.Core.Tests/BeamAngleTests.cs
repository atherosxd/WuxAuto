using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The phone's fast north turn (user, 2026-10-30: "it is too slow to rotate try to rotate faster"): which way the
/// searchlight points (<see cref="ScreenAnalyzer.BeamAngle"/>), and the one long turn the pilot makes from it.
/// </summary>
public class BeamAngleTests : AppPathsScope
{
    // The phone's searchlight box at 1920x1080 (MuMu: the minimap line at 1656,107) and the beam's place and scale in it.
    static readonly PixelRect Box = new(1531, 107, 360, 375);
    const int BeamCx = 1662, BeamCy = 178;
    const double BeamScale = 1.5;

    static string DesktopAssets => Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>A dark frame with flashlight3 drawn at x1.5, turned <paramref name="angleCw"/> degrees clockwise.</summary>
    static Mat BeamFrame(ScreenAnalyzer analyzer, int angleCw)
    {
        var frame = new Mat(1080, 1920, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(18, 18, 18, 255));
        var template = analyzer.Library.Get("flashlight3")!;
        using var scaled = new Mat();
        CvInvoke.Resize(template.Gray, scaled, new Size((int)Math.Round(template.Width * BeamScale), (int)Math.Round(template.Height * BeamScale)), 0, 0, Inter.Cubic);
        int side = (int)Math.Ceiling(Math.Sqrt(scaled.Width * scaled.Width + scaled.Height * scaled.Height)) + 2;
        using var square = new Mat();
        CvInvoke.CopyMakeBorder(scaled, square, (side - scaled.Height) / 2, side - scaled.Height - (side - scaled.Height) / 2,
            (side - scaled.Width) / 2, side - scaled.Width - (side - scaled.Width) / 2, BorderType.Constant, new MCvScalar(18));
        using var rotation = new Mat();
        using var turned = new Mat();
        CvInvoke.GetRotationMatrix2D(new PointF(side / 2f, side / 2f), -angleCw, 1.0, rotation); // OpenCV: + = anticlockwise
        CvInvoke.WarpAffine(square, turned, rotation, new Size(side, side), Inter.Linear, Warp.Default, BorderType.Constant, new MCvScalar(18));
        using var bgra = new Mat();
        CvInvoke.CvtColor(turned, bgra, ColorConversion.Gray2Bgra);
        using (var target = new Mat(frame, new Rectangle(BeamCx - side / 2, BeamCy - side / 2, side, side)))
            bgra.CopyTo(target);
        return frame;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(165)]
    [InlineData(270)]
    [InlineData(330)]
    public void BeamAngle_ReadsTheTurn_Clockwise(int angleCw)
    {
        AppPaths.Configure(TempRoot(), DesktopAssets);
        using var analyzer = new ScreenAnalyzer(new AppSettings(), new FakeOcrEngine());
        using var frame = BeamFrame(analyzer, angleCw);

        var beam = analyzer.BeamAngle(frame, "flashlight3", Box, BeamScale);

        Assert.NotNull(beam);
        Assert.Equal(angleCw, beam.Value.AngleCw);
        Assert.True(beam.Value.Score > 0.9, $"score {beam.Value.Score:0.000}");
    }

    [Fact]
    public void BeamAngle_NoTemplate_Null()
    {
        AppPaths.Configure(TempRoot(), DesktopAssets);
        using var analyzer = new ScreenAnalyzer(new AppSettings(), new FakeOcrEngine());
        using var frame = new Mat(1080, 1920, DepthType.Cv8U, 4);
        Assert.Null(analyzer.BeamAngle(frame, "no_such_template", Box, BeamScale));
    }

    [Theory]
    // beam 90 deg clockwise of north: one turn left of 75 deg (leaves 15 clockwise for the taps); finger moves left.
    [InlineData(90, "left", 75)]
    // 60 deg anticlockwise of north: one turn right of 75 deg (lands 15 clockwise of north).
    [InlineData(300, "right", 75)]
    // 180: the longest single turn is 100 deg, then it looks again.
    [InlineData(180, "left", 100)]
    public async Task Phone_FaceNorth_OneLongTurn_TowardsTheHeading(int angleCw, string way, int degrees)
    {
        string root = TempRoot();
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        using var phone = new PhonePilot(root, settings, DesktopAssets);
        // The minimap's coordinate line anchors the searchlight box (Box above).
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 60), FakeOcrEngine.Line("3595 , 3415", 1656, 107, 110, 15)];
        using var frame = BeamFrame(phone.Analyzer, angleCw);

        phone.Pilot.Start(PilotPhase.FaceNorth);
        long deadline = Environment.TickCount64 + 20000;
        while (!phone.Lines.Any(l => l.Contains("one long turn", StringComparison.Ordinal)) && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        phone.Pilot.Stop("test over");
        Assert.True(phone.Lines.Any(l => l.Contains($"one long turn {way} of ~{degrees} degrees (1/3)", StringComparison.Ordinal)), phone.Log);
        string drag = Assert.Single(phone.Touch.Calls, c => c.StartsWith("Drag ", StringComparison.Ordinal));
        // "Drag x1,y->x2,y": px = 20 + 4.8 x degrees, cut to the turn band; the finger moves the way the camera turns.
        var parts = drag[5..].Split("->");
        int x1 = int.Parse(parts[0].Split(',')[0]), x2 = int.Parse(parts[1].Split(',')[0]);
        int expected = Math.Min((int)Math.Round(20 + 4.8 * degrees), (int)Math.Round(1920 * 0.88) - (int)Math.Round(1920 * 0.60));
        Assert.InRange(Math.Abs(x2 - x1), expected - 4, expected + 4);
        Assert.Equal(way == "left", x2 < x1);
    }

    [Fact]
    public async Task Phone_FaceNorth_AlmostLinedUp_NoLongTurn_TapsOnly()
    {
        string root = TempRoot();
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        using var phone = new PhonePilot(root, settings, DesktopAssets);
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 60), FakeOcrEngine.Line("3595 , 3415", 1656, 107, 110, 15)];
        using var frame = BeamFrame(phone.Analyzer, 15);

        phone.Pilot.Start(PilotPhase.FaceNorth);
        long deadline = Environment.TickCount64 + 20000;
        while (!phone.Touch.Calls.Any(c => c.StartsWith("Drag ", StringComparison.Ordinal)) && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        phone.Pilot.Stop("test over");
        Assert.DoesNotContain(phone.Lines, l => l.Contains("one long turn", StringComparison.Ordinal));
        // The first drag is a tap's (180 ms = 90 px, or the 90 ms half tap near the gate = 45 px), not a long turn.
        string drag = phone.Touch.Calls.First(c => c.StartsWith("Drag ", StringComparison.Ordinal));
        var parts = drag[5..].Split("->");
        Assert.InRange(Math.Abs(int.Parse(parts[1].Split(',')[0]) - int.Parse(parts[0].Split(',')[0])), 40, 94);
    }

    [Theory]
    // live MuMu 2026-10-30 at the Labyrinth's entrance, the beam cut by the minimap's bottom edge (true ~100 degrees)
    [InlineData(new[] { 75, 105, 120 }, new[] { 0.841, 0.780, 0.725 }, 75)]
    // lined up and 15 degrees off on the open minimap: over the gate
    [InlineData(new[] { 0, 0, 0 }, new[] { 0.912, 0.969, 0.991 }, 0)]
    [InlineData(new[] { 15, 15, 15 }, new[] { 0.915, 0.971, 0.992 }, 15)]
    // under the gate with no clear lead: two far angles score alike
    [InlineData(new[] { 75, 255 }, new[] { 0.80, 0.78 }, null)]
    // under the floor
    [InlineData(new[] { 90 }, new[] { 0.70 }, null)]
    // a near neighbour (< 30 degrees) is no rival
    [InlineData(new[] { 90, 105 }, new[] { 0.75, 0.74 }, 90)]
    public void TrustedBeam_ClearWinnerOnly(int[] angles, double[] scores, int? expected)
    {
        var reads = angles.Zip(scores, (a, s) => (a, s)).ToList();
        Assert.Equal(expected, AutomationPilot.TrustedBeam(reads, 0.85)?.AngleCw);
    }

    /// <summary>A phone Labyrinth pilot on a grey frame (no beam to read: the turn goes on by taps) with the minimap's title.</summary>
    PhonePilot TurningPhone(Action<AppSettings>? tweak = null)
    {
        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        tweak?.Invoke(settings);
        var phone = new PhonePilot(TempRoot(), settings, DesktopAssets);
        phone.Shortcuts.Icons[GameAction.TargetNearest] = new PixelRect(1595, 755, 120, 120); // the sword (Tab)
        phone.Ocr.Scene = () => [FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 60), FakeOcrEngine.Line("3595 , 3415", 1656, 107, 110, 15)];
        return phone;
    }

    [Fact]
    public async Task Phone_ArrivalTurn_GivesWayToTheFirstGate_TheMapFirst()
    {
        // User, 2026-10-30: "it is so slow to turn, while turning keep track on the timer as per wiki and also keep track
        // of gate opened ... open the map and click the boss". 1 s here instead of 30.
        using var phone = TurningPhone();
        phone.Pilot.LabyFirstGateMs = 1000;
        using var frame = PhoneFrames.Grey();

        phone.Pilot.Start(PilotPhase.ZoomOut, stageIndex: 0);
        long deadline = Environment.TickCount64 + 20000;
        while (phone.Pilot.Phase != PilotPhase.ClickMap && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        var phase = phone.Pilot.Phase;
        phone.Pilot.Stop("test over");
        Assert.True(phase == PilotPhase.ClickMap, phone.Log);
        Assert.Contains(phone.Phases, p => p == PilotPhase.FaceNorth); // it was turning
        Assert.Contains(phone.Lines, l => l.Contains("The stage 1 gate is open (its fixed timer, 30 s after arriving) while turning", StringComparison.Ordinal));
        Assert.True(phone.Pilot.NorthPending);
    }

    [Fact]
    public async Task Phone_PendingTurn_DoneDuringTheBossWait_TheSpawnClockKept()
    {
        using var phone = TurningPhone(s =>
        {
            s.Dungeon.BossSpawnWaitMs = 20000;
            s.Sequence.AlignMaxAttempts = 3; // no beam: three taps, then "carrying on"
        });
        using var frame = PhoneFrames.Grey();

        phone.Pilot.Start(PilotPhase.WaitBoss, stageIndex: 0);
        phone.Pilot.NorthPending = true; // as the arrival turn left it (Start clears it)
        long started = Environment.TickCount64;
        long backAt = 0;
        long deadline = started + 40000;
        while (!phone.Lines.Any(l => l.Contains("Pressed 'Tab'", StringComparison.Ordinal)) && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            if (backAt == 0 && phone.Phases.Count(p => p == PilotPhase.WaitBoss) >= 2)
                backAt = Environment.TickCount64;
            await Task.Delay(15);
        }

        long tabAt = Environment.TickCount64 - started;
        phone.Pilot.Stop("test over");
        Assert.Contains(phone.Lines, l => l.Contains("Lining the searchlight up while the stage 1 boss spawns", StringComparison.Ordinal));
        Assert.Equal([PilotPhase.WaitBoss, PilotPhase.FaceNorth, PilotPhase.WaitBoss], phone.Phases.Take(3));
        Assert.True(backAt > 0, phone.Log);
        Assert.False(phone.Pilot.NorthPending);
        // The Tab comes 20 s after the wait began, not 20 s after the turn came back.
        Assert.InRange(tabAt, 19000, 23000);
    }
}
