using Android.App;
using Android.Content;
using Android.Util;
using WuxAuto.Mobile.Engine;

namespace WuxAuto.Mobile;

/// <summary>
/// The notification while the pilot runs: its status and a Stop button (a second way to stop next to the floating
/// control). The capture's foreground service keeps the process alive; this one only informs. Needs POST_NOTIFICATIONS
/// on Android 13+ (asked on the Setup page); without it nothing shows and the floating control still works.
/// </summary>
public static class PilotNotification
{
    public const string Channel = "wuxauto-pilot";
    public const int Id = 8;

    private static bool _shown;
    private static string? _lastText;

    /// <summary>Shows, refreshes or removes the notification for the engine's state. Main thread.</summary>
    public static void Update(Context context, MobileEngine engine)
    {
        var nm = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (nm == null)
            return;

        if (engine.State != EngineState.Running)
        {
            if (_shown)
                nm.Cancel(Id);
            _shown = false;
            _lastText = null;
            return;
        }

        string[] lines = engine.StatusText.Split('\n');
        string title = lines[0];
        string text = string.Join(" - ", lines.Skip(1).Where(l => l.Length > 0));
        if (_shown && title + text == _lastText)
            return;

        try
        {
            nm.CreateNotificationChannel(new NotificationChannel(Channel, "Pilot", NotificationImportance.Low));
            var stop = PendingIntent.GetBroadcast(context, 0,
                new Intent(context, typeof(PilotStopReceiver)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var open = PendingIntent.GetActivity(context, 0,
                new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var builder = new Notification.Builder(context, Channel)
                .SetContentTitle($"WuxAuto: {title}")!
                .SetContentText(text)!
                .SetStyle(new Notification.BigTextStyle().BigText(text))!
                .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)!
                .SetOngoing(true)!
                .SetOnlyAlertOnce(true)!
                .SetContentIntent(open)!
                .AddAction(new Notification.Action.Builder((Android.Graphics.Drawables.Icon?)null, "Stop", stop).Build());
            nm.Notify(Id, builder.Build());
            _shown = true;
            _lastText = title + text;
        }
        catch (Exception ex)
        {
            Log.Warn(MainApplication.Tag, $"pilot notification: {ex.Message}");
        }
    }
}

/// <summary>The notification's Stop button.</summary>
[BroadcastReceiver(Name = "io.github.atherosxd.wuxauto.PilotStopReceiver", Exported = false)]
public sealed class PilotStopReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent) => EngineHost.Engine.Stop("Stop in the notification");
}
