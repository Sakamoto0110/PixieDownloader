using YtDlpCore;

namespace Pixie.Library.Catalog;

internal enum SyncMode
{
    /// <summary>Scan what the stamps say changed; with the flag on <c>synced</c> and nothing changed, do nothing.</summary>
    Incremental,

    /// <summary>List every directory again (tags still go by identity). What the button does, and what follows a failure.</summary>
    Full,
}

/// <summary>
/// Runs the state machine: one sync at a time, on the thread pool, with the manifest flag as the contract.
/// A request while a run is going on does not start another — it is queued, and the run goes again when it
/// finishes (with the stronger of the two modes). A flag flip during a scan (a download landing) makes the
/// compare-and-swap at the end fail, and the run goes again from the index it just wrote, incrementally.
/// Each run derives the <see cref="ScanPlan"/> from the manifest as it is then; a tree made with other
/// extensions or scanner rules makes the run a full one (a source that came or went needs no more than the
/// incremental walk: new trees are listed whole, trees without a source are dropped). Every event here is
/// raised on the pool thread; the view model marshals.
/// </summary>
internal sealed class LibrarySync
{
    private readonly LibraryManifest _manifest;
    private readonly LibraryStore _store;
    private readonly Func<ScanPlan> _plan;
    private readonly Func<string, TagInfo?> _readTags;
    private readonly Func<string, FileIdentityInfo> _identity;
    private readonly Action<LogLevel, string, Exception?> _log;
    private readonly CancellationToken _shutdown;
    private readonly object _lock = new();
    private readonly object _loadLock = new();
    private Task? _running;
    private bool _isRunning;
    private SyncMode? _queued;
    private IndexData? _index;

    public LibrarySync(LibraryManifest manifest, LibraryStore store, Func<ScanPlan> plan, Func<string, TagInfo?> readTags,
        Func<string, FileIdentityInfo> identity, Action<LogLevel, string, Exception?> log, CancellationToken shutdown)
    {
        _manifest = manifest;
        _store = store;
        _plan = plan;
        _readTags = readTags;
        _identity = identity;
        _log = log;
        _shutdown = shutdown;
    }

    /// <summary>The identity delegate the app uses: one handle per listed file (see <see cref="FileIdentity"/>).</summary>
    public static FileIdentityInfo WindowsIdentity(string path)
        => FileIdentity.TryGet(path, isFolder: false, out var serial, out var id) ? new FileIdentityInfo(serial, id) : FileIdentityInfo.None;

    /// <summary>The index as the plugin knows it: the file as last read, then each scan's result. Null until loaded.</summary>
    public IndexData? Index { get { lock (_lock) return _index; } }

    public bool IsRunning { get { lock (_lock) return _isRunning; } }

    /// <summary>The previous index came off the disk — show it, the scan comes after.</summary>
    public event Action<IndexData>? IndexLoaded;

    public event Action<ScanProgress>? Progress;

    /// <summary>A scan finished and its index is on disk.</summary>
    public event Action<ScanResult>? Completed;

    /// <summary>A scan stopped short (error; not cancellation). The flag is <c>failed</c>: the next run is full.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Raised when a run starts and when it ends.</summary>
    public event Action? RunStateChanged;

    /// <summary>Reads the index from disk if that has not happened yet. Does not scan.</summary>
    public Task EnsureLoadedAsync() => Task.Run(() => EnsureLoaded(), CancellationToken.None);

    /// <summary>Asks for a sync. Returns the task of the run that will include it — a run already going on, or a new one.</summary>
    public Task Request(SyncMode mode)
    {
        lock (_lock)
        {
            if (_shutdown.IsCancellationRequested)
                return Task.CompletedTask;
            if (_isRunning)
            {
                _queued = _queued is SyncMode.Full ? SyncMode.Full : mode;
                return _running!;
            }
            _queued = null;
            _isRunning = true;
            _running = Task.Run(() => RunLoop(mode), CancellationToken.None);
            return _running;
        }
    }

    private void RunLoop(SyncMode mode)
    {
        RunStateChanged?.Invoke();
        try
        {
            while (true)
            {
                try
                {
                    RunOnce(mode);
                }
                catch (OperationCanceledException)
                {
                    // the plugin was disabled or the app is closing; the flag says failed, the next run is a full one
                }
                catch (Exception ex)
                {
                    _log(LogLevel.Error, $"sincronização abortada: {ex.Message}", ex);
                    try { Faulted?.Invoke(ex); } catch { /* the UI's problem, not the runner's */ }
                }

                lock (_lock)
                {
                    // Nothing touches the flags after this block: a request arriving now starts a run of its own.
                    if (_queued is not { } next || _shutdown.IsCancellationRequested)
                    {
                        _isRunning = false;
                        _queued = null;
                        return;
                    }
                    _queued = null;
                    mode = next;
                }
            }
        }
        finally
        {
            RunStateChanged?.Invoke();
        }
    }

    private void RunOnce(SyncMode mode)
    {
        var index = EnsureLoaded();
        if (_manifest.ReadOnly)
            return;   // a manifest from a newer plugin: nothing is written, so nothing is scanned
        var plan = _plan();
        var status = _manifest.Status;
        var rulesChanged = LibraryScanner.RulesChanged(index, plan);
        var full = mode == SyncMode.Full || status == SyncStatus.Failed || rulesChanged || index.ScannedAt is null;   // a tree that never came from a scan has nothing to reuse
        if (!full && status == SyncStatus.Synced && !LibraryScanner.DirectoriesChanged(index, plan))
            return;
        if (rulesChanged)
            _log(LogLevel.Debug, "as extensões ou as regras da varredura mudaram: varredura completa", null);

        while (true)
        {
            _shutdown.ThrowIfCancellationRequested();
            var generation = _manifest.Generation;
            _manifest.MarkSyncing();
            ScanResult result;
            try
            {
                var blocks = _store.Inbox.ReadAll();
                var priority = blocks.SelectMany(b => b.Files).ToArray();
                var request = new ScanRequest(plan, index, full, priority, _readTags, _identity);
                result = LibraryScanner.Scan(request, new ImmediateProgress(p => Progress?.Invoke(p)), _shutdown);
                _store.WriteIndex(result.Index);
                lock (_lock)
                    _index = result.Index;
                _store.Inbox.Delete(blocks);
            }
            catch
            {
                _manifest.MarkFailed();
                throw;
            }

            try { Completed?.Invoke(result); } catch (Exception ex) { _log(LogLevel.Warning, $"handler de sync lançou: {ex.Message}", ex); }
            if (_manifest.TryMarkSynced(generation, DateTime.UtcNow))
                return;

            // Something flipped the flag while we scanned (a download, an import): once more, from where we are.
            index = result.Index;
            plan = _plan();
            full = LibraryScanner.RulesChanged(index, plan);
        }
    }

    private IndexData EnsureLoaded()
    {
        lock (_loadLock)
        {
            lock (_lock)
            {
                if (_index is { } loaded)
                    return loaded;
            }
            var index = _store.ReadIndex(message => _log(LogLevel.Warning, message, null));
            lock (_lock)
                _index = index;
            try { IndexLoaded?.Invoke(index); } catch (Exception ex) { _log(LogLevel.Warning, $"handler de índice lançou: {ex.Message}", ex); }
            return index;
        }
    }

    /// <summary><see cref="Progress{T}"/> posts to the captured context; this one just calls, on the scanning thread.</summary>
    private sealed class ImmediateProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
