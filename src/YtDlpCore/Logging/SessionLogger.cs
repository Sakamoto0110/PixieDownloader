using System.Text.Json;

namespace YtDlpCore;

/// <summary>
/// Per-session dual logger. Writes a human-readable <c>.log</c> (one line per event, flushed
/// immediately) and a structured <c>.json</c> array (throttled rewrite). Thread-safe; intended
/// to be called from background threads only.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private readonly StreamWriter _logWriter;
    private DateTime _lastJsonFlushUtc = DateTime.MinValue;
    private bool _disposed;

    public string LogDirectory { get; }
    public string LogFilePath { get; }
    public string JsonFilePath { get; }

    /// <summary>Raised for every entry (after it is persisted) so the UI can mirror it live.</summary>
    public event EventHandler<LogEntry>? EntryLogged;

    public SessionLogger(string? logDirectory = null)
    {
        LogDirectory = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(LogDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        LogFilePath = Path.Combine(LogDirectory, $"session-{stamp}.log");
        JsonFilePath = Path.Combine(LogDirectory, $"session-{stamp}.json");

        _logWriter = new StreamWriter(
            new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    public void Log(LogEntry entry)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _entries.Add(entry);
            _logWriter.WriteLine(Format(entry));
            MaybeFlushJson_NoLock(force: entry.Level == LogLevel.Error);
        }

        EntryLogged?.Invoke(this, entry);
    }

    private void MaybeFlushJson_NoLock(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastJsonFlushUtc).TotalMilliseconds < 500)
            return;

        FlushJson_NoLock();
        _lastJsonFlushUtc = now;
    }

    private void FlushJson_NoLock()
    {
        try
        {
            var dtos = _entries.ConvertAll(ToDto);
            File.WriteAllText(JsonFilePath, JsonSerializer.Serialize(dtos, JsonOptions));
        }
        catch { /* never let logging crash the app */ }
    }

    private static string Format(LogEntry e)
    {
        var ts = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var level = e.Level.ToString().ToUpperInvariant().PadRight(7);
        var url = string.IsNullOrEmpty(e.Url) ? "" : $" <{e.Url}>";
        var ex = e.Exception is null ? "" : $"{Environment.NewLine}    {e.Exception}";
        return $"{ts} [{level}] {e.Source}: {e.Message}{url}{ex}";
    }

    private static LogEntryDto ToDto(LogEntry e) => new(
        e.Timestamp.ToString("o"),
        e.Level.ToString(),
        e.Source,
        e.Message,
        e.Url,
        e.Exception?.ToString());

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            FlushJson_NoLock();
            _logWriter.Dispose();
        }
    }

    private sealed record LogEntryDto(
        string Timestamp,
        string Level,
        string Source,
        string Message,
        string? Url,
        string? Exception);
}
