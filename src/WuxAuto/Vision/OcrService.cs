using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenCvSharp;
using WuxAuto.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinOcrWord = Windows.Media.Ocr.OcrWord;

namespace WuxAuto.Vision;

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

/// <summary>Small fuzzy matcher for OCR output ("Labyrinth" vs "Labyrinlh").</summary>
public static class TextMatcher
{
    public static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int count = 0;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[count++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..count]);
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    public static double Similarity(string a, string b)
    {
        string left = Normalize(a);
        string right = Normalize(b);
        if (left.Length == 0 || right.Length == 0)
            return 0;

        if (left == right)
            return 1.0;

        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            // Substring containment is how "Labyrinth" matches "[The Labyrinth (Adept)]" in OCR
            // output, but it must not turn a stray letter or two into a match for a boss name.
            int shortest = Math.Min(left.Length, right.Length);
            if (shortest >= 4)
            {
                double ratio = shortest / (double)Math.Max(left.Length, right.Length);
                return 0.80 + 0.15 * ratio;
            }
        }

        int distance = Levenshtein(left, right);
        return 1.0 - distance / (double)Math.Max(left.Length, right.Length);
    }

    /// <summary>
    /// True when <paramref name="read"/> has at least 60% of the letters of <paramref name="target"/>.
    /// Containment matching accepts any 4+ letter piece ("open" for "DUNGEON GATE OPENED",
    /// "[Rage" for "Ragesoul Heathen"), which is wrong wherever such words appear elsewhere.
    /// </summary>
    public static bool CoversMost(string read, string target)
        => Normalize(read).Length >= Normalize(target).Length * 0.6;

    /// <summary>Picks the word (or line) that best matches the target text.</summary>
    public static TextHit? FindBest(IReadOnlyList<OcrWord> words, string target, double minSimilarity, int? anchorX = null, int? anchorY = null, double ocrMs = 0)
    {
        if (words.Count == 0 || string.IsNullOrWhiteSpace(target))
            return null;

        var candidates = new List<TextHit>();

        foreach (var word in words)
        {
            double similarity = Similarity(word.Text, target);
            if (similarity >= minSimilarity)
                candidates.Add(new TextHit(word.Text, similarity, word.Bounds, word.LineText, word.LineBounds, ocrMs));
        }

        // OCR sometimes splits a label; fall back to the whole line.
        foreach (var line in words.GroupBy(w => w.LineText))
        {
            double similarity = Similarity(line.Key, target);
            if (similarity >= minSimilarity)
            {
                var first = line.First();
                candidates.Add(new TextHit(line.Key, similarity * 0.98, first.LineBounds, line.Key, first.LineBounds, ocrMs));
            }
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.Similarity).First();
        if (anchorX.HasValue && anchorY.HasValue)
        {
            var nearby = candidates
                .Where(c => c.Similarity >= best.Similarity - 0.05)
                .OrderBy(c => c.Bounds.DistanceTo(anchorX.Value, anchorY.Value))
                .First();
            best = nearby;
        }

        return best;
    }
}
