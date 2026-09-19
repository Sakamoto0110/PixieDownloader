using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Pixie.Library.Catalog;
using Pixie.Library.Player;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Library;

/// <summary>
/// Module 2 of the roadmap — Library, the catalogue of what is already on disk. Named folders point at real
/// ones; an index under <c>data/library/</c> remembers every audio/video file beneath them with the tags read
/// once; the "Biblioteca" tab searches and sorts it and opens a file in the player the user chose (or whatever
/// Windows would). Every delivered download joins the catalogue by itself: its output folder becomes a root if
/// none contains it yet (unless the user turned that off), a block goes to the inbox, the flag flips to
/// pending and an incremental sync runs. No player of its own, no knowledge of any other plugin.
/// </summary>
public sealed class LibraryPlugin : IPixiePlugin, IUiContribution
{
    private IPluginHost _host = null!;
    private LibraryStore _store = null!;
    private LibraryManifest _manifest = null!;
    private LibrarySync _sync = null!;
    private LibraryTabViewModel _tab = null!;
    private HttpClient? _http;

    public void Configure(IPluginHost host)
    {
        _host = host;
        _store = new LibraryStore(host.DataDirectory);
        _manifest = LibraryManifest.Load(_store.ManifestPath, message => host.Log(LogLevel.Warning, message));
        _sync = new LibrarySync(_manifest, _store, TagReader.Read, (level, message, ex) => host.Log(level, message, ex), host.ShutdownToken);
        // The VLC download: its own client (the host's is not part of the SDK), into tools\ next to the exe —
        // the folder the app keeps yt-dlp and ffmpeg in, computed the way BinaryManager computes it.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"PixieDownloader-Library/{typeof(LibraryPlugin).Assembly.GetName().Version?.ToString(3) ?? "dev"}");
        var vlc = new VlcInstaller(_http, Path.Combine(AppContext.BaseDirectory, "tools"), (level, message) => host.Log(level, message));
        _tab = new LibraryTabViewModel(_manifest, _sync, host, vlc);
        host.DownloadCompleted += OnDownloadCompleted;
        host.Log(LogLevel.Debug, $"{_manifest.Roots.Count} pasta(s) no catálogo, estado {_manifest.Status}");
    }

    public string TabHeader => "Biblioteca";

    public FrameworkElement CreateView() => new LibraryView { DataContext = _tab };

    private void OnDownloadCompleted(object? sender, DownloadCompletedEventArgs e)
    {
        var path = e.Result.OutputFilePath;
        if (string.IsNullOrEmpty(path))
            return;
        if (!_manifest.Extensions.Contains(Path.GetExtension(path)))
            return;   // a GIF, say, with GIFs not in the catalogue

        if (_manifest.RootOf(path) is null)
        {
            if (!_manifest.AutoAddRoots)
            {
                _host.Log(LogLevel.Debug, $"{Path.GetFileName(path)} ficou fora do catálogo (pasta não cadastrada)");
                return;
            }
            // The app's output folder is the root the user would add by hand; the file's own folder if the
            // request did not say (or said something the file is not under — the template may have moved it).
            var folder = e.Request.OutputDirectory;
            if (string.IsNullOrWhiteSpace(folder) || !PathUtil.IsUnder(path, folder))
                folder = Path.GetDirectoryName(path)!;
            folder = PathUtil.Normalize(folder);
            var name = LibraryTabViewModel.RootNameFor(folder);
            var taken = new HashSet<string>(_manifest.Roots.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            for (var i = 2; taken.Contains(name); i++)
                name = $"{LibraryTabViewModel.RootNameFor(folder)} ({i})";
            _manifest.Update(m => m.Roots.Add(new LibraryRoot(name, folder)));
            _host.Log(LogLevel.Info, $"pasta \"{name}\" ({folder}) entrou no catálogo por causa de um download");
            _tab.RootsChanged();
        }

        _store.Inbox.Append(path);
        _manifest.MarkPending();
        _sync.Request(SyncMode.Incremental);
    }
}
