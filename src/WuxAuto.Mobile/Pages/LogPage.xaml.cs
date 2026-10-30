using System.Text;
using WuxAuto.Core;
using WuxAuto.Mobile.Engine;

namespace WuxAuto.Mobile.Pages;

/// <summary>The newest log lines (the whole log is the file named at the top, also in the debug bundle).</summary>
public partial class LogPage : ContentPage
{
    private const int ShownLines = 300;

    private bool _visible;
    private int _shown;
    private int _dirty;

    public LogPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        int shown = ++_shown;
        var engine = EngineHost.Engine;
        LogFile.Text = engine.LogFile;
        engine.Log += OnLog;
        Show();
        // At most two redraws a second, however fast the pilot logs; one timer per appearance.
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(500), () =>
        {
            bool current = _visible && shown == _shown;
            if (current && Interlocked.Exchange(ref _dirty, 0) != 0)
                Show();
            return current;
        });
    }

    protected override void OnDisappearing()
    {
        _visible = false;
        EngineHost.Engine.Log -= OnLog;
        base.OnDisappearing();
    }

    private void OnLog(LogEntry entry) => Interlocked.Exchange(ref _dirty, 1);

    private void Show()
    {
        var recent = EngineHost.Engine.RecentLog();
        var text = new StringBuilder();
        foreach (var entry in recent.Skip(Math.Max(0, recent.Count - ShownLines)))
            text.AppendLine(entry.ToString());
        Lines.Text = text.ToString();
        _ = Scroller.ScrollToAsync(Lines, ScrollToPosition.End, animated: false);
    }
}
