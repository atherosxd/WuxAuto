using OpenCvSharp;
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

    public string Status => "fake";
    public bool IsAvailable => true;
    public double LastDurationMs => 0;
    public bool EnsureInitialized() => true;

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(Mat bgra, PixelRect area, double upscale)
    {
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
}

/// <summary>Records every input in order ("KeyTap J", "Click 720,450"); the game is always in front.</summary>
sealed class FakeGameInput : IGameInput
{
    public List<string> Calls { get; } = [];
    public int Clicks { get; private set; }

    public bool IsDryRun => false;
    public bool IsTargetForeground => true;
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
