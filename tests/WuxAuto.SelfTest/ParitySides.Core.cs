using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.SelfTest;

/// <summary>
/// The --parity probe's new side: Core's vision (src/WuxAuto.Core) with the desktop OCR engine, as
/// the app builds it. Decodes its own frames; nothing image-typed leaves this class. This is the
/// only probe file that changes when Core's imaging library does.
/// </summary>
internal sealed class CoreSide : IVisionSide
{
    private readonly ScreenAnalyzer _analyzer = new(new AppSettings(), new OcrService());
    private Mat? _frame;

    private Mat Frame => _frame ?? throw new InvalidOperationException("no frame loaded");

    public FrameInfo? Load(string path)
    {
        _frame?.Dispose();
        _frame = null;
        using var source = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (source.Empty())
            return null;

        var frame = new Mat();
        switch (source.Channels())
        {
            case 4:
                source.CopyTo(frame);
                break;
            case 3:
                Cv2.CvtColor(source, frame, ColorConversionCodes.BGR2BGRA);
                break;
            case 1:
                Cv2.CvtColor(source, frame, ColorConversionCodes.GRAY2BGRA);
                break;
            default:
                frame.Dispose();
                return null;
        }

        _frame = frame;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int height = frame.Height;
        var row = new byte[frame.Width * frame.ElemSize()];
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(frame.Ptr(y), row, 0, row.Length);
            hash.AppendData(row);
        }

        return new FrameInfo(frame.Width, frame.Height, Convert.ToHexString(hash.GetHashAndReset())[..16]);
    }

    public bool HasTemplate(string name) => _analyzer.Library.TryGet(name, out _);
    public TemplateMatch? FindIcon(string template, PixelRect roi, double? threshold) => _analyzer.FindIcon(Frame, template, roi, threshold);
    public IReadOnlyList<TemplateMatch> FindIcons(string template, PixelRect roi, double? threshold, int maxResults) => _analyzer.FindIcons(Frame, template, roi, threshold, maxResults);
    public TemplateMatch? FindIconAnyScale(string template, PixelRect roi, double threshold) => _analyzer.FindIconAnyScale(Frame, template, roi, threshold).Match;
    public async Task<TextHit?> FindTextAsync(PixelRect roi, string text) => (await _analyzer.FindTextAsync(Frame, roi, text)).Hit;
    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(PixelRect roi, double upscale) => _analyzer.Ocr.RecognizeAsync(Frame, roi, upscale);

    public async Task<CoordinateRead?> ReadCoordinatesAsync()
        => await _analyzer.ReadCoordinatesAsync(Frame) is { } c ? new CoordinateRead(c.X, c.Y, c.Bounds, c.Raw) : null;

    public CompassReading? ReadCompass() => _analyzer.ReadCompass(Frame);
    public PixelRect CompassRegion() => _analyzer.CompassRegion(Frame.Width, Frame.Height);
    public PixelRect? FindSelectionRing() => _analyzer.FindSelectionRing(Frame);
    public PixelRect? FindBossRing() => _analyzer.FindBossRing(Frame);
    public PixelRect? FindBossBelt() => _analyzer.FindBossBelt(Frame);
    public double RingFraction(PixelRect icon) => _analyzer.RingFraction(Frame, icon);
    public IReadOnlyList<PixelRect> FindGoldButtons() => _analyzer.FindGoldButtons(Frame);
    public PixelRect? FindRedOrangeGlow(PixelRect roi, int minArea) => _analyzer.FindRedOrangeGlow(Frame, roi, minArea);
    public Task<(PixelRect? Up, PixelRect? Down)> FindMapScrollArrowsAsync(PixelRect roi) => _analyzer.FindMapScrollArrowsAsync(Frame, roi);
    public Task<ConfirmDialog?> FindConfirmDialogAsync(string text, string button) => _analyzer.FindConfirmDialogAsync(Frame, text, button);
    public Task<SettingRow?> ReadSettingRowAsync(string label, bool numeric) => _analyzer.ReadSettingRowAsync(Frame, label, numeric);
    public PixelRect FindCheckboxLeftOf(PixelRect label) => _analyzer.FindCheckboxLeftOf(Frame, label);
    public bool IsCheckboxTicked(PixelRect box) => _analyzer.IsCheckboxTicked(Frame, box);
    public PixelRect FindCloseButtonRightOf(PixelRect title) => _analyzer.FindCloseButtonRightOf(Frame, title);
    public IReadOnlyList<PixelRect> FindBrightBlobs(PixelRect roi) => _analyzer.FindBrightBlobs(Frame, roi);
    public ulong FrameSignature(PixelRect roi) => WuxAuto.Vision.FrameSignature.Compute(Frame, roi);

    public void Dispose()
    {
        _frame?.Dispose();
        _analyzer.Dispose();
    }
}
