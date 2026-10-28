using System.Runtime.InteropServices;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Phone;
using Activity = Android.App.Activity;

namespace WuxAuto.Mobile;

/// <summary>The real display size: <c>WindowManager.MaximumWindowMetrics</c>, system bars included (Resources.DisplayMetrics may leave them out or report portrait, M0).</summary>
public static class DisplaySize
{
    /// <summary>Width and height as the display stands now (the largest window it allows, system bars included).</summary>
    public static (int W, int H) Current(Context context)
    {
        var wm = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
        using var metrics = wm?.MaximumWindowMetrics;
        using var bounds = metrics?.Bounds;
        if (bounds != null && bounds.Width() > 0 && bounds.Height() > 0)
            return (bounds.Width(), bounds.Height());

        var dm = context.Resources?.DisplayMetrics;
        return (dm?.WidthPixels ?? 0, dm?.HeightPixels ?? 0);
    }

    /// <summary>The same size turned landscape (the game only runs landscape): the longer side is the width.</summary>
    public static (int W, int H) Landscape(Context context)
    {
        var (w, h) = Current(context);
        return (Math.Max(w, h), Math.Min(w, h));
    }
}

/// <summary>
/// Asks for screen-capture consent (the system dialog), hands the grant to <see cref="CaptureService"/> and brings the
/// game back to the front. Consent is once per capture session; from Android 14 a grant works for one projection only.
/// </summary>
[Activity(Name = "io.github.atherosxd.wuxauto.ConsentActivity", Exported = false,
    Theme = "@android:style/Theme.Translucent.NoTitleBar", ExcludeFromRecents = true,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape)]   // stay landscape over the game
public class ConsentActivity : Activity
{
    /// <summary>Intent extra (bool, default true): launch the game after the grant.</summary>
    public const string ExtraBringGame = "bringGame";

    const int RequestCode = 1;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (savedInstanceState != null)
            return; // recreated while the dialog is up: the request is still pending
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(mpm.CreateScreenCaptureIntent(), RequestCode);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RequestCode)
        {
            if (resultCode == Result.Ok && data != null)
            {
                CaptureService.Start(this, (int)resultCode, data);
                if (Intent?.GetBooleanExtra(ExtraBringGame, true) ?? true)
                    PilotAccessibilityService.LaunchGame(this);
            }
            else
            {
                Log.Warn(MainApplication.Tag, "capture consent denied");
            }
        }

        Finish();
    }
}

/// <summary>
/// Screen capture through MediaProjection: a foreground service of type mediaProjection mirrors the display into an
/// ImageReader. Frames are copied out at most every <see cref="MinFrameIntervalMs"/>; a black frame (screen off, a
/// secure window) never replaces the last good one. Whether a frame shows the game (a launcher frame is not black)
/// is the engine's zone-title check, not this class's.
/// </summary>
[Service(Name = "io.github.atherosxd.wuxauto.CaptureService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public class CaptureService : Service
{
    public const int MinFrameIntervalMs = 100;

    /// <summary>A frame is "black" when fewer than this fraction of sampled pixels are lit.</summary>
    const double BlackLit = 0.05;

    const string Channel = "wuxauto-capture";
    const int NotificationId = 7;
    const string ExtraResultCode = "resultCode";
    const string ExtraData = "data";
    const string Tag = MainApplication.Tag;

    static volatile CaptureService? _current;

    /// <summary>The running capture, or null.</summary>
    public static CaptureService? Current => _current;

    /// <summary>Raised (on a background thread) when the capture stops for any reason: Stop, consent revoked, projection killed.</summary>
    public static event Action? Stopped;

    /// <summary>Shows the consent dialog (from any context); the capture starts when the user allows it.</summary>
    public static void RequestConsent(Context context, bool bringGame = true)
    {
        var intent = new Intent(context, typeof(ConsentActivity)).AddFlags(ActivityFlags.NewTask);
        intent.PutExtra(ConsentActivity.ExtraBringGame, bringGame);
        context.StartActivity(intent);
    }

    internal static void Start(Context context, int resultCode, Intent data)
    {
        var intent = new Intent(context, typeof(CaptureService));
        intent.PutExtra(ExtraResultCode, resultCode);
        intent.PutExtra(ExtraData, data);
        context.StartForegroundService(intent);
    }

    /// <summary>Stops the running capture, if any.</summary>
    public static void StopCapture() => _current?.Teardown("stopped");

    readonly object _frameLock = new();
    readonly object _imageLock = new();
    byte[]? _latest;      // RGBA rows, _rowStride bytes apart; guarded by _frameLock
    byte[]? _spare;       // the buffer the next frame is copied into (only the image thread touches it)
    long _latestTicks;    // FrameGate.NowTicks() of _latest
    int _rowStride;
    long _lastCopyTicks;

    MediaProjection? _projection;
    ImageReader? _reader;
    VirtualDisplay? _display;
    HandlerThread? _imageThread;
    int _torndown;

    /// <summary>Capture size (landscape, the real display size).</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Frames copied so far (black ones excluded).</summary>
    public long FrameCount { get; private set; }

    /// <summary>
    /// A BGRA copy of the newest good frame and its <see cref="FrameGate.NowTicks"/> stamp, or null before the first
    /// one. Every call returns a new Mat the caller owns.
    /// </summary>
    public (Mat Bgra, long Ticks)? LatestFrame
    {
        get
        {
            lock (_frameLock)
            {
                if (_latest == null)
                    return null;

                // A Mat header over the pinned managed copy (row stride = the ImageReader's), read by OpenCV only:
                // nothing is written into a Mat from managed code (CLAUDE.md section 10).
                var handle = GCHandle.Alloc(_latest, GCHandleType.Pinned);
                try
                {
                    using var rgba = new Mat(Height, Width, DepthType.Cv8U, 4, handle.AddrOfPinnedObject(), _rowStride);
                    var bgra = new Mat();
                    CvInvoke.CvtColor(rgba, bgra, ColorConversion.Rgba2Bgra);
                    return (bgra, _latestTicks);
                }
                finally
                {
                    handle.Free();
                }
            }
        }
    }

    /// <summary>Stamp of the newest good frame (0 = none yet).</summary>
    public long LatestTicks
    {
        get { lock (_frameLock) return _latestTicks; }
    }

    /// <summary>
    /// The newest turned-down frame (black, an unexpected size or stride, a failed copy): when, why, and how many so far.
    /// Frames that keep coming but are all turned down mean the capture is not usable (the engine's loop gate).
    /// </summary>
    public (long Ticks, string? Reason, long Count) LastRejected
    {
        get { lock (_frameLock) return (_rejectedTicks, _rejectReason, _rejectedCount); }
    }

    long _rejectedTicks;
    string? _rejectReason;
    long _rejectedCount;

    /// <summary>Records a turned-down frame; the log gets a line only when the reason changes (not one per frame).</summary>
    void Reject(string reason)
    {
        bool changed;
        lock (_frameLock)
        {
            changed = reason != _rejectReason;
            _rejectedTicks = FrameGate.NowTicks();
            _rejectReason = reason;
            _rejectedCount++;
        }

        if (changed)
            Log.Warn(Tag, $"capture: frame turned down: {reason}");
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Must be in the foreground with the mediaProjection type before GetMediaProjection (required from Android 14).
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.CreateNotificationChannel(new NotificationChannel(Channel, "Screen capture", NotificationImportance.Low));
        var notification = new Notification.Builder(this, Channel)
            .SetContentTitle("WuxAuto is reading the screen")!
            .SetSmallIcon(Android.Resource.Drawable.IcMenuView)!
            .SetOngoing(true)!
            .Build()!;
        StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);

        int code = intent?.GetIntExtra(ExtraResultCode, 0) ?? 0;
        var data = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent?.GetParcelableExtra(ExtraData, Java.Lang.Class.FromType(typeof(Intent))) as Intent
#pragma warning disable CA1422 // the typed overload exists from Android 13; MuMu runs 12
            : intent?.GetParcelableExtra(ExtraData) as Intent;
#pragma warning restore CA1422
        if (data == null || _current != null)
        {
            Log.Warn(Tag, data == null ? "capture: no consent data" : "capture: already running");
            if (_current != this)
                StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        try
        {
            StartProjection(code, data);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"capture failed to start: {ex}");
            Teardown("start failed");
        }

        return StartCommandResult.NotSticky;
    }

    void StartProjection(int code, Intent data)
    {
        // The system may hand a new start to this same instance after an earlier capture ended.
        Interlocked.Exchange(ref _torndown, 0);
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        _projection = mpm.GetMediaProjection(code, data) ?? throw new InvalidOperationException("no projection");
        _imageThread = new HandlerThread("wux-capture");
        _imageThread.Start();
        var handler = new Handler(_imageThread.Looper!);
        // Registered before the virtual display (required from Android 14): the system stopping the projection ends the capture.
        _projection.RegisterCallback(new ProjectionCallback(this), handler);

        (Width, Height) = DisplaySize.Landscape(this);
        int dpi = (int)(Resources?.Configuration?.DensityDpi ?? 320);
        _reader = ImageReader.NewInstance(Width, Height, (Android.Graphics.ImageFormatType)1 /* RGBA_8888 */, 2)!;
        _reader.SetOnImageAvailableListener(new ImageListener(this), handler);
        _display = _projection.CreateVirtualDisplay("wuxauto", Width, Height, dpi,
            (DisplayFlags)16 /* AUTO_MIRROR */, _reader.Surface, null, handler)
            ?? throw new InvalidOperationException("no virtual display");
        _current = this;
        Log.Info(Tag, $"capture started {Width}x{Height} dpi {dpi}");
    }

    void OnImage(ImageReader reader)
    {
        // One lock across acquire, copy and close: Teardown takes it before releasing the display and closing the
        // reader, so the plane's native buffer is never read after it is freed.
        lock (_imageLock)
        {
            if (_torndown != 0 || !ReferenceEquals(reader, _reader))
                return;

            Android.Media.Image? image;
            try
            {
                image = reader.AcquireLatestImage();
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, $"capture: acquire failed: {ex.Message}");
                return;
            }

            if (image == null)
                return;

            using (image)
            {
                try
                {
                    CopyFrame(image);
                }
                catch (Exception ex)
                {
                    Reject($"frame copy failed: {ex.Message}");
                }
                finally
                {
                    image.Close(); // an unclosed Image counts against maxImages and stalls the reader
                }
            }
        }
    }

    void CopyFrame(Android.Media.Image image)
    {
        long now = FrameGate.NowTicks();
        if (now - _lastCopyTicks < MinFrameIntervalMs * TimeSpan.TicksPerMillisecond)
            return;

        var planes = image.GetPlanes()!;
        try
        {
            var plane = planes[0];
            int rowStride = plane.RowStride;
            if (plane.PixelStride != 4 || image.Width != Width || image.Height != Height || rowStride < Width * 4)
            {
                Reject($"unexpected image {image.Width}x{image.Height} (capture {Width}x{Height}) px stride {plane.PixelStride} row {rowStride}");
                return;
            }

            using var buffer = plane.Buffer!;
            int length = Math.Min(buffer.Capacity(), rowStride * Height);
            if (_spare == null || _spare.Length != rowStride * Height)
                _spare = new byte[rowStride * Height];
            // Out of the direct buffer into managed memory (a read; the Mat is built over this copy later).
            Marshal.Copy(buffer.GetDirectBufferAddress(), _spare, 0, length);
            _lastCopyTicks = now;

            if (LitFraction(_spare, rowStride) < BlackLit)
            {
                Reject("black frame");
                return; // keep the last good frame
            }

            lock (_frameLock)
            {
                (_latest, _spare) = (_spare, _latest);
                _rowStride = rowStride;
                _latestTicks = now;
                FrameCount++;
            }
        }
        finally
        {
            foreach (var plane in planes)
                plane.Dispose();
        }
    }

    /// <summary>Fraction of a 32x20 sample grid that is not pure black.</summary>
    double LitFraction(byte[] rgba, int rowStride)
    {
        int lit = 0, n = 0;
        for (int y = Height / 40; y < Height; y += Math.Max(1, Height / 20))
        {
            for (int x = Width / 64; x < Width; x += Math.Max(1, Width / 32), n++)
            {
                int i = y * rowStride + x * 4;
                if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0)
                    lit++;
            }
        }

        return n == 0 ? 0 : (double)lit / n;
    }

    /// <summary>Releases in the order VirtualDisplay → ImageReader → projection, leaves the foreground and stops. Runs once.</summary>
    void Teardown(string why)
    {
        if (Interlocked.Exchange(ref _torndown, 1) != 0)
            return;

        // Not while a frame is being copied out of the reader (OnImage holds this lock).
        lock (_imageLock)
        {
            try { _display?.Release(); } catch (Exception) { }
            _display = null;
            try { _reader?.Close(); } catch (Exception) { }
            _reader = null;
        }

        try { _projection?.Stop(); } catch (Exception) { }
        _projection = null;
        _imageThread?.QuitSafely();
        _imageThread = null;

        if (_current == this)
            _current = null;
        lock (_frameLock)
        {
            _latest = null;
            _latestTicks = 0;
            _rejectedTicks = 0;
            _rejectReason = null;
        }

        try
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
        catch (Exception) { }

        Log.Info(Tag, $"capture ended: {why}");
        try { Stopped?.Invoke(); } catch (Exception ex) { Log.Warn(Tag, $"capture Stopped handler: {ex.Message}"); }
    }

    public override void OnDestroy()
    {
        Teardown("service destroyed");
        base.OnDestroy();
    }

    sealed class ProjectionCallback(CaptureService owner) : MediaProjection.Callback
    {
        public override void OnStop() => owner.Teardown("projection stopped by the system");
    }

    sealed class ImageListener(CaptureService owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader != null)
                owner.OnImage(reader);
        }
    }
}
