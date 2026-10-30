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
}
