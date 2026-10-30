using Android.OS;
using WuxAuto.Core;

namespace WuxAuto.Mobile.Engine;

/// <summary>
/// The one <see cref="MobileEngine"/> of the process, shared by the pages, the floating control (accessibility
/// service) and the notification's Stop. Created on first use; <see cref="AppPaths"/> is configured by then
/// (MainApplication.OnCreate runs before any component). Keeps the notification and the floating control in step with
/// the engine on the main thread.
/// </summary>
public static class EngineHost
{
    private static readonly Lazy<MobileEngine> Lazy = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Handler Main = new(Looper.MainLooper!);
    private static int _uiPosted;

    public static MobileEngine Engine => Lazy.Value;

    public static bool IsCreated => Lazy.IsValueCreated;

    private static MobileEngine Create()
    {
        var settings = SettingsStore.Load();
        var engine = new MobileEngine(settings, () => CaptureService.Current, PilotAccessibilityService.Touch, new MlKitOcrEngine());
        engine.StateChanged += PostUi;
        return engine;
    }

    /// <summary>One UI refresh in flight at a time: the loop may raise StateChanged several times per second.</summary>
    private static void PostUi()
    {
        if (Interlocked.Exchange(ref _uiPosted, 1) != 0)
            return;
        Main.Post(() =>
        {
            Interlocked.Exchange(ref _uiPosted, 0);
            var engine = Engine;
            PilotNotification.Update(Android.App.Application.Context, engine);
            FloatingControl.Current?.Refresh();
        });
    }
}
