using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views.Accessibility;

namespace WuxAuto.Mobile;

/// <summary>
/// The accessibility service: the only way to touch another app's window. It dispatches the gestures
/// <see cref="AndroidTouchInput"/> builds and tells which app is in front. The user turns it on in the system's
/// accessibility settings; nothing here works until then.
/// </summary>
[Service(Name = "io.github.atherosxd.wuxauto.PilotAccessibilityService",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service")]
public class PilotAccessibilityService : AccessibilityService
{
    public const string GamePackage = "com.wuxen.wuxen2";

    /// <summary>The game's player activity: the fallback when the package manager has no launch intent for it.</summary>
    public const string GameActivity = "com.wuxen.gxd.GXDPlayerActivity";

    static volatile PilotAccessibilityService? _instance;

    /// <summary>The connected service, or null while it is off.</summary>
    public static PilotAccessibilityService? Instance => _instance;

    /// <summary>The touch input; lives as long as the process, gestures go out only while the service is connected.</summary>
    public static AndroidTouchInput Touch { get; } = new();

    volatile string? _lastEventPackage;
    HandlerThread? _callbackThread;
    Handler? _callbackHandler;

#if DEBUG
    DebugReceiver? _debug;
#endif

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        _callbackThread = new HandlerThread("wux-gestures");
        _callbackThread.Start();
        _callbackHandler = new Handler(_callbackThread.Looper!);
        _instance = this;
#if DEBUG
        _debug = DebugReceiver.Register(this);
#endif
        Log.Info(MainApplication.Tag, "accessibility service connected");
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e?.EventType == EventTypes.WindowStateChanged && e.PackageName is { } package)
            _lastEventPackage = package;
    }

    public override void OnInterrupt() { }

    public override bool OnUnbind(Intent? intent)
    {
        Disconnect();
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Disconnect();
        base.OnDestroy();
    }

    void Disconnect()
    {
        if (_instance != this)
            return;

        // Lift any held finger while dispatching still works, then stop dispatching.
        Touch.Release();
        _instance = null;
#if DEBUG
        _debug?.Unregister(this);
        _debug = null;
#endif
        _callbackThread?.QuitSafely();
        _callbackThread = null;
        Log.Info(MainApplication.Tag, "accessibility service disconnected");
    }

    /// <summary>
    /// The package of the window that has the input focus: the active window's root node, else the package of the last
    /// window-state event (a window list can be briefly unavailable). Null when nothing is known.
    /// </summary>
    public string? ActivePackage()
    {
        try
        {
            var windows = Windows;
            if (windows != null)
            {
                string? active = null;
                foreach (var window in windows)
                {
                    using (window)
                    {
                        if (active != null || !window.IsActive)
                            continue;
                        using var root = window.Root;
                        active = root?.PackageName;
                    }
                }

                if (active != null)
                    return active;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(MainApplication.Tag, $"window list unavailable: {ex.Message}");
        }

        return _lastEventPackage;
    }

    public bool IsGameActive() => ActivePackage() == GamePackage;

    /// <summary>Brings the game's task to the front (as the launcher icon would; a running game is not restarted).</summary>
    public bool LaunchGame() => LaunchGame(this);

    /// <inheritdoc cref="LaunchGame()"/>
    public static bool LaunchGame(Context context)
    {
        try
        {
            var intent = context.PackageManager?.GetLaunchIntentForPackage(GamePackage)
                ?? new Intent(Intent.ActionMain).SetClassName(GamePackage, GameActivity);
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
            context.StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(MainApplication.Tag, $"could not bring the game to the front: {ex.Message}");
            return false;
        }
    }

    /// <summary>Dispatches <paramref name="gesture"/>; the task ends with true when it completed, false when it was cancelled or refused.</summary>
    internal Task<bool> DispatchAsync(GestureDescription gesture)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sent;
        try
        {
            // Callbacks on our own looper: a caller may block the main thread (Release from the UI) while it waits.
            sent = DispatchGesture(gesture, new Callback(done), _callbackHandler);
        }
        catch (Exception ex)
        {
            Log.Warn(MainApplication.Tag, $"gesture refused: {ex.Message}");
            sent = false;
        }

        if (!sent)
            done.TrySetResult(false);
        return done.Task;
    }

    sealed class Callback(TaskCompletionSource<bool> done) : GestureResultCallback
    {
        public override void OnCompleted(GestureDescription? gestureDescription) => done.TrySetResult(true);

        public override void OnCancelled(GestureDescription? gestureDescription) => done.TrySetResult(false);
    }
}
