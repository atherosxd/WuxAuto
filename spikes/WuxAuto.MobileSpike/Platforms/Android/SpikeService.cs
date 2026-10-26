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
                float jx = I("jx", (int)JoyX), jy = I("jy", (int)JoyY), jr = Cl(I("jr", (int)JoyR), 10, 400);
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
                float jx = I("jx", (int)JoyX), jy = I("jy", (int)JoyY), jr = Cl(I("jr", (int)JoyR), 10, 400);
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
        }
        return Task.CompletedTask;
    }

    /// <summary>Joystick as a continued stroke: gesture 1 holds it (willContinue), gesture 2 continues it and adds the tap.</summary>
    async Task MultiContinuedAsync(Intent i, int W, int H)
    {
        int I(string k, int d = 0) => i.GetIntExtra(k, d);
        var ms = Math.Min(Math.Max(I("ms", 2500), 50), 10000);
        float jx = I("jx", (int)JoyX), jy = I("jy", (int)JoyY), jr = Math.Min(Math.Max(I("jr", (int)JoyR), 10), 400);
        float dx = I("dx", 0), dy = I("dy", -100), len = Math.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
        float ex = jx + dx / len * jr, ey = jy + dy / len * jr;
        int tx = Math.Min(Math.Max(I("tx"), 0), W - 1), ty = Math.Min(Math.Max(I("ty"), 0), H - 1);
        var first = new GestureDescription.StrokeDescription(Line(jx, jy, ex, ey), 0, ms / 2, true);
        await GestureAsync("MULTIC-1", new[] { first });
        var tap = new APath(); tap.MoveTo(tx, ty); tap.LineTo(tx + 1, ty);
        var second = first.ContinueStroke(Line(ex, ey, ex, ey + 1), 0, ms / 2, false)!;
        await GestureAsync("MULTIC-2", new[] { second, Stroke(tap, 50, 100) });
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
    public static readonly string[] Actions = { "PING", "SHOT", "SHOTMP", "TAP", "MULTIC", "WALK", "SWIPE", "PINCH", "MULTI" };

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
