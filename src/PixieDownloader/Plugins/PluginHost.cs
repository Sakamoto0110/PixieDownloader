using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace PixieDownloader.Plugins;

/// <summary>
/// The <see cref="IPluginHost"/> one plugin gets: its manifest, the app's download service, a data folder of
/// its own, a token that dies with it, a log channel tagged with its id, and the capability registry the
/// host shares between plugins. <see cref="Shutdown"/> is what "disable" means from the plugin's side.
/// </summary>
internal sealed class PluginHost : IPluginHost
{
    private readonly PluginCatalog _catalog;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _dataDirectory;

    public PluginHost(PluginCatalog catalog, PluginManifest manifest, IYtDlpService downloads, string dataDirectory)
    {
        _catalog = catalog;
        Manifest = manifest;
        Downloads = downloads;
        _dataDirectory = dataDirectory;
    }

    public PluginManifest Manifest { get; }

    public IYtDlpService Downloads { get; }

    // Created on first use, not on load: a plugin that never stores anything never gets a folder.
    public string DataDirectory
    {
        get
        {
            Directory.CreateDirectory(_dataDirectory);
            return _dataDirectory;
        }
    }

    public CancellationToken ShutdownToken => _shutdown.Token;

    public bool IsShutDown => _shutdown.IsCancellationRequested;

    public void Log(LogLevel level, string message, Exception? exception = null)
        => _catalog.Emit(LogEntry.Now(level, "Plugin:" + Manifest.Id, message, ex: exception));

    public IDisposable RegisterCapability(string id, Delegate implementation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(implementation);
        // A disabled plugin registering late is ignored, not an error — the registry decides that under its own
        // lock, so a registration from a background thread can't slip in behind Shutdown's RemoveAll.
        return _catalog.Capabilities.Register(this, id, implementation);
    }

    public bool TryGetCapability(string id, [NotNullWhen(true)] out Delegate? implementation)
        => _catalog.Capabilities.TryGet(id, out implementation);

    // ───── Since API 1.1 ─────

    public event EventHandler<AnalysisCompletedEventArgs>? AnalysisCompleted;

    public event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;

    private int _commentRequests;

    /// <summary>True while at least one <see cref="RequireAnalysisComments"/> registration is alive.</summary>
    internal bool WantsAnalysisComments => !IsShutDown && Volatile.Read(ref _commentRequests) > 0;

    public IDisposable RequireAnalysisComments()
    {
        if (IsShutDown)
            return NoRegistration.Instance;
        Interlocked.Increment(ref _commentRequests);
        return new CommentRequest(this);
    }

    // The catalog fans these out to every loaded plugin; a handler that throws is logged under the plugin's
    // id and never reaches the others (or the host).
    internal void RaiseAnalysisCompleted(AnalysisCompletedEventArgs e) => Raise(AnalysisCompleted, e, "AnalysisCompleted");

    internal void RaiseDownloadCompleted(DownloadCompletedEventArgs e) => Raise(DownloadCompleted, e, "DownloadCompleted");

    // Handlers run on the UI thread, by contract, and are expected to hand real work to a task. One that
    // doesn't freezes the whole app for as long as it takes — so it gets named in the logs.
    private static readonly TimeSpan SlowHandler = TimeSpan.FromMilliseconds(250);

    private void Raise<T>(EventHandler<T>? handler, T e, string name)
    {
        if (handler is null || IsShutDown)
            return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            handler(this, e);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"handler de {name} falhou: {ex.Message}", ex);
        }
        var took = Stopwatch.GetElapsedTime(started);
        if (took > SlowHandler)
            Log(LogLevel.Warning, $"handler de {name} segurou a UI por {took.TotalMilliseconds:F0} ms — trabalho demorado vai numa Task");
    }

    /// <summary>Disable, a failed Configure or app exit: cancels the token and drops this plugin's registrations. Idempotent.</summary>
    internal void Shutdown()
    {
        if (IsShutDown)
            return;
        try
        {
            _shutdown.Cancel();   // runs whatever the plugin hung on ShutdownToken.Register, right here
        }
        catch (AggregateException ex)
        {
            // A callback that throws is the plugin's bug, not a reason for Desabilitar or the app's exit to crash.
            foreach (var inner in ex.InnerExceptions)
                Log(LogLevel.Error, $"callback de ShutdownToken falhou: {inner.Message}", inner);
        }
        _catalog.Capabilities.RemoveAll(this);
        AnalysisCompleted = null;
        DownloadCompleted = null;
        Volatile.Write(ref _commentRequests, 0);
    }

    private sealed class CommentRequest(PluginHost host) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && !host.IsShutDown)
                Interlocked.Decrement(ref host._commentRequests);
        }
    }
}
