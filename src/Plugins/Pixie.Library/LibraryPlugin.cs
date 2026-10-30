using System.IO;
using System.Net.Http;
using System.Windows;
using Pixie.Library.Catalog;
using Pixie.Library.Player;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Library;

/// <summary>
/// Module 2 of the roadmap — Library, the catalogue of what is already on disk: the storage root (the Music
/// folder unless the person chose another) and the folders and files added as sources are scanned into an
/// index tree; the manifest keeps what the disk does not know — the sources, the groups, which items belong
/// to which group; a reconciler joins the two with the file system into what the "Biblioteca" tab shows.
/// Every delivered download joins the catalogue by itself: under a source it is indexed; outside every source
/// its output folder becomes a source (unless the person turned that off). No player of its own (opens in the
/// player chosen, or the VLC it can fetch), no knowledge of any other plugin.
/// </summary>
public sealed class LibraryPlugin : IPixiePlugin, IUiContribution
{
    private IPluginHost _host = null!;
    private LibraryService _service = null!;
    private LibraryTabViewModel _tab = null!;
    private HttpClient? _http;

    public void Configure(IPluginHost host)
    {
        _host = host;
        void Log(LogLevel level, string message, Exception? exception = null) => host.Log(level, message, exception);

        var store = new LibraryStore(host.DataDirectory);
        var manifest = LibraryManifest.Load(host.DataDirectory, MusicFolder(), message => host.Log(LogLevel.Warning, message));
        var operations = new LibraryOperations(manifest, store, new WindowsFileProbe(), (level, message) => host.Log(level, message));
        var sync = new LibrarySync(manifest, store, () => ScanPlan.Build(manifest.Snapshot(), manifest.MusicFolder), TagReader.Read, LibrarySync.WindowsIdentity, Log, host.ShutdownToken);
        _service = new LibraryService(manifest, store, sync, operations, () => new WindowsFileProbe(), Log, host.ShutdownToken);
        _service.Start();

        // The VLC download: its own client (the host's is not part of the SDK), into tools\ next to the exe —
        // the folder the app keeps yt-dlp and ffmpeg in, computed the way BinaryManager computes it.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PixieDownloader-Library/{typeof(LibraryPlugin).Assembly.GetName().Version?.ToString(3) ?? "dev"}");
        var vlc = new VlcInstaller(_http, Path.Combine(AppContext.BaseDirectory, "tools"), (level, message) => host.Log(level, message));

        _tab = new LibraryTabViewModel(_service, host, vlc);
        host.DownloadCompleted += OnDownloadCompleted;
        var snapshot = manifest.Snapshot();
        host.Log(LogLevel.Debug, $"biblioteca em {manifest.StorageRoot}: {snapshot.Sources.Count()} fonte(s), {snapshot.Groups.Count} grupo(s), estado {snapshot.Status}{(snapshot.ReadOnly ? ", só leitura" : "")}");
    }

    /// <summary>The system's Music folder — the storage root unless the person chose another. Never a literal path with a user name in it.</summary>
    private static string MusicFolder()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(music))
            music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
        return music;
    }

    public string TabHeader => "Biblioteca";

    public FrameworkElement CreateView() => new LibraryView { DataContext = _tab };

    private void OnDownloadCompleted(object? sender, DownloadCompletedEventArgs e)
    {
        var path = e.Result.OutputFilePath;
        if (string.IsNullOrEmpty(path))
            return;
        if (_service.Operations.Deliver(path, e.Request.OutputDirectory))
            _service.Sync.Request(SyncMode.Incremental);
        _service.RequestReconcile();
    }
}
