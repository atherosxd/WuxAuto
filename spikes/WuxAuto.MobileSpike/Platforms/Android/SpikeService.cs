using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views.Accessibility;

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
    public static Bitmap? LastShot { get; set; }

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
    public static readonly string[] Actions = { "PING" };

    readonly SpikeService _service;

    public SpikeReceiver(SpikeService service) => _service = service;

    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action ?? "";
        var name = action.StartsWith(SpikeService.ActionPrefix) ? action[SpikeService.ActionPrefix.Length..] : action;
        Log.Info(SpikeService.Tag, $"received {name}");
        // Tasks 2-3 dispatch here: switch (name) { case "SHOT": ... }
    }
}
