using WuxAuto.Core;
using WuxAuto.Mobile.Engine;

namespace WuxAuto.Mobile.Pages;

/// <summary>Mode, Start / Stop / Panic, dry run, the 3-line status, the last kill and the debug bundle.</summary>
public partial class MainPage : ContentPage
{
    private bool _visible;
    private int _shown;
    private bool _syncing;

    public MainPage()
    {
        InitializeComponent();
    }

    private static MobileEngine Engine => EngineHost.Engine;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        int shown = ++_shown;
        Engine.StateChanged += OnEngineState;
        Engine.ModeChanged += OnEngineState;
        Sync();
        // One timer per appearance: an older one (the page left and came back within a second) ends itself.
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            bool current = _visible && shown == _shown;
            if (current)
                Sync();
            return current;
        });
    }

    protected override void OnDisappearing()
    {
        _visible = false;
        Engine.StateChanged -= OnEngineState;
        Engine.ModeChanged -= OnEngineState;
        base.OnDisappearing();
    }

    private void OnEngineState() => Dispatcher.Dispatch(Sync);

    private void Sync()
    {
        var engine = Engine;
        _syncing = true;
        try
        {
            ServiceHint.IsVisible = PilotAccessibilityService.Instance == null;
            // LOD is the only mode on the phone until M5 (the Laby button stays disabled, "coming in M5").
            ModeLod.IsChecked = true;
            ModeLaby.IsChecked = false;
            bool running = engine.State == EngineState.Running;
            ModeLod.IsEnabled = !running;
            DryRun.IsChecked = engine.Settings.Sequence.DryRun;
            DryRun.IsEnabled = !running;
            StartButton.IsEnabled = !running;
            Status.Text = engine.StatusText.TrimEnd('\n');
            Status.TextColor = engine.State switch
            {
                EngineState.Running => Color.FromArgb("#2E7D32"),
                EngineState.Error => Color.FromArgb("#C62828"),
                _ => Colors.Gray,
            };
            LastKill.Text = engine.LastKill.Length > 0 ? $"Last kill: {engine.LastKill}" : "Last kill: -";
            var capture = CaptureService.Current;
            Capture.Text = capture == null
                ? "Screen capture: off"
                : $"Screen capture: {capture.Width}x{capture.Height}, {capture.FrameCount} frame(s)";
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnModeChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (_syncing || !e.Value || sender != ModeLod)
            return;
        var engine = Engine;
        var mode = RunMode.AutoLod;
        if (engine.Settings.Sequence.Mode == mode)
            return;
        engine.Settings.Sequence.Mode = mode;
        _ = Task.Run(engine.Save);
    }

    private void OnDryRunChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (_syncing)
            return;
        var engine = Engine;
        engine.Settings.Sequence.DryRun = e.Value;
        _ = Task.Run(engine.Save);
    }

    private void OnStart(object? sender, EventArgs e)
    {
        var engine = Engine;
        engine.Start(engine.Settings.Sequence.Mode);
        Sync();
    }

    private void OnStop(object? sender, EventArgs e) => Engine.Stop("Stop on the app's page");

    private void OnPanic(object? sender, EventArgs e) => Engine.Panic();

    private void OnStopCapture(object? sender, EventArgs e)
    {
        Engine.StopCapture();
        Sync();
    }

    private async void OnDebugBundle(object? sender, EventArgs e)
    {
        try
        {
            string result = await DebugBundle.ShareAsync();
            if (result.StartsWith("Nothing", StringComparison.Ordinal))
                await DisplayAlertAsync("Debug bundle", result, "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Debug bundle", $"Could not build it: {ex.Message}", "OK");
        }
    }
}
