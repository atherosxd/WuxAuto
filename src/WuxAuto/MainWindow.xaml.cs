using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using WuxAuto.Core;
using WuxAuto.Interop;
using WuxAuto.ViewModels;

namespace WuxAuto;

public partial class MainWindow : Window
{
    private GlobalHotkey? _hotkeys;

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();

        if (DataContext is MainViewModel viewModel)
            viewModel.LogAppended += ScrollLog;

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// Smaller screens (user, 2026-10-23): the 1380x880 window ran past the screen and its title bar
    /// buttons were out of reach. Shrink it to the work area (the screen without the taskbar) when it
    /// does not fit; the left column scrolls.
    /// </summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        double maxWidth = Math.Max(400, area.Width - 16);
        double maxHeight = Math.Max(300, area.Height - 16);
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;

            // Match the (dark) app theme in the non-client area / title bar.
            int enabled = 1;
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            _hotkeys = new GlobalHotkey(
                handle,
                onToggle: () => ViewModel?.ToggleStartCommand.Execute(null),
                onPanic: () => ViewModel?.PanicCommand.Execute(null),
                onSnapshot: () => ViewModel?.SnapshotCommand.Execute(null));

            if (ViewModel != null)
            {
                if (_hotkeys.ToggleRegistered)
                    ViewModel.LogEntries.Add(new LogEntry(LogLevel.Info, "Global hotkeys armed: F9 start/stop, F10 panic, F11 save frame."));
                else
                    ViewModel.LogEntries.Add(new LogEntry(LogLevel.Warning, "Could not register F9; another app already owns it."));
            }
        }
        catch (Exception ex)
        {
            ViewModel?.LogEntries.Add(new LogEntry(LogLevel.Warning, $"Hotkeys unavailable: {ex.Message}"));
        }
    }

    private MiniWindow? _mini;

    /// <summary>Mini mode (user): hide this window, show the small one at the top right.</summary>
    private void OnMiniMode(object sender, RoutedEventArgs e)
    {
        if (_mini != null)
        {
            _mini.Activate();
            return;
        }

        _mini = new MiniWindow(this);
        _mini.Closed += (_, _) => _mini = null;
        _mini.Show();
        Hide();
    }

    private void ScrollLog()
    {
        if (LogList.Items.Count == 0)
            return;

        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _hotkeys?.Dispose();
        _hotkeys = null;

        try
        {
            ViewModel?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4));
        }
        catch
        {
            // best effort shutdown
        }
    }
}
