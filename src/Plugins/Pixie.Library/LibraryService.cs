using Pixie.Library.Catalog;
using YtDlpCore;

namespace Pixie.Library;

/// <summary>
/// The application layer of the plugin: owns the manifest, the store, the sync runner and the operations; runs
/// the reconciler whenever something it depends on changed (index loaded, scan finished, manifest edited) and
/// executes the actions it proposes — locators healed go to the manifest, scan requests go to the runner,
/// manual relocations stay in the model for the tab to offer. One reconciliation at a time, on the pool; a
/// request during one runs again right after. The model is published through <see cref="ModelChanged"/> on
/// the pool thread.
/// </summary>
internal sealed class LibraryService
{
    private static readonly TimeSpan ScanThrottle = TimeSpan.FromSeconds(5);

    private readonly LibraryManifest _manifest;
    private readonly LibraryStore _store;
    private readonly LibrarySync _sync;
    private readonly LibraryOperations _operations;
    private readonly Func<IFileProbe> _probeFactory;
    private readonly Action<LogLevel, string, Exception?> _log;
    private readonly CancellationToken _shutdown;
    private readonly object _lock = new();
    private bool _reconciling;
    private bool _again;
    private DateTime _lastScanRequest = DateTime.MinValue;
    private LibraryModel? _model;

    public LibraryService(LibraryManifest manifest, LibraryStore store, LibrarySync sync, LibraryOperations operations,
        Func<IFileProbe> probeFactory, Action<LogLevel, string, Exception?> log, CancellationToken shutdown)
    {
        _manifest = manifest;
        _store = store;
        _sync = sync;
        _operations = operations;
        _probeFactory = probeFactory;
        _log = log;
        _shutdown = shutdown;
        _sync.IndexLoaded += _ => RequestReconcile();
        _sync.Completed += _ => RequestReconcile();
        _sync.Faulted += _ => RequestReconcile();
    }

    public LibraryManifest Manifest => _manifest;
    public LibraryStore Store => _store;
    public LibrarySync Sync => _sync;
    public LibraryOperations Operations => _operations;

    /// <summary>The last model built, or null before the index was loaded.</summary>
    public LibraryModel? Model { get { lock (_lock) return _model; } }

    /// <summary>A new model. Pool thread: the view model marshals.</summary>
    public event Action<LibraryModel>? ModelChanged;

    /// <summary>
    /// What happens once per start, before any model: leftovers of imports that did not finish are removed, a
    /// downgrade's new roots are brought in from the 1.x manifest, and the 1.x inbox blocks the 2.x has not
    /// seen are replayed. All of it reads the 1.x files without writing them.
    /// </summary>
    public void Start()
    {
        try
        {
            StorageCopy.CleanupTemporaries(_operations.StorageRoot);
            var v1 = System.IO.File.Exists(_manifest.LegacyPath) ? LegacyMigration.TryReadV1(_manifest.LegacyPath, m => _log(LogLevel.Warning, m, null)) : null;
            var added = LegacyMigration.SyncRootsSnapshot(_manifest, v1, DateTime.UtcNow);
            foreach (var item in added)
                _log(LogLevel.Info, $"pasta \"{item.Source?.Name}\" ({item.Locator.LastKnownPath}) veio do manifest.json (1.x): entrou como fonte", null);
            var replayed = LegacyMigration.TranslateInbox(_manifest, _store.LegacyInboxDirectory, (file, created) => _operations.Deliver(file, null, created), m => _log(LogLevel.Warning, m, null));
            if (replayed > 0)
                _log(LogLevel.Info, $"{replayed} pendência(s) do inbox (1.x) trazidas pro inbox.v2", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log(LogLevel.Warning, $"migração do lado 1.x: {ex.Message}", ex);
        }
    }

    /// <summary>Builds a model soon, on the pool. Coalesces: many requests while one runs become one more run.</summary>
    public void RequestReconcile()
    {
        if (_shutdown.IsCancellationRequested)
            return;
        lock (_lock)
        {
            if (_reconciling)
            {
                _again = true;
                return;
            }
            _reconciling = true;
        }
        Task.Run(ReconcileLoop, CancellationToken.None);
    }

    private void ReconcileLoop()
    {
        while (true)
        {
            try
            {
                ReconcileOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log(LogLevel.Error, $"reconciliação da biblioteca falhou: {ex.Message}", ex);
            }
            lock (_lock)
            {
                if (!_again || _shutdown.IsCancellationRequested)
                {
                    _reconciling = false;
                    _again = false;
                    return;
                }
                _again = false;
            }
        }
    }

    private void ReconcileOnce()
    {
        var index = _sync.Index;
        if (index is null)
            return;   // IndexLoaded will call again
        var snapshot = _manifest.Snapshot();
        var plan = ScanPlan.Build(snapshot, _manifest.MusicFolder);
        var result = Reconciler.Build(new ReconcileInput(snapshot, index, plan), _probeFactory());
        Apply(result.Actions);
        lock (_lock)
            _model = result.Model;
        try { ModelChanged?.Invoke(result.Model); }
        catch (Exception ex) { _log(LogLevel.Warning, $"handler do modelo lançou: {ex.Message}", ex); }
    }

    private void Apply(IReadOnlyList<ReconcileAction> actions)
    {
        var rewrites = actions.OfType<RewriteLocator>().ToList();
        if (rewrites.Count > 0 && !_manifest.ReadOnly)
        {
            _manifest.Update(m =>
            {
                foreach (var rewrite in rewrites)
                {
                    foreach (var item in m.Items.Where(i => i.Id == rewrite.ItemId))
                        item.Locator = rewrite.Locator;
                }
            });
            foreach (var rewrite in rewrites)
                _log(LogLevel.Debug, $"locator atualizado: {rewrite.ItemId} → {rewrite.Locator.LastKnownPath}", null);
        }
        var scan = actions.OfType<RequestScan>().OrderByDescending(s => s.Full).FirstOrDefault();
        if (scan is not null && !_sync.IsRunning)
        {
            var now = DateTime.UtcNow;
            if (scan.Reason == ScanReason.ScanPlanChanged || now - _lastScanRequest >= ScanThrottle)
            {
                _lastScanRequest = now;
                _sync.Request(scan.Full ? SyncMode.Full : SyncMode.Incremental);
            }
        }
    }
}
