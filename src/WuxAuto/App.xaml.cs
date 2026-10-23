using System.Windows;
using System.Windows.Threading;
using WuxAuto.Core;

namespace WuxAuto;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("crash", args.ExceptionObject.ToString() ?? "unknown error");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            Fail($"Cannot create the data folder {AppPaths.Root}:\n\n{ex.Message}", null);
            return;
        }

        var check = StartupCheck.Run();
        if (check.HasProblems)
        {
            string? report = StartupCheck.WriteReport(check);
            if (check.Fatal.Count > 0)
            {
                Fail("WuxAuto cannot start on this PC:\n\n" + Bullets(check.Fatal.Concat(check.Warnings)), report);
                return;
            }

            MessageBox.Show(
                "WuxAuto will start, but:\n\n" + Bullets(check.Warnings) + SavedTo(report),
                "WuxAuto",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            Fail($"WuxAuto could not open its window:\n\n{ex.GetBaseException().Message}",
                CrashLog.Write("crash", ex.ToString()));
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string? log = CrashLog.Write("crash", e.Exception.ToString());
        e.Handled = true;

        // Without a visible window the process would keep running with nothing on screen.
        if (MainWindow is not { IsVisible: true })
        {
            Fail($"Unexpected error:\n\n{e.Exception.GetBaseException().Message}", log);
            return;
        }

        MessageBox.Show(
            $"Unexpected error:\n\n{e.Exception.Message}" + SavedTo(log),
            "WuxAuto",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void Fail(string message, string? report)
    {
        MessageBox.Show(message + SavedTo(report), "WuxAuto", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private static string Bullets(IEnumerable<string> lines) => string.Join("\n\n", lines.Select(l => "• " + l));

    private static string SavedTo(string? path) =>
        path == null ? string.Empty : $"\n\nDetails saved to:\n{path}\n(Ctrl+C copies this message.)";
}
