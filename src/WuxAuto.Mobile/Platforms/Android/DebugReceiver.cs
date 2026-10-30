#if DEBUG
using System.Diagnostics;
using Android.Content;
using Android.Util;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Mobile;

/// <summary>
/// Debug builds only: adb probes of the platform layer. Only holders of DUMP (the adb shell) may send these broadcasts:
/// <c>adb shell am broadcast -a io.github.atherosxd.wuxauto.&lt;ACTION&gt; -p io.github.atherosxd.wuxauto [--ei name value ...]</c>.
/// Results go to logcat (tag WuxAuto).
/// <list type="bullet">
/// <item>PING, FRONT (active package, game in front?), TEMPLATES (what TemplateLibrary loads)</item>
/// <item>CAPTURE [--ez bringGame false] (consent dialog), STOPCAPTURE, SHOT (LatestFrame → files/debug/shot_*.png)</item>
/// <item>OCR --ei x --ei y --ei w --ei h [--ef scale 1.5, clamped 0.5-4] (whole frame without w/h)</item>
/// <item>START [--es mode lod|laby] (the pilot in that mode, else the saved one, as the Start button), STOP (as the Stop button) - a live check can stop the run from a
/// logcat watcher before it teleports</item>
/// <item>TAP x y [ms], SWIPE x1 y1 x2 y2 [ms], DRAG x1 y1 x2 y2 [ms] [hold], JOY cx cy dx dy [ms], PINCH cx cy from to [ms] (full gaps, default 720 → 192), HOLD x y, RELEASE</item>
/// </list>
/// </summary>
public sealed class DebugReceiver : BroadcastReceiver
{
    public const string Prefix = "io.github.atherosxd.wuxauto.";
    const string Permission = "android.permission.DUMP";
    const string Tag = MainApplication.Tag;

    static readonly string[] Actions =
        ["PING", "FRONT", "TEMPLATES", "CAPTURE", "STOPCAPTURE", "SHOT", "OCR", "TAP", "SWIPE", "DRAG", "JOY", "PINCH", "HOLD", "RELEASE",
         "START", "STOP"];

    static readonly HashSet<string> TouchActions = ["TAP", "SWIPE", "DRAG", "JOY", "PINCH", "HOLD", "RELEASE"];

    static readonly Lazy<MlKitOcrEngine> Ocr = new(() => new MlKitOcrEngine());

    readonly Context _context;

    DebugReceiver(Context context) => _context = context;

    public static DebugReceiver Register(Context context)
    {
        var receiver = new DebugReceiver(context);
        var filter = new IntentFilter();
        foreach (string action in Actions)
            filter.AddAction(Prefix + action);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            context.RegisterReceiver(receiver, filter, Permission, null, ReceiverFlags.Exported);
        else
            context.RegisterReceiver(receiver, filter, Permission, null);
        Log.Info(Tag, "debug receiver registered (DUMP-protected)");
        return receiver;
    }

    public void Unregister(Context context)
    {
        try { context.UnregisterReceiver(this); } catch (Exception) { }
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is not { } action || !action.StartsWith(Prefix, StringComparison.Ordinal))
            return;
        string name = action[Prefix.Length..];
        Log.Info(Tag, $"debug {name}");
        if (name == "PING")
            return;

        // Touches block (a JOY step for its whole length): run them in the background without holding the broadcast -
        // a pending GoAsync result kept the next broadcast (RELEASE, meant to cut that JOY short) from being delivered.
        if (TouchActions.Contains(name))
        {
            Task.Run(async () =>
            {
                try
                {
                    await RunAsync(name, intent);
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, $"debug {name} failed: {ex}");
                }
            });
            return;
        }

        // The rest: OnReceive runs on the main thread with a ~10 s budget - work in the background, holding the broadcast.
        var pending = GoAsync();
        Task.Run(async () =>
        {
            try
            {
                var work = RunAsync(name, intent);
                await Task.WhenAny(work, Task.Delay(8000));
                pending?.Finish();
                pending = null;
                await work;
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"debug {name} failed: {ex}");
            }
            finally
            {
                pending?.Finish();
            }
        });
    }

    async Task RunAsync(string name, Intent i)
    {
        int I(string key, int fallback = 0) => i.GetIntExtra(key, fallback);
        var touch = PilotAccessibilityService.Touch;
        switch (name)
        {
            case "FRONT":
            {
                var service = PilotAccessibilityService.Instance;
                Log.Info(Tag, $"front: active={service?.ActivePackage() ?? "(service off)"} gameInFront={touch.IsGameInFront} holding={touch.IsHolding}");
                break;
            }
            case "TEMPLATES":
            {
                using var library = new TemplateLibrary();
                Log.Info(Tag, $"templates: {library.Names.Count} from {string.Join(" | ", library.SearchPaths)}");
                Log.Info(Tag, $"templates: {string.Join(", ", library.Names)}");
                break;
            }
            case "CAPTURE":
                CaptureService.RequestConsent(_context, i.GetBooleanExtra(ConsentActivity.ExtraBringGame, true));
                break;
            case "STOPCAPTURE":
                CaptureService.StopCapture();
                break;
            case "SHOT":
                Shot();
                break;
            case "OCR":
                await OcrAsync(I("x"), I("y"), I("w"), I("h"), Math.Clamp(i.GetFloatExtra("scale", 1.5f), 0.5f, 4f));
                break;
            case "TAP":
                Report(name, touch.Tap(I("x"), I("y"), I("ms", 60)));
                break;
            case "SWIPE":
                Report(name, touch.Swipe(I("x1"), I("y1"), I("x2"), I("y2"), I("ms", 300)));
                break;
            case "DRAG":
                Report(name, touch.Drag(I("x1"), I("y1"), I("x2"), I("y2"), I("ms", 800), I("hold", 100)));
                break;
            case "JOY":
                Report(name, touch.Joystick(I("cx"), I("cy"), I("dx"), I("dy"), I("ms", 900)));
                break;
            case "PINCH":
                Report(name, touch.Pinch(I("cx"), I("cy"), I("from", 720), I("to", 192), I("ms", 400)));
                break;
            case "HOLD":
                Report(name, touch.PressDown(I("x"), I("y")));
                break;
            case "RELEASE":
                touch.Release();
                Report(name, !touch.IsHolding);
                break;
            case "START":
            {
                // [--es mode lod|laby]; without it the saved mode. On the main thread, as the Start button calls it (it raises
                // UI state events).
                var engine = Engine.EngineHost.Engine;
                var mode = i.GetStringExtra("mode")?.ToLowerInvariant() switch
                {
                    "laby" => RunMode.AutoLaby,
                    "lod" => RunMode.AutoLod,
                    _ => engine.Settings.Sequence.Mode,
                };
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => engine.Start(mode));
                Report(name, true);
                break;
            }
            case "STOP":
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => Engine.EngineHost.Engine.Stop("adb STOP probe"));
                Report(name, true);
                break;
        }
    }

    static void Report(string name, bool ok) => Log.Info(Tag, $"debug {name} -> {ok}");

    static void Shot()
    {
        var capture = CaptureService.Current;
        if (capture?.LatestFrame is not { } frame)
        {
            Log.Warn(Tag, $"shot: no frame (capture {(capture == null ? "off" : $"on, {capture.FrameCount} frames")})");
            return;
        }

        using var bgra = frame.Bgra;
        Directory.CreateDirectory(AppPaths.DebugFolder);
        string path = Path.Combine(AppPaths.DebugFolder, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        ImageFile.Write(path, bgra);
        long ageMs = (Phone.FrameGate.NowTicks() - frame.Ticks) / TimeSpan.TicksPerMillisecond;
        Log.Info(Tag, $"shot: {bgra.Width}x{bgra.Height} age {ageMs} ms frames {capture.FrameCount} -> {path}");
    }

    static async Task OcrAsync(int x, int y, int w, int h, float scale)
    {
        if (CaptureService.Current?.LatestFrame is not { } frame)
        {
            Log.Warn(Tag, "ocr: no frame");
            return;
        }

        using var bgra = frame.Bgra;
        var area = w > 0 && h > 0 ? new PixelRect(x, y, w, h) : new PixelRect(0, 0, bgra.Width, bgra.Height);
        var engine = Ocr.Value;
        var sw = Stopwatch.StartNew();
        var words = await engine.RecognizeAsync(bgra, area, scale);
        Log.Info(Tag, $"ocr: {engine.Status}; {words.Count} word(s) in {sw.ElapsedMilliseconds} ms (engine {engine.LastDurationMs:F0} ms), area {area.X},{area.Y},{area.Width},{area.Height} x{scale}");
        foreach (var line in words.GroupBy(word => (word.LineText, word.LineBounds)))
        {
            var b = line.Key.LineBounds;
            Log.Info(Tag, $"ocr line \"{line.Key.LineText}\" {b.X},{b.Y},{b.Width},{b.Height}: {string.Join(" ", line.Select(word => $"[{word.Text} {word.Bounds.X},{word.Bounds.Y},{word.Bounds.Width},{word.Bounds.Height}]"))}");
        }
    }
}
#endif
