using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// OCR that "reads" a fixed scene: every word placed on the frame whose box touches the asked
/// area comes back, with absolute frame coordinates like the real engine. One word per line.
/// </summary>
sealed class FakeOcrEngine : IOcrEngine
{
    public Func<IReadOnlyList<OcrWord>> Scene { get; set; } = () => [];

    /// <summary>How many reads were asked for so far.</summary>
    public int Reads { get; private set; }

    public string Status => "fake";
    public bool IsAvailable => true;
    public double LastDurationMs => 0;
    public bool EnsureInitialized() => true;

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(Mat bgra, PixelRect area, double upscale)
    {
        Reads++;
        IReadOnlyList<OcrWord> hits = Scene()
            .Where(w => w.Bounds.X < area.Right && w.Bounds.Right > area.X
                        && w.Bounds.Y < area.Bottom && w.Bounds.Bottom > area.Y)
            .ToList();
        return Task.FromResult(hits);
    }

    public static OcrWord Line(string text, int x, int y, int width = 200, int height = 14)
    {
        var box = new PixelRect(x, y, width, height);
        return new OcrWord(text, box, text, box);
    }

    /// <summary>One line read as separate words, the way the real engine reads it: each word's box cut from the line's by its characters.</summary>
    public static IEnumerable<OcrWord> Words(string text, int x, int y, int width = 200, int height = 14)
    {
        var line = new PixelRect(x, y, width, height);
        int at = 0;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int start = text.IndexOf(word, at, StringComparison.Ordinal);
            at = start + word.Length;
            int left = x + width * start / text.Length;
            int right = x + width * at / text.Length;
            yield return new OcrWord(word, new PixelRect(left, y, Math.Max(1, right - left), height), text, line);
        }
    }
}

/// <summary>
/// Records every input in order ("KeyTap J", "Click 720,450"); the game is in front unless <see cref="InFront"/> is
/// cleared (inputs are still recorded then - the pilot is expected not to send any).
/// </summary>
sealed class FakeGameInput : IGameInput
{
    public List<string> Calls { get; } = [];
    public int Clicks { get; private set; }

    /// <summary>Whether the game window is in front.</summary>
    public bool InFront { get; set; } = true;

    /// <summary>A dry run: the pilot is expected to send nothing (its calls are still recorded).</summary>
    public bool DryRun { get; set; }

    public bool IsDryRun => DryRun;
    public bool IsTargetForeground => InFront;
    public bool TryFocus(int timeoutMs = 1200) => true;

    public bool KeyTap(string keyName) => Record($"KeyTap {keyName}");
    public bool KeyDown(string keyName) => Record($"KeyDown {keyName}");
    public bool KeyUp(string keyName) => Record($"KeyUp {keyName}");
    public bool CtrlKeyTap(string keyName) => Record($"CtrlKeyTap {keyName}");
    public bool CtrlClick(int clientX, int clientY) => Record($"CtrlClick {clientX},{clientY}");
    public bool CtrlWheel(int clientX, int clientY, int notches) => Record($"CtrlWheel {clientX},{clientY},{notches}");
    public bool Drag(int fromX, int fromY, int toX, int toY, int durationMs = 260) => Record($"Drag {fromX},{fromY}->{toX},{toY}");
    public bool Wheel(int clientX, int clientY, int notches) => Record($"Wheel {clientX},{clientY},{notches}");

    public bool Click(int clientX, int clientY, MouseButton button = MouseButton.Left)
    {
        Clicks++;
        return Record($"Click {clientX},{clientY}" + (button == MouseButton.Left ? "" : $" {button}"));
    }

    private bool Record(string call)
    {
        Calls.Add(call);
        return true;
    }
}

/// <summary>
/// Ticks a real pilot over one fixed frame from <paramref name="startAt"/> until <c>done</c> (given the
/// pilot and its log so far) or the time-out, with an empty templates folder (things are found by name
/// or by colour) - or the templates <c>assets</c> writes into the folder it is given. Returns the last
/// phase and the log.
/// </summary>
static class PilotRun
{
    public static async Task<(PilotPhase Phase, List<string> Lines)> RunAsync(AppSettings settings, IGameInput input,
        IOcrEngine ocr, Mat frame, PilotPhase startAt, Func<AutomationPilot, IReadOnlyList<string>, bool> done, int timeoutMs = 15000,
        Action<string>? assets = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            if (assets != null)
            {
                Directory.CreateDirectory(AppPaths.AssetsFolder);
                assets(AppPaths.AssetsFolder);
            }

            using var analyzer = new ScreenAnalyzer(settings, ocr);
            var pilot = new AutomationPilot(settings, analyzer, new KeyboardMouseControls(settings, input));
            var lines = new List<string>();
            pilot.Log += (_, text) => lines.Add(text);
            pilot.Start(startAt);

            long deadline = Environment.TickCount64 + timeoutMs;
            while (!done(pilot, lines) && pilot.IsRunning && Environment.TickCount64 < deadline)
            {
                await pilot.TickAsync(frame, CancellationToken.None);
                await Task.Delay(20);
            }

            var phase = pilot.Phase;
            pilot.Stop("test over");
            return (phase, lines);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}

/// <summary>Black test frames: cv::Mat(rows, cols, type, Scalar::all(0)), allocated and filled by OpenCV.</summary>
static class Blank
{
    public static Mat Bgra(int rows, int cols) => Filled(rows, cols, 4);

    public static Mat Gray(int rows, int cols) => Filled(rows, cols, 1);

    private static Mat Filled(int rows, int cols, int channels)
    {
        var mat = new Mat(rows, cols, DepthType.Cv8U, channels);
        mat.SetTo(new MCvScalar(0, 0, 0, 0));
        return mat;
    }
}
