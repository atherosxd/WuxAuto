using System.Windows.Media;

namespace WuxAuto.Core;

public enum LogLevel
{
    Trace,
    Info,
    Success,
    Warning,
    Error,
}

public sealed class LogEntry
{
    private static readonly Brush TraceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0x8A, 0x9E)));
    private static readonly Brush InfoBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE6)));
    private static readonly Brush SuccessBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x54, 0xD6, 0x8A)));
    private static readonly Brush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x4D)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x6D, 0x6D)));

    public LogEntry(LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Time = DateTime.Now;
        Brush = level switch
        {
            LogLevel.Trace => TraceBrush,
            LogLevel.Success => SuccessBrush,
            LogLevel.Warning => WarningBrush,
            LogLevel.Error => ErrorBrush,
            _ => InfoBrush,
        };
    }

    public LogLevel Level { get; }
    public string Message { get; }
    public DateTime Time { get; }
    public Brush Brush { get; }
    public string TimeText => Time.ToString("HH:mm:ss.fff");

    public override string ToString() => $"{TimeText} [{Level}] {Message}";

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
