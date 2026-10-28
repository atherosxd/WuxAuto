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
using Android.Gms.Extensions;

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

    /// <summary>Lit fraction (0..1) of <see cref="LastShot"/>; about 0.99 for a game frame. Task 3 should check this before OCR/MATCH.</summary>
    public static double LastShotLit { get; private set; }

    /// <summary>Frames darker than this are treated as black and never replace <see cref="LastShot"/>.</summary>
    const double BlackLit = 0.05;

    /// <summary>Stores <paramref name="bmp"/> as LastShot unless it is black (then it is recycled and the previous frame kept).</summary>
    internal static void Offer(Bitmap bmp, double lit)
    {
        if (lit < BlackLit)
        {
            Log.Info(Tag, "black frame, kept previous LastShot");
            bmp.Recycle();
            return;
        }
        LastShot = bmp; // recycles the previous one
        LastShotLit = lit;
    }

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
        // Only holders of DUMP (the adb shell) may send these broadcasts; ordinary apps cannot.
        // The flags overload exists from Android 13; older versions register exported by default.
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_receiver, filter, BroadcastPermission, null, ReceiverFlags.Exported);
        else
            RegisterReceiver(_receiver, filter, BroadcastPermission, null);
        Log.Info(Tag, "connected");
    }

    const string BroadcastPermission = "android.permission.DUMP";

    internal Task RunAsync(string name, Intent i)
    {
        var dm = Resources!.DisplayMetrics!;
        int W = dm.WidthPixels, H = dm.HeightPixels;
        static int Cl(int v, int lo, int hi) => Math.Min(Math.Max(v, lo), hi);
        int I(string k, int d = 0) => i.GetIntExtra(k, d);
        int Count() => Cl(I("count", 1), 1, 100);
        int Ms(int d) => Cl(I("ms", d), 50, 10000);
        int X(string k) => Cl(I(k), 0, W - 1);
        int Y(string k) => Cl(I(k), 0, H - 1);
        switch (name)
        {
            case "SHOT": return ShotAsync(Count());
            case "SHOTMP":
            {
                var c = new Intent(this, typeof(ConsentActivity)).AddFlags(ActivityFlags.NewTask);
                c.PutExtra("count", Count());
                StartActivity(c);
                return Task.CompletedTask;
            }
            case "WALK":
            {
                float dx = Cl(I("dx"), -10000, 10000), dy = Cl(I("dy"), -10000, 10000);
                var len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1) { Log.Warn(Tag, "WALK needs dx/dy"); return Task.CompletedTask; }
                float jx = Cl(I("jx", (int)JoyX), 0, W - 1), jy = Cl(I("jy", (int)JoyY), 0, H - 1), jr = Cl(I("jr", (int)JoyR), 10, 400);
                var path = Line(jx, jy, jx + dx / len * jr, jy + dy / len * jr);
                return GestureAsync("WALK", new[] { Stroke(path, 0, Ms(2000)) });
            }
            case "SWIPE":
                return GestureAsync("SWIPE", new[] { Stroke(Line(X("x1"), Y("y1"), X("x2"), Y("y2")), 0, Ms(600)) });
            case "PINCH":
            {
                int cx = X("cx"), cy = Y("cy"), from = Cl(I("from"), 0, W), to = Cl(I("to"), 0, W), ms = Ms(600);
                // Keep both fingers on screen.
                var reach = Math.Min(cx, W - 1 - cx);
                from = Math.Min(from, reach); to = Math.Min(to, reach);
                // Two fingers on a horizontal line through the centre, moving towards each other.
                return GestureAsync("PINCH", new[]
                {
                    Stroke(Line(cx - from, cy, cx - to, cy), 0, ms),
                    Stroke(Line(cx + from, cy, cx + to, cy), 0, ms),
                });
            }
            case "MULTI":
            {
                int ms = Ms(2500);
                float jx = Cl(I("jx", (int)JoyX), 0, W - 1), jy = Cl(I("jy", (int)JoyY), 0, H - 1), jr = Cl(I("jr", (int)JoyR), 10, 400);
                float dx = I("dx", 0), dy = I("dy", -100), len = Math.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
                var joy = Line(jx, jy, jx + dx / len * jr, jy + dy / len * jr);
                var tap = new APath(); tap.MoveTo(X("tx"), Y("ty")); tap.LineTo(X("tx") + 1, Y("ty"));
                // Joystick held the whole time; the tap lands half-way, in the same gesture.
                return GestureAsync("MULTI", new[] { Stroke(joy, 0, ms), Stroke(tap, ms / 2, 100) });
            }
            case "TAP":
            {
                var tap = new APath(); tap.MoveTo(X("x"), Y("y")); tap.LineTo(X("x") + 1, Y("y"));
                return GestureAsync("TAP", new[] { Stroke(tap, 0, 100) });
            }
            case "MULTIC": return MultiContinuedAsync(i, W, H);
            case "SEQ": return SeqAsync(i.GetStringExtra("steps") ?? "", W, H);
            case "DRAG": return DragAsync(X("x1"), Y("y1"), X("x2"), Y("y2"), Ms(800), Cl(I("hold"), 0, 2000));
            case "OCR": return OcrAsync(I("x"), I("y"), I("w"), I("h"), I("scale", 1));
            case "MATCH": return Task.Run(() => Match(I("x"), I("y"), I("w"), I("h")));
        }
        return Task.CompletedTask;
    }

    /// <summary>Copy of <see cref="LastShot"/> (Offer recycles the stored one on replace), or null with a log line when there is no usable game frame.</summary>
    static Bitmap? CopyLastShot(string who)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var s = LastShot;
            if (s == null || LastShotLit < BlackLit)
            {
                Log.Warn(Tag, $"{who}: no usable LastShot (null={s == null} lit={LastShotLit:F3}); run SHOT/SHOTMP first");
                return null;
            }
            try { return s.Copy(Bitmap.Config.Argb8888!, false); }
            catch (Exception ex) { Log.Warn(Tag, $"{who}: copy raced a replace ({ex.Message}), retrying"); }
        }
        return null;
    }

    static readonly System.Text.RegularExpressions.Regex CoordRx = new(
        @"(\d{3,6})(?:\s+,\s*|\s*,\s+)(\d{3,6})|(\d{4,6})\s*\.\s*(\d{4,6})");

    /// <summary>OCR of the last shot; with w,h > 0 only the box x,y,w,h, upscaled by <paramref name="scale"/> (boxes are then relative to the crop).</summary>
    async Task OcrAsync(int cx, int cy, int cw, int ch, int scale)
    {
        using var full = CopyLastShot("OCR");
        if (full == null) return;
        var bmp = full;
        if (cw > 0 && ch > 0)
        {
            cx = Math.Min(Math.Max(cx, 0), full.Width - 1); cy = Math.Min(Math.Max(cy, 0), full.Height - 1);
            cw = Math.Min(cw, full.Width - cx); ch = Math.Min(ch, full.Height - cy);
            scale = Math.Min(Math.Max(scale, 1), 4);
            using var part = Bitmap.CreateBitmap(full, cx, cy, cw, ch);
            bmp = Bitmap.CreateScaledBitmap(part, cw * scale, ch * scale, true)!;
            Log.Info(Tag, $"ocr crop {cx},{cy},{cw},{ch} x{scale}");
        }
        var sw = Stopwatch.StartNew();
        Xamarin.Google.MLKit.Vision.Text.ITextRecognizer? client = null;
        try
        {
            var image = Xamarin.Google.MLKit.Vision.Common.InputImage.FromBitmap(bmp, 0)!;
            client = Xamarin.Google.MLKit.Vision.Text.TextRecognition.GetClient(
                Xamarin.Google.MLKit.Vision.Text.Latin.TextRecognizerOptions.DefaultOptions!)!;
            var result = (Xamarin.Google.MLKit.Vision.Text.Text?)await client.Process(image)!.AsAsync<Java.Lang.Object>();
            sw.Stop();
            bool title = false, coords = false;
            foreach (var block in result!.TextBlocks!)
                foreach (var line in block.Lines!)
                {
                    var r = line.BoundingBox;
                    var text = line.Text ?? "";
                    Log.Info(Tag, $"ocr \"{text}\" {r?.Left},{r?.Top},{r?.Width()},{r?.Height()}");
                    if (text.Contains("Piercing", StringComparison.OrdinalIgnoreCase)) title = true;
                    if (CoordRx.IsMatch(text)) { coords = true; Log.Info(Tag, $"ocr coordinate line: \"{text}\""); }
                }
            Log.Info(Tag, $"ocr ms={sw.ElapsedMilliseconds} frame={bmp.Width}x{bmp.Height} lit={LastShotLit:F3} piercing={title} coords={coords}");
        }
        catch (Exception ex) { Log.Error(Tag, $"OCR failed: {ex}"); }
        finally { client?.Close(); if (!ReferenceEquals(bmp, full)) bmp.Recycle(); }
    }

    static bool _emguInit;

    /// <summary>ARGB_8888 bitmap to a gray Emgu Mat.</summary>
    static Emgu.CV.Mat ToGray(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        // GetPixels fills a managed array (CopyPixelsToBuffer on a wrapped array writes to a Java-side copy).
        var px = new int[w * h];
        b.GetPixels(px, 0, w, 0, 0, w, h);
        var bytes = new byte[px.Length * 4];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length); // little-endian ARGB ints = B,G,R,A bytes
        using var bgra = new Emgu.CV.Mat(h, w, Emgu.CV.CvEnum.DepthType.Cv8U, 4);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bgra.DataPointer, bytes.Length);
        var gray = new Emgu.CV.Mat();
        Emgu.CV.CvInvoke.CvtColor(bgra, gray, Emgu.CV.CvEnum.ColorConversion.Bgra2Gray);
        return gray;
    }

    static void Match(int x, int y, int w, int h)
    {
        if (w < 8 || h < 8) { Log.Error(Tag, $"match refused: MATCH needs --ei w and --ei h of at least 8 (got w={w} h={h})"); return; }
        using var bmp = CopyLastShot("MATCH");
        if (bmp == null) return;
        try
        {
            if (!_emguInit) { Emgu.CV.CvInvokeAndroid.Init(); _emguInit = true; }
            x = Math.Min(Math.Max(x, 0), bmp.Width - 1); y = Math.Min(Math.Max(y, 0), bmp.Height - 1);
            w = Math.Min(w, bmp.Width - x); h = Math.Min(h, bmp.Height - y);
            if (w < 8 || h < 8) { Log.Error(Tag, $"match refused: box clipped to {w}x{h} at {x},{y}"); return; }
            var sw = Stopwatch.StartNew();
            using var gray = ToGray(bmp);
            using var roi = new Emgu.CV.Mat(gray, new System.Drawing.Rectangle(x, y, w, h));
            using var templ = roi.Clone();
            var stdT = Std(templ); var stdF = Std(gray);
            if (stdT < 2.0) { Log.Error(Tag, $"match refused: flat template (std={stdT:F2})"); return; }
            if (stdF < 2.0) { Log.Error(Tag, $"match refused: flat frame (std={stdF:F2})"); return; }
            using var res = new Emgu.CV.Mat();
            Emgu.CV.CvInvoke.MatchTemplate(gray, templ, res, Emgu.CV.CvEnum.TemplateMatchingType.CcoeffNormed);
            double min = 0, max = 0; System.Drawing.Point minLoc = default, maxLoc = default;
            Emgu.CV.CvInvoke.MinMaxLoc(res, ref min, ref max, ref minLoc, ref maxLoc);
            sw.Stop();
            Log.Info(Tag, $"match selfmatch=path-check score={max:F4} at {maxLoc.X},{maxLoc.Y} ms={sw.ElapsedMilliseconds} box={x},{y},{w},{h} templStd={stdT:F1} frameStd={stdF:F1}");
        }
        catch (Exception ex) { Log.Error(Tag, $"MATCH failed: {ex}"); }
    }

    static double Std(Emgu.CV.Mat gray)
    {
        using var mean = new Emgu.CV.Mat(); using var std = new Emgu.CV.Mat();
        Emgu.CV.CvInvoke.MeanStdDev(gray, mean, std);
        var d = new double[1];
        System.Runtime.InteropServices.Marshal.Copy(std.DataPointer, d, 0, 1); // 1x1 CV_64F
        return d[0];
    }
    /// <summary>Joystick as a continued stroke: gesture 1 holds it (willContinue), gesture 2 continues it and adds the tap.</summary>
    async Task MultiContinuedAsync(Intent i, int W, int H)
    {
        int I(string k, int d = 0) => i.GetIntExtra(k, d);
        var ms = Math.Min(Math.Max(I("ms", 2500), 50), 10000);
        float jx = Math.Min(Math.Max(I("jx", (int)JoyX), 0), W - 1), jy = Math.Min(Math.Max(I("jy", (int)JoyY), 0), H - 1), jr = Math.Min(Math.Max(I("jr", (int)JoyR), 10), 400);
        float dx = I("dx", 0), dy = I("dy", -100), len = Math.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
        float ex = jx + dx / len * jr, ey = jy + dy / len * jr;
        int tx = Math.Min(Math.Max(I("tx"), 0), W - 1), ty = Math.Min(Math.Max(I("ty"), 0), H - 1);
        var first = new GestureDescription.StrokeDescription(Line(jx, jy, ex, ey), 0, ms / 2, true);
        await GestureAsync("MULTIC-1", new[] { first });
        var tap = new APath(); tap.MoveTo(tx, ty); tap.LineTo(tx + 1, ty);
        var second = first.ContinueStroke(Line(ex, ey, ex, ey + 1), 0, ms / 2, false)!;
        await GestureAsync("MULTIC-2", new[] { second, Stroke(tap, 50, 100) });
    }

    /// <summary>
    /// Separate gestures back to back on the device, no adb round trips: "W:dx:dy:ms" joystick step (centre/radius as
    /// WALK's defaults or jx/jy/jr in the step: "W:dx:dy:ms:jx:jy:jr"), "T:x:y" 100 ms tap, "G:ms" wait. Each gesture
    /// is awaited (completed callback) before the next; every start/end is logged with ms since the sequence began.
    /// </summary>
    async Task SeqAsync(string steps, int W, int H)
    {
        static int Cl(int v, int lo, int hi) => Math.Min(Math.Max(v, lo), hi);
        var sw = Stopwatch.StartNew();
        long lastEnd = 0;
        var n = 0;
        foreach (var raw in steps.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (++n > 40) { Log.Warn(Tag, "seq: more than 40 steps, stopped"); break; }
            var p = raw.Split(':');
            int P(int k, int d = 0) => k < p.Length && int.TryParse(p[k], out var v) ? v : d;
            switch (p[0])
            {
                case "G":
                    await Task.Delay(Cl(P(1), 0, 5000));
                    Log.Info(Tag, $"seq {n} G{P(1)} t={sw.ElapsedMilliseconds}");
                    break;
                case "W":
                {
                    float dx = P(1), dy = P(2), len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len < 1) { Log.Warn(Tag, $"seq {n}: W needs dx/dy"); return; }
                    float jx = Cl(P(4, (int)JoyX), 0, W - 1), jy = Cl(P(5, (int)JoyY), 0, H - 1), jr = Cl(P(6, (int)JoyR), 10, 400);
                    var t0 = sw.ElapsedMilliseconds;
                    var path = Line(jx, jy, jx + dx / len * jr, jy + dy / len * jr);
                    await GestureAsync($"seq {n} W", new[] { Stroke(path, 0, Cl(P(3, 1500), 50, 10000)) });
                    Log.Info(Tag, $"seq {n} W start={t0} gapSincePrevEnd={t0 - lastEnd} end={sw.ElapsedMilliseconds}");
                    lastEnd = sw.ElapsedMilliseconds;
                    break;
                }
                case "T":
                {
                    int x = Cl(P(1), 0, W - 1), y = Cl(P(2), 0, H - 1);
                    var tap = new APath(); tap.MoveTo(x, y); tap.LineTo(x + 1, y);
                    var t0 = sw.ElapsedMilliseconds;
                    await GestureAsync($"seq {n} T", new[] { Stroke(tap, 0, 100) });
                    Log.Info(Tag, $"seq {n} T {x},{y} start={t0} gapSincePrevEnd={t0 - lastEnd} end={sw.ElapsedMilliseconds}");
                    lastEnd = sw.ElapsedMilliseconds;
                    break;
                }
                default: Log.Warn(Tag, $"seq {n}: unknown step '{raw}'"); return;
            }
        }
        Log.Info(Tag, $"seq done t={sw.ElapsedMilliseconds}");
    }

    /// <summary>A drag: the finger moves x1,y1 → x2,y2 over ms, then (hold &gt; 0) rests there for hold ms before lifting (continued stroke, 1 px).</summary>
    async Task DragAsync(int x1, int y1, int x2, int y2, int ms, int hold)
    {
        var move = new GestureDescription.StrokeDescription(Line(x1, y1, x2, y2), 0, ms, hold > 0);
        await GestureAsync("DRAG-move", new[] { move });
        if (hold > 0)
            await GestureAsync("DRAG-hold", new[] { move.ContinueStroke(Line(x2, y2, x2, y2 + 1), 0, hold, false)! });
        Log.Info(Tag, $"DRAG done {x1},{y1}->{x2},{y2} ms={ms} hold={hold}");
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
            var lit = LitFraction(bmp);
            Log.Info(Tag, $"shot {n} {bmp.Width}x{bmp.Height} t={now - prev} lit={lit:F3}");
            prev = now;
            Offer(bmp, lit);
        }
        var secs = Math.Max(total.ElapsedMilliseconds, 1) / 1000.0;
        SaveLast("shot_last.png");
        Log.Info(Tag, $"shots ok={ok} fail={fail} fps={ok / secs:F1} lastShotLit={LastShotLit:F3}");
    }

    /// <summary>Writes <see cref="LastShot"/> as a PNG into the app's external files "shots" folder.</summary>
    internal static void SaveLast(string fileName)
    {
        var last = LastShot;
        if (last == null) return;
        var dir = Android.App.Application.Context.GetExternalFilesDir("shots")!;
        dir.Mkdirs();
        using var fs = System.IO.File.Create(System.IO.Path.Combine(dir.AbsolutePath, fileName));
        last.Compress(Bitmap.CompressFormat.Png!, 100, fs);
    }

    /// <summary>Fraction of sampled pixels that are not pure black (a quick "is it the game, not black" check).</summary>
    internal static double LitFraction(Bitmap b)
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
                // Alternative: lock the buffer for CPU reads (AHardwareBuffer) instead of the GPU copy.
                var cpu = CpuRead(r.HardwareBuffer!);
                var gpuLit = LitFraction(copy);
                var cpuLit = cpu == null ? -1 : LitFraction(cpu);
                Log.Info(Tag, $"shot gpuLit={gpuLit:F3} cpuLit={cpuLit:F3}");
                if (cpu != null && gpuLit == 0 && cpuLit > 0) { copy.Recycle(); copy = cpu; }
                else cpu?.Recycle();
                r.HardwareBuffer!.Close();
                _tcs.TrySetResult(copy);
            }
            catch (Exception ex) { Log.Error(Tag, $"shot decode failed: {ex.Message}"); _tcs.TrySetResult(null); }
        }
        [System.Runtime.InteropServices.DllImport("libandroid.so")]
        static extern IntPtr AHardwareBuffer_fromHardwareBuffer(IntPtr env, IntPtr hardwareBuffer);
        [System.Runtime.InteropServices.DllImport("libandroid.so")]
        static extern void AHardwareBuffer_describe(IntPtr buffer, IntPtr desc);
        [System.Runtime.InteropServices.DllImport("libandroid.so")]
        static extern int AHardwareBuffer_lock(IntPtr buffer, ulong usage, int fence, IntPtr rect, out IntPtr address);
        [System.Runtime.InteropServices.DllImport("libandroid.so")]
        static extern int AHardwareBuffer_unlock(IntPtr buffer, IntPtr fence);

        /// <summary>Reads an RGBA_8888 HardwareBuffer on the CPU. Returns null when it cannot be locked.</summary>
        static Bitmap? CpuRead(HardwareBuffer hb)
        {
            var desc = System.Runtime.InteropServices.Marshal.AllocHGlobal(48);
            try
            {
                var ab = AHardwareBuffer_fromHardwareBuffer(Android.Runtime.JNIEnv.Handle, hb.Handle);
                if (ab == IntPtr.Zero) return null;
                AHardwareBuffer_describe(ab, desc);
                int w = System.Runtime.InteropServices.Marshal.ReadInt32(desc, 0);
                int h = System.Runtime.InteropServices.Marshal.ReadInt32(desc, 4);
                int format = System.Runtime.InteropServices.Marshal.ReadInt32(desc, 12);
                int stride = System.Runtime.InteropServices.Marshal.ReadInt32(desc, 24);
                var rc = AHardwareBuffer_lock(ab, 3 /* CPU_READ_OFTEN */, -1, IntPtr.Zero, out var addr);
                if (rc != 0) { Log.Warn(Tag, $"cpu lock rc={rc} format={format}"); return null; }
                try
                {
                    var bytes = new byte[stride * 4 * h];
                    System.Runtime.InteropServices.Marshal.Copy(addr, bytes, 0, bytes.Length);
                    var wide = Bitmap.CreateBitmap(stride, h, Bitmap.Config.Argb8888!)!;
                    wide.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(bytes));
                    if (stride == w) return wide;
                    var crop = Bitmap.CreateBitmap(wide, 0, 0, w, h)!;
                    wide.Recycle();
                    return crop;
                }
                finally { AHardwareBuffer_unlock(ab, IntPtr.Zero); }
            }
            catch (Exception ex) { Log.Warn(Tag, $"cpu read failed: {ex.Message}"); return null; }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(desc); }
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
    public static readonly string[] Actions = { "PING", "SHOT", "SHOTMP", "TAP", "MULTIC", "WALK", "SWIPE", "PINCH", "MULTI", "OCR", "MATCH", "SEQ", "DRAG" };

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
            try
            {
                // A broadcast gets ~10 s: release it after 8 s even if the work (e.g. 100 shots) runs on.
                var work = _service.RunAsync(name, intent);
                await Task.WhenAny(work, Task.Delay(8000));
                pending?.Finish();
                pending = null;
                await work;
            }
            catch (Exception ex) { Log.Error(SpikeService.Tag, $"{name} failed: {ex}"); }
            finally { pending?.Finish(); }
        });
    }
}
