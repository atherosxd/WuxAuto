using Emgu.CV;
using WuxAuto.Vision;

namespace WuxAuto.Core;

/// <summary>Text recognition on a region of a captured frame; the platform supplies the engine.</summary>
public interface IOcrEngine
{
    string Status { get; }
    bool IsAvailable { get; }
    double LastDurationMs { get; }
    bool EnsureInitialized();
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(Mat bgra, PixelRect area, double upscale);
}
