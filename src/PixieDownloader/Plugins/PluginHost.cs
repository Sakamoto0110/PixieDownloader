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
        if (IsShutDown)
            return NoRegistration.Instance;   // a disabled plugin registering late: ignored, not an error
        return _catalog.Capabilities.Register(Manifest.Id, id, implementation);
    }

    public bool TryGetCapability(string id, [NotNullWhen(true)] out Delegate? implementation)
        => _catalog.Capabilities.TryGet(id, out implementation);

    /// <summary>Disable or app exit: cancels the token and drops this plugin's registrations. Idempotent.</summary>
    internal void Shutdown()
    {
        if (IsShutDown)
            return;
        _shutdown.Cancel();
        _catalog.Capabilities.RemoveAll(Manifest.Id);
    }

    private sealed class NoRegistration : IDisposable
    {
        public static readonly NoRegistration Instance = new();
        public void Dispose() { }
    }
}
