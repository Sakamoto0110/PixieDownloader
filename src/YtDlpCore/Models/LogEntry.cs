namespace YtDlpCore;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Source,                    // "YtDlp" | "Ffmpeg" | "Core" | "Cache"
    string Message,
    string? Url = null,
    Exception? Exception = null)
{
    public static LogEntry Now(LogLevel level, string source, string message, string? url = null, Exception? ex = null)
        => new(DateTimeOffset.Now, level, source, message, url, ex);
}
