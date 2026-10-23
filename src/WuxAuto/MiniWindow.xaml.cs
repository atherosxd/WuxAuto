using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WuxAuto.Core;
using WuxAuto.Interop;
using WuxAuto.ViewModels;

namespace WuxAuto;

/// <summary>
/// Mini mode (user, 2026-10-23): a small always-on-top window with the mode buttons and Start /
/// Stop, placed at the top right of the screen. The main window hides while it is open and comes
/// back with "Full" or when this window is closed.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly Window _main;
    private bool _mainClosed;

    public MiniWindow(Window main)
    {
        InitializeComponent();
        _main = main;
        DataContext = main.DataContext;
        Topmost = (DataContext as MainViewModel)?.Settings.Ui.MiniAlwaysOnTop ?? true;

        // The app is shutting down (main window closed): just go away with it.
        main.Closed += (_, _) =>
        {
            _mainClosed = true;
            Close();
        };
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) => Place();
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch
        {
            // cosmetic only
        }
    }

    /// <summary>
    /// The chosen spot of the work area (the screen without the taskbar, primary screen), 8 px from
    /// its edges (Ui.MiniPosition, user: bottom left by default).
    /// </summary>
    private void Place()
    {
        const double margin = 8;
        var area = SystemParameters.WorkArea;
        var position = (DataContext as MainViewModel)?.Settings.Ui.MiniPosition ?? MiniPosition.BottomLeft;

        double left = area.Left + margin;
        double center = area.Left + (area.Width - ActualWidth) / 2;
        double right = area.Right - ActualWidth - margin;
        double top = area.Top + margin;
        double middle = area.Top + (area.Height - ActualHeight) / 2;
        double bottom = area.Bottom - ActualHeight - margin;

        (Left, Top) = position switch
        {
            MiniPosition.TopLeft => (left, top),
            MiniPosition.TopCenter => (center, top),
            MiniPosition.TopRight => (right, top),
            MiniPosition.MiddleLeft => (left, middle),
            MiniPosition.Middle => (center, middle),
            MiniPosition.MiddleRight => (right, middle),
            MiniPosition.BottomLeft => (left, bottom),
            MiniPosition.BottomCenter => (center, bottom),
            _ => (right, bottom),
        };
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnFull(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Settings.Ui.MiniAlwaysOnTop = Topmost;
            // The mode may have been changed here: the full window's buttons read it again.
            viewModel.RefreshSettings();
        }
        if (_mainClosed)
            return;

        // Closing the mini window in any way brings the full window back - never leave the app
        // running with no window at all.
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
    }
}
