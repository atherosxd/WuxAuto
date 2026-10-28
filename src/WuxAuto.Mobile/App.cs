using WuxAuto.Mobile.Pages;

namespace WuxAuto.Mobile;

/// <summary>The app's pages: Pilot (start / stop / status), Setup (checklist), Settings and Log, as tabs.</summary>
public class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var tabs = new TabBar();
        tabs.Items.Add(new ShellContent { Title = "Pilot", ContentTemplate = new DataTemplate(typeof(MainPage)) });
        tabs.Items.Add(new ShellContent { Title = "Setup", ContentTemplate = new DataTemplate(typeof(SetupPage)) });
        tabs.Items.Add(new ShellContent { Title = "Settings", ContentTemplate = new DataTemplate(typeof(SettingsPage)) });
        tabs.Items.Add(new ShellContent { Title = "Log", ContentTemplate = new DataTemplate(typeof(LogPage)) });
        var shell = new Shell();
        shell.Items.Add(tabs);
        return new Window(shell) { Title = "WuxAuto" };
    }
}
