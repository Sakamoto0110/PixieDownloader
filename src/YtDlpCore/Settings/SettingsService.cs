using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpCore;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON. Any property change anywhere in the tree
/// schedules a debounced (500ms) save. On save, the existing file is copied to
/// <c>settings.json.bak</c> first; on load, a corrupt primary falls back to the backup, then to defaults.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;
    private readonly string _backupPath;
    private readonly Action<LogEntry>? _log;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _debounceGate = new();
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public AppSettings Current { get; private set; } = new();

    public SettingsService(string? settingsPath = null, Action<LogEntry>? log = null)
    {
        _path = settingsPath ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
        _backupPath = _path + ".bak";
        _log = log;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Current = await TryLoadAsync(_path, ct).ConfigureAwait(false)
                  ?? await TryLoadAsync(_backupPath, ct).ConfigureAwait(false)
                  ?? new AppSettings();
        WireChangeTracking();
    }

    private async Task<AppSettings?> TryLoadAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(fs, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke(LogEntry.Now(LogLevel.Warning, "Core", $"Falha ao carregar {Path.GetFileName(path)}: {ex.Message}"));
            return null;
        }
    }

    /// <summary>Debounced save — coalesces a burst of changes into a single write 500ms later.</summary>
    public void ScheduleSave()
    {
        lock (_debounceGate)
        {
            if (_disposed)
                return;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    await SaveAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);

            try
            {
                if (File.Exists(_path))
                    File.Copy(_path, _backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _log?.Invoke(LogEntry.Now(LogLevel.Warning, "Core", $"Falha ao criar backup das settings: {ex.Message}"));
            }

            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke(LogEntry.Now(LogLevel.Error, "Core", $"Falha ao salvar settings: {ex.Message}", ex: ex));
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>Pushes a URL to the front of the recent list (deduped, capped at 10).</summary>
    public void AddRecentUrl(string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        var list = Current.RecentUrls;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i].Url, url, StringComparison.OrdinalIgnoreCase))
                list.RemoveAt(i);
        }

        list.Insert(0, new RecentUrl { Url = url, Title = title, LastUsed = DateTimeOffset.Now });

        while (list.Count > 10)
            list.RemoveAt(list.Count - 1);
    }

    private void WireChangeTracking()
    {
        void Sub(INotifyPropertyChanged o) => o.PropertyChanged += OnAnyChanged;

        Sub(Current);
        Sub(Current.Ui);
        Sub(Current.Ui.LastWindowSize);
        Sub(Current.Paths);
        Sub(Current.Audio);
        Sub(Current.Video);
        Sub(Current.Advanced);
        Sub(Current.Tools);
        Current.RecentUrls.CollectionChanged += (_, _) => ScheduleSave();
    }

    private void OnAnyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    public void Dispose()
    {
        lock (_debounceGate)
        {
            _disposed = true;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
        _ioGate.Dispose();
    }
}
