// FROZEN COPY for the Emgu parity probe: the pre-port OpenCvSharp vision code. Do not edit.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenCvSharp;
using WuxAuto.Core;
using OcrWord = WuxAuto.Vision.OcrWord;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinOcrWord = Windows.Media.Ocr.OcrWord;

namespace WuxAuto.Legacy;

/// <summary>
/// In-process OCR through the Windows OCR engine (Windows.Media.Ocr). No external runtime
/// to deploy and typical latency for a UI-sized crop is in the tens of milliseconds.
/// </summary>
public sealed class OcrService : IDisposable
{
    private readonly object _gate = new();
    private OcrEngine? _engine;

    public string Status { get; private set; } = "not initialised";
    public bool IsAvailable => _engine != null;
    public double LastDurationMs { get; private set; }

    public bool EnsureInitialized()
    {
        if (_engine != null)
            return true;

        lock (_gate)
        {
            if (_engine != null)
                return true;

            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();

                if (_engine == null)
                {
                    var languages = OcrEngine.AvailableRecognizerLanguages;
                    var english = languages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                    if (english != null)
                        _engine = OcrEngine.TryCreateFromLanguage(english);

                    if (_engine == null)
                    {
                        Status = languages.Count == 0
                            ? "No OCR language pack installed (Settings > Time & language > Language > English)."
                            : $"No usable OCR language. Available: {string.Join(", ", languages.Select(l => l.LanguageTag))}";
                        return false;
                    }
                }

                Status = $"Windows OCR ready ({_engine.RecognizerLanguage.LanguageTag})";
                return true;
            }
            catch (Exception ex)
            {
                Status = $"OCR unavailable: {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>Runs OCR over a client-space region and returns word boxes in client coordinates.</summary>
    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(Mat bgra, PixelRect area, double upscale)
    {
        var results = new List<OcrWord>();
        if (!EnsureInitialized())
            return results;

        area = area.Clamp(bgra.Width, bgra.Height);
        if (area.Width < 12 || area.Height < 12 || upscale <= 0)
            return results;

        Mat? resized = null;
        Mat? converted = null;
        SoftwareBitmap? bitmap = null;

        try
        {
            using var view = new Mat(bgra, new Rect(area.X, area.Y, area.Width, area.Height));

            Mat source = view;
            if (Math.Abs(upscale - 1.0) > 0.01)
            {
                resized = new Mat();
                Cv2.Resize(view, resized, new Size(0, 0), upscale, upscale, InterpolationFlags.Cubic);
                source = resized;
            }

            if (source.Channels() != 4)
            {
                converted = new Mat();
                if (source.Channels() == 3)
                    Cv2.CvtColor(source, converted, ColorConversionCodes.BGR2BGRA);
                else
                    Cv2.CvtColor(source, converted, ColorConversionCodes.GRAY2BGRA);
                source = converted;
            }

            int width = source.Width;
            int height = source.Height;
            int stride = width * 4;
            var bytes = new byte[stride * height];

            if (source.IsContinuous())
            {
                Marshal.Copy(source.Data, bytes, 0, bytes.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr row = source.Data + y * (int)source.Step();
                    Marshal.Copy(row, bytes, y * stride, stride);
                }
            }

            bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                bytes.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                width,
                height,
                BitmapAlphaMode.Premultiplied);

            var started = Environment.TickCount64;
            var ocr = await _engine!.RecognizeAsync(bitmap);
            LastDurationMs = Environment.TickCount64 - started;

            foreach (var line in ocr.Lines)
            {
                if (line.Words.Count == 0)
                    continue;

                var lineBounds = UnionOf(line.Words, upscale, area);
                foreach (var word in line.Words)
                {
                    var bounds = ToPixelRect(word.BoundingRect, upscale, area);
                    results.Add(new OcrWord(word.Text, bounds, line.Text, lineBounds));
                }
            }

            return results;
        }
        catch (Exception)
        {
            return results;
        }
        finally
        {
            bitmap?.Dispose();
            converted?.Dispose();
            resized?.Dispose();
        }
    }

    private static PixelRect ToPixelRect(Windows.Foundation.Rect rect, double upscale, PixelRect area)
    {
        int x = area.X + (int)Math.Round(rect.X / upscale);
        int y = area.Y + (int)Math.Round(rect.Y / upscale);
        int w = Math.Max(1, (int)Math.Round(rect.Width / upscale));
        int h = Math.Max(1, (int)Math.Round(rect.Height / upscale));
        return new PixelRect(x, y, w, h);
    }

    private static PixelRect UnionOf(IReadOnlyList<WinOcrWord> words, double upscale, PixelRect area)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var word in words)
        {
            left = Math.Min(left, word.BoundingRect.X);
            top = Math.Min(top, word.BoundingRect.Y);
            right = Math.Max(right, word.BoundingRect.X + word.BoundingRect.Width);
            bottom = Math.Max(bottom, word.BoundingRect.Y + word.BoundingRect.Height);
        }

        return ToPixelRect(new Windows.Foundation.Rect(left, top, right - left, bottom - top), upscale, area);
    }

    public void Dispose()
    {
        _engine = null;
    }
}
