using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WuxAuto;
using WuxAuto.Core;

namespace WuxAuto.UiShot;

/// <summary>
/// Development helper: instantiates the real main window, lets WPF lay it out and renders the
/// visual tree to a PNG with RenderTargetBitmap. Works without an interactive/locked desktop,
/// which PrintWindow does not.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(Path.GetTempPath(), "wuxauto-ui.png");

        AppPaths.EnsureCreated();

        // The window saves the settings when it closes, and the render below ticks check boxes
        // (Dry run among them) - put the user's file back afterwards.
        string? savedSettings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;

        var app = new App();
        app.InitializeComponent();

        var window = new MainWindow
        {
            Width = 1380,
            Height = 880,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowActivated = false,
        };

        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();

        // Force a representative state so the render shows real data: a selected process and a
        // couple of ticked check boxes.
        var combo = Descendants<ComboBox>(window).FirstOrDefault();
        if (combo != null && combo.Items.Count > 0)
            combo.SelectedIndex = 0;

        foreach (var check in Descendants<CheckBox>(window).Take(2))
            check.IsChecked = true;

        // --update-banner: show the "update available" bar with a made-up release.
        if (args.Contains("--update-banner") && window.DataContext is WuxAuto.ViewModels.MainViewModel viewModel)
        {
            viewModel.AvailableUpdate = new UpdateInfo(new Version(9, 9, 9), "v9.9.9", "notes", "https://example/app.zip", 0, UpdateService.ReleasesPage);
            viewModel.UpdateStatus = "Update v9.9.9 available.";
        }

        if (args.Contains("--settings"))
        {
            var expander = Descendants<Expander>(window).FirstOrDefault();
            if (expander != null)
                expander.IsExpanded = true;
        }

        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();

        // --bottom: scroll the left panel to its end (the settings sections are below the fold).
        if (args.Contains("--bottom"))
        {
            foreach (var viewer in Descendants<ScrollViewer>(window))
                viewer.ScrollToEnd();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        int width = Math.Max(1, (int)Math.Round(window.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Round(window.ActualHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(output))
            encoder.Save(stream);

        Console.WriteLine($"{output} ({width}x{height}, dpi x{dpi.DpiScaleX:0.##})");

        window.Close();
        app.Shutdown();

        if (savedSettings != null)
            File.WriteAllText(AppPaths.SettingsFile, savedSettings);
        else if (File.Exists(AppPaths.SettingsFile))
            File.Delete(AppPaths.SettingsFile);
        return 0;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }
}
