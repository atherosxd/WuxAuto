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
    public LogEntry(LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Time = DateTime.Now;
    }

    public LogLevel Level { get; }
    public string Message { get; }
    public DateTime Time { get; }
    public string TimeText => Time.ToString("HH:mm:ss.fff");

    public override string ToString() => $"{TimeText} [{Level}] {Message}";
}
