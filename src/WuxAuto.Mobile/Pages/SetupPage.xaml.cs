using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;

namespace WuxAuto.Mobile.Pages;

/// <summary>The one-time setup: accessibility service, notifications, battery exemption, HyperOS hints, capture.</summary>
public partial class SetupPage : ContentPage
{
    public SetupPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = RefreshAsync();
    }

    private static string Mark(bool ok) => ok ? "✓" : "✗";

    private async Task RefreshAsync()
    {
        bool service = PilotAccessibilityService.Instance != null;
        AccessibilityState.Text = $"{Mark(service)} Accessibility service {(service ? "on" : "off")}";
        AccessibilityState.TextColor = service ? Color.FromArgb("#2E7D32") : Color.FromArgb("#C62828");

        bool notifications = await NotificationsAllowedAsync();
        NotificationState.Text = $"{Mark(notifications)} Notifications {(notifications ? "allowed" : "not allowed")}";
        NotificationState.TextColor = notifications ? Color.FromArgb("#2E7D32") : Color.FromArgb("#E65100");
        NotificationButton.IsEnabled = !notifications;

        var power = (PowerManager?)AndroidApp.Context.GetSystemService(Context.PowerService);
        bool exempt = power?.IsIgnoringBatteryOptimizations(AndroidApp.Context.PackageName) == true;
        BatteryState.Text = $"{Mark(exempt)} Battery optimisation {(exempt ? "off for WuxAuto" : "on")}";
        BatteryState.TextColor = exempt ? Color.FromArgb("#2E7D32") : Color.FromArgb("#E65100");

        bool capture = CaptureService.Current != null;
        CaptureState.Text = $"{Mark(capture)} Screen capture {(capture ? "running" : "asked for on Start")}";
        CaptureState.TextColor = capture ? Color.FromArgb("#2E7D32") : Colors.Gray;
    }

    /// <summary>Android 13+ asks for POST_NOTIFICATIONS; before that notifications only need to be switched on.</summary>
    private static async Task<bool> NotificationsAllowedAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            return false;
        var nm = (Android.App.NotificationManager?)AndroidApp.Context.GetSystemService(Context.NotificationService);
        return nm?.AreNotificationsEnabled() != false;
    }

    private void OnAccessibility(object? sender, EventArgs e) =>
        Open(new Intent(Settings.ActionAccessibilitySettings));

    private void OnAppInfo(object? sender, EventArgs e) =>
        Open(new Intent(Settings.ActionApplicationDetailsSettings, AndroidUri.Parse($"package:{AndroidApp.Context.PackageName}")));

    private async void OnNotifications(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
                Open(NotificationSettings());
        }
        else
        {
            Open(NotificationSettings());
        }

        await RefreshAsync();
    }

    private static Intent NotificationSettings() =>
        new Intent(Settings.ActionAppNotificationSettings).PutExtra(Settings.ExtraAppPackage, AndroidApp.Context.PackageName);

    private void OnBattery(object? sender, EventArgs e) =>
        Open(new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, AndroidUri.Parse($"package:{AndroidApp.Context.PackageName}")));

    private void OnRefresh(object? sender, EventArgs e) => _ = RefreshAsync();

    private async void Open(Intent intent)
    {
        try
        {
            intent.AddFlags(ActivityFlags.NewTask);
            AndroidApp.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Setup", $"This phone has no such settings screen ({ex.Message}).", "OK");
        }
    }
}
