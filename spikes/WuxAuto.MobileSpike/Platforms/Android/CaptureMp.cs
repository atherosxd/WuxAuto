using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Util;
using Android.Views;
using AImage = Android.Media.Image;
using Stopwatch = System.Diagnostics.Stopwatch;
using Activity = Android.App.Activity;

namespace WuxAuto.MobileSpike;

/// <summary>Transparent activity that only asks for screen-capture consent, then hands the result to <see cref="MpService"/>.</summary>
[Activity(Name = "io.github.atherosxd.wuxauto.spike.ConsentActivity", Exported = false,
    Theme = "@android:style/Theme.Translucent.NoTitleBar", ExcludeFromRecents = true)]
public class ConsentActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(mpm.CreateScreenCaptureIntent(), 1);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode == Result.Ok && data != null)
        {
            var i = new Intent(this, typeof(MpService));
            i.PutExtra("resultCode", (int)resultCode);
            i.PutExtra("data", data);
            i.PutExtra("count", Intent?.GetIntExtra("count", 1) ?? 1);
            StartForegroundService(i);
        }
        else Log.Warn(SpikeService.Tag, "mp consent denied");
        Finish();
    }
}

[Service(Name = "io.github.atherosxd.wuxauto.spike.MpService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public class MpService : Service
{
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        const string channel = "wuxspike";
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.CreateNotificationChannel(new NotificationChannel(channel, "Capture", NotificationImportance.Low));
        var n = new Notification.Builder(this, channel)
            .SetContentTitle("WuxAuto spike capturing")
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)!.Build()!;
        // Must run before GetMediaProjection (Android 10+; the type is mandatory from 14).
        StartForeground(7, n, ForegroundService.TypeMediaProjection);

        var code = intent?.GetIntExtra("resultCode", 0) ?? 0;
        var data = intent?.GetParcelableExtra("data") as Intent;
        var count = intent?.GetIntExtra("count", 1) ?? 1;
        if (data == null) { StopSelf(); return StartCommandResult.NotSticky; }
        Task.Run(async () =>
        {
            try { await CaptureAsync(code, data, count); }
            catch (Exception ex) { Log.Error(SpikeService.Tag, $"mp failed: {ex}"); }
            finally { StopForeground(StopForegroundFlags.Remove); StopSelf(); }
        });
        return StartCommandResult.NotSticky;
    }

    async Task CaptureAsync(int code, Intent data, int count)
    {
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        var proj = mpm.GetMediaProjection(code, data)!;
        proj.RegisterCallback(new ProjCb(), new Handler(Looper.MainLooper!));
        var m = Resources!.DisplayMetrics!;
        // The consent dialog and the launcher cover the game for a moment: let it come back to the front first.
        await Task.Delay(8000);
        int w = m.WidthPixels, h = m.HeightPixels;
        using var reader = ImageReader.NewInstance(w, h, (ImageFormatType)1 /* RGBA_8888 */, 2)!;
        var vd = proj.CreateVirtualDisplay("wuxspike", w, h, (int)m.DensityDpi,
            (DisplayFlags)16 /* AUTO_MIRROR */, reader.Surface, null, null)!;
        int ok = 0, fail = 0;
        var total = Stopwatch.StartNew();
        var prev = 0L;
        for (var n = 1; n <= count; n++)
        {
            Bitmap? bmp = null;
            var deadline = total.ElapsedMilliseconds + 3000;
            while (bmp == null && total.ElapsedMilliseconds < deadline)
            {
                var img = reader.AcquireLatestImage();
                if (img == null) { await Task.Delay(15); continue; }
                try { bmp = ToBitmap(img, w, h); }
                finally { img.Close(); } // an unclosed Image counts against maxImages
            }
            var now = total.ElapsedMilliseconds;
            if (bmp == null) { fail++; prev = now; continue; }
            ok++;
            Log.Info(SpikeService.Tag, $"mp shot {n} {w}x{h} t={now - prev} lit={SpikeService.LitFraction(bmp):F3}");
            prev = now;
            SpikeService.LastShot = bmp;
        }
        var secs = Math.Max(total.ElapsedMilliseconds, 1) / 1000.0;
        SpikeService.SaveLast("shot_last_mp.png");
        Log.Info(SpikeService.Tag, $"mp shots ok={ok} fail={fail} fps={ok / secs:F1}");
        vd.Release();
        proj.Stop();
    }

    static Bitmap ToBitmap(AImage img, int w, int h)
    {
        var plane = img.GetPlanes()![0];
        var buf = plane.Buffer!;
        int pixelStride = plane.PixelStride, rowStride = plane.RowStride;
        // Rows are padded: build a wider bitmap, then crop to the real width.
        var wide = Bitmap.CreateBitmap(rowStride / pixelStride, h, Bitmap.Config.Argb8888!)!;
        wide.CopyPixelsFromBuffer(buf);
        if (wide.Width == w) return wide;
        var crop = Bitmap.CreateBitmap(wide, 0, 0, w, h)!;
        wide.Recycle();
        return crop;
    }

    sealed class ProjCb : MediaProjection.Callback { }
}
