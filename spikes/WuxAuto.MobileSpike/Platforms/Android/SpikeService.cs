using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Hardware;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Views.Accessibility;
using System.Diagnostics;
using APath = Android.Graphics.Path;
using Java.Util.Concurrent;

namespace WuxAuto.MobileSpike;

[Service(Name = "io.github.atherosxd.wuxauto.spike.SpikeService",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/spike_accessibility")]
public class SpikeService : AccessibilityService
{
    public const string Tag = "WuxSpike";
    public const string ActionPrefix = "io.github.atherosxd.wuxauto.spike.";

    /// <summary>The most recent screenshot; the OCR / MATCH actions (Task 3) read this.</summary>
    public static Bitmap? LastShot
    {
        get => _lastShot;
        set
        {
            // Replace, then recycle the old one so screenshots do not leak native memory.
            var old = Interlocked.Exchange(ref _lastShot, value);
            if (old != null && !ReferenceEquals(old, value) && !old.IsRecycled) old.Recycle();
        }
    }
    static Bitmap? _lastShot;

    // Hardcoded for the 1600x900 MuMu layout (spec 4).
    const float JoyX = 220, JoyY = 682, JoyR = 88;

    IExecutor? _executor;
    IExecutor Executor => _executor ??= Executors.NewSingleThreadExecutor()!;

    SpikeReceiver? _receiver;

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        _receiver = new SpikeReceiver(this);
        var filter = new IntentFilter();
        // Tasks 2-3 add: SHOT, WALK, SWIPE, PINCH, MULTI, OCR, MATCH.
        foreach (var action in SpikeReceiver.Actions)
            filter.AddAction(ActionPrefix + action);
        // The flags overload exists from Android 13; older versions register exported by default.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_receiver, filter, ReceiverFlags.Exported);
        else
            RegisterReceiver(_receiver, filter);
        Log.Info(Tag, "connected");
    }

    internal Task RunAsync(string name, Intent i)
    {
        int I(string k, int d = 0) => i.GetIntExtra(k, d);
        switch (name)
        {
            case "SHOT": return ShotAsync(I("count", 1));
            case "WALK":
            {
                float dx = I("dx"), dy = I("dy");
                var len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1) { Log.Warn(Tag, "WALK needs dx/dy"); return Task.CompletedTask; }
                var path = Line(JoyX, JoyY, JoyX + dx / len * JoyR, JoyY + dy / len * JoyR);
                return GestureAsync("WALK", new[] { Stroke(path, 0, I("ms", 2000)) });
            }
            case "SWIPE":
                return GestureAsync("SWIPE", new[] { Stroke(Line(I("x1"), I("y1"), I("x2"), I("y2")), 0, I("ms", 600)) });
            case "PINCH":
            {
                int cx = I("cx"), cy = I("cy"), from = I("from"), to = I("to"), ms = I("ms", 600);
                // Two fingers on a horizontal line through the centre, moving towards each other.
                return GestureAsync("PINCH", new[]
                {
                    Stroke(Line(cx - from, cy, cx - to, cy), 0, ms),
                    Stroke(Line(cx + from, cy, cx + to, cy), 0, ms),
                });
            }
            case "MULTI":
            {
                int ms = I("ms", 2500);
                var joy = Line(JoyX, JoyY, JoyX, JoyY - JoyR);
                var tap = new APath(); tap.MoveTo(I("tx"), I("ty"));
                // Joystick held the whole time; the tap lands half-way, in the same gesture.
                return GestureAsync("MULTI", new[] { Stroke(joy, 0, ms), Stroke(tap, ms / 2, 60) });
            }
        }
        return Task.CompletedTask;
    }

    static APath Line(float x1, float y1, float x2, float y2)
    {
        var p = new APath();
        p.MoveTo(x1, y1);
        p.LineTo(x2, y2);
        return p;
    }

    static GestureDescription.StrokeDescription Stroke(APath p, long start, long dur)
        => new(p, start, Math.Max(1, dur));

    Task GestureAsync(string name, GestureDescription.StrokeDescription[] strokes)
    {
        var tcs = new TaskCompletionSource();
        var b = new GestureDescription.Builder();
        foreach (var s in strokes) b.AddStroke(s);
        var cb = new GestureCallback(name, tcs);
        var ok = DispatchGesture(b.Build()!, cb, null);
        if (!ok) { Log.Warn(Tag, $"{name} dispatch refused"); tcs.TrySetResult(); }
        return tcs.Task;
    }

    async Task ShotAsync(int count)
    {
        int ok = 0, fail = 0;
        var total = Stopwatch.StartNew();
        var prev = total.ElapsedMilliseconds;
        for (var n = 1; n <= count; n++)
        {
            Bitmap? bmp = null;
            // The platform allows one screenshot per ~333 ms (error 3, interval too short): wait and retry.
            for (var attempt = 0; attempt < 5 && bmp == null; attempt++)
            {
                var tcs = new TaskCompletionSource<Bitmap?>();
                TakeScreenshot(0, Executor, new ShotCallback(tcs));
                bmp = await tcs.Task;
                if (bmp == null) await Task.Delay(ShotCallback.LastError == 3 ? 340 : 100);
            }
            var now = total.ElapsedMilliseconds;
            if (bmp == null) { fail++; prev = now; continue; }
            ok++;
            Log.Info(Tag, $"shot {n} {bmp.Width}x{bmp.Height} t={now - prev} lit={LitFraction(bmp):F3}");
            prev = now;
            LastShot = bmp; // recycles the previous one
        }
        var secs = Math.Max(total.ElapsedMilliseconds, 1) / 1000.0;
        var last = LastShot;
        if (last != null)
        {
            var dir = GetExternalFilesDir("shots")!;
            dir.Mkdirs();
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir.AbsolutePath, "shot_last.png"));
            last.Compress(Bitmap.CompressFormat.Png!, 100, fs);
        }
        Log.Info(Tag, $"shots ok={ok} fail={fail} fps={ok / secs:F1}");
    }

    /// <summary>Fraction of sampled pixels that are not pure black (a quick "is it the game, not black" check).</summary>
    static double LitFraction(Bitmap b)
    {
        int lit = 0, n = 0;
        for (var y = 10; y < b.Height; y += b.Height / 20)
            for (var x = 10; x < b.Width; x += b.Width / 32, n++)
                if ((b.GetPixel(x, y) & 0xFFFFFF) != 0) lit++;
        return n == 0 ? 0 : (double)lit / n;
    }

    sealed class GestureCallback : GestureResultCallback
    {
        readonly string _name; readonly TaskCompletionSource _tcs;
        public GestureCallback(string name, TaskCompletionSource tcs) { _name = name; _tcs = tcs; }
        public override void OnCompleted(GestureDescription? g) { Log.Info(Tag, $"{_name} completed"); _tcs.TrySetResult(); }
        public override void OnCancelled(GestureDescription? g) { Log.Info(Tag, $"{_name} cancelled"); _tcs.TrySetResult(); }
    }

    sealed class ShotCallback : Java.Lang.Object, AccessibilityService.ITakeScreenshotCallback
    {
        readonly TaskCompletionSource<Bitmap?> _tcs;
        public static volatile int LastError;
        public ShotCallback(TaskCompletionSource<Bitmap?> tcs) { _tcs = tcs; }
        public void OnSuccess(ScreenshotResult? r)
        {
            try
            {
                using var hw = Bitmap.WrapHardwareBuffer(r!.HardwareBuffer!, r.ColorSpace)!;
                var copy = hw.Copy(Bitmap.Config.Argb8888!, false)!;
                r.HardwareBuffer!.Close();
                _tcs.TrySetResult(copy);
            }
            catch (Exception ex) { Log.Error(Tag, $"shot decode failed: {ex.Message}"); _tcs.TrySetResult(null); }
        }
        public void OnFailure(int errorCode)
        {
            LastError = errorCode;
            Log.Warn(Tag, $"shot failed code={errorCode}");
            _tcs.TrySetResult(null);
        }
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e) { }

    public override void OnInterrupt() { }

    public override bool OnUnbind(Intent? intent)
    {
        if (_receiver != null)
        {
            try { UnregisterReceiver(_receiver); } catch (Exception) { }
            _receiver = null;
        }
        Log.Info(Tag, "unbound");
        return base.OnUnbind(intent);
    }
}

public class SpikeReceiver : BroadcastReceiver
{
    /// <summary>Action suffixes this receiver listens for. Add new ones here and handle them in <see cref="OnReceive"/>.</summary>
    public static readonly string[] Actions = { "PING", "SHOT", "WALK", "SWIPE", "PINCH", "MULTI" };

    readonly SpikeService _service;

    public SpikeReceiver(SpikeService service) => _service = service;

    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action ?? "";
        var name = action.StartsWith(SpikeService.ActionPrefix) ? action[SpikeService.ActionPrefix.Length..] : action;
        Log.Info(SpikeService.Tag, $"received {name}");
        if (name == "PING" || intent == null) return;
        // OnReceive runs on the main thread with a ~10 s budget: hand the work to a background task.
        var pending = GoAsync();
        Task.Run(async () =>
        {
            try { await _service.RunAsync(name, intent); }
            catch (Exception ex) { Log.Error(SpikeService.Tag, $"{name} failed: {ex}"); }
            finally { pending?.Finish(); }
        });
    }
}
