using System.Diagnostics;
using Android.Gms.Extensions;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Core;
using WuxAuto.Vision;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Latin;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace WuxAuto.Mobile;

/// <summary>
/// <see cref="IOcrEngine"/> through ML Kit's on-device Latin text recognizer (the bundled model, no download). Like the
/// desktop's OcrService: the region is cut out of the frame, upscaled (cubic), recognised, and every word (ML Kit
/// "element") comes back as an <see cref="OcrWord"/> with its line's text and bounds, in frame coordinates.
/// </summary>
public sealed class MlKitOcrEngine : IOcrEngine, IDisposable
{
    /// <summary>A recognition that takes longer than this returns no words.</summary>
    public const int TimeoutMs = 5000;

    readonly object _gate = new();
    ITextRecognizer? _client;
    bool _failed;

    public string Status { get; private set; } = "not initialised";

    public bool IsAvailable => _client != null && !_failed;

    public double LastDurationMs { get; private set; }

    /// <summary>
    /// Creates the recognizer and, off the main thread, recognises a small test picture so a model that cannot load
    /// shows up here (IsAvailable false) instead of on the first real read. On the main thread the test is skipped:
    /// ML Kit reports back on the main thread, so waiting there would never end.
    /// </summary>
    public bool EnsureInitialized()
    {
        if (_failed)
            return false;
        if (_client != null)
            return true;

        lock (_gate)
        {
            if (_client != null)
                return true;
            try
            {
                var client = TextRecognition.GetClient(TextRecognizerOptions.DefaultOptions!)!;
                if (Looper.MyLooper() != Looper.MainLooper)
                {
                    using var probe = Bitmap.CreateBitmap(96, 32, Bitmap.Config.Argb8888!)!;
                    probe.EraseColor(Android.Graphics.Color.White);
                    var test = client.Process(InputImage.FromBitmap(probe, 0)!)!.AsAsync<Java.Lang.Object>();
                    if (!test.Wait(TimeoutMs * 2))
                        throw new TimeoutException("the test recognition did not finish");
                }

                _client = client;
                Status = "ML Kit text recognition ready (Latin, bundled)";
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                Status = $"OCR unavailable: {ex.GetBaseException().Message}";
                Log.Error(MainApplication.Tag, Status);
                return false;
            }
        }
    }

    /// <summary>Runs OCR over a frame region; word and line boxes come back in frame coordinates.</summary>
    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(Mat bgra, PixelRect area, double upscale)
    {
        var results = new List<OcrWord>();
        if (!EnsureInitialized())
            return results;

        area = area.Clamp(bgra.Width, bgra.Height);
        if (area.Width < 12 || area.Height < 12 || upscale <= 0)
            return results;

        Bitmap? bitmap;
        try
        {
            bitmap = ToBitmap(bgra, area, upscale);
        }
        catch (Exception ex)
        {
            Log.Warn(MainApplication.Tag, $"OCR crop failed: {ex.Message}");
            return results;
        }

        try
        {
            var started = Stopwatch.StartNew();
            var image = InputImage.FromBitmap(bitmap, 0)!;
            var work = _client!.Process(image)!.AsAsync<Java.Lang.Object>();
            if (await Task.WhenAny(work, Task.Delay(TimeoutMs)).ConfigureAwait(false) != work)
            {
                Log.Warn(MainApplication.Tag, $"OCR timed out after {TimeoutMs} ms");
                return results;
            }

            var text = (Text?)await work.ConfigureAwait(false);
            LastDurationMs = started.Elapsed.TotalMilliseconds;
            if (text?.TextBlocks == null)
                return results;

            foreach (var block in text.TextBlocks)
            {
                foreach (var line in block.Lines ?? [])
                {
                    var lineBounds = Map(line.BoundingBox, upscale, area);
                    if (lineBounds == null)
                        continue;

                    string lineText = line.Text ?? "";
                    foreach (var element in line.Elements ?? [])
                    {
                        if (Map(element.BoundingBox, upscale, area) is { } bounds)
                            results.Add(new OcrWord(element.Text ?? "", bounds, lineText, lineBounds.Value));
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            Log.Warn(MainApplication.Tag, $"OCR failed: {ex.GetBaseException().Message}");
            return results;
        }
        finally
        {
            bitmap.Recycle();
            bitmap.Dispose();
        }
    }

    /// <summary>The region, upscaled, as an ARGB_8888 bitmap (its memory is RGBA bytes).</summary>
    static Bitmap ToBitmap(Mat bgra, PixelRect area, double upscale)
    {
        using var view = new Mat(bgra, new Rectangle(area.X, area.Y, area.Width, area.Height));
        using var scaled = new Mat();
        if (Math.Abs(upscale - 1.0) > 0.01)
            CvInvoke.Resize(view, scaled, new Size(0, 0), upscale, upscale, Inter.Cubic);
        else
            view.CopyTo(scaled);

        using var rgba = new Mat();
        var conversion = scaled.NumberOfChannels switch
        {
            4 => ColorConversion.Bgra2Rgba,
            3 => ColorConversion.Bgr2Rgba,
            _ => ColorConversion.Gray2Rgba,
        };
        CvInvoke.CvtColor(scaled, rgba, conversion);

        // Read out of the Mat (a fresh, continuous one) into managed memory; never written into from managed code.
        var bytes = new byte[rgba.Width * rgba.Height * 4];
        rgba.CopyTo(bytes);
        var bitmap = Bitmap.CreateBitmap(rgba.Width, rgba.Height, Bitmap.Config.Argb8888!)!;
        bitmap.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(bytes));
        return bitmap;
    }

    static PixelRect? Map(Android.Graphics.Rect? box, double upscale, PixelRect area)
    {
        if (box == null)
            return null;
        int x = area.X + (int)Math.Round(box.Left / upscale);
        int y = area.Y + (int)Math.Round(box.Top / upscale);
        int w = Math.Max(1, (int)Math.Round(box.Width() / upscale));
        int h = Math.Max(1, (int)Math.Round(box.Height() / upscale));
        return new PixelRect(x, y, w, h);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Close();
            _client = null;
        }
    }
}
