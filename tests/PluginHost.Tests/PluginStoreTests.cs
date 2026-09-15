using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PixieDownloader.Plugins;
using PixieDownloader.ViewModels;
using YtDlpCore;

namespace PluginHost.Tests;

/// <summary>
/// The store end to end against a fake release: a folder with the plugins.json and the plugin zip the release
/// script would produce (the zip is built here from the real TrackTracer output, the same way the script does
/// it), served by an <see cref="HttpMessageHandler"/> that reads files instead of the network. What is
/// checked is the contract the app relies on: the catalog is validated, a download is verified against its
/// hash and its contents before it lands, <see cref="PluginCatalog.Install"/> loads it now or parks it for the
/// next start, and the next start applies what was parked.
/// </summary>
public sealed class PluginStoreTests : IDisposable
{
    private static readonly string TrackTracerOutput =
        typeof(PluginStoreTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TrackTracerPluginOutput").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pixie-store-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    private string PluginsDir => Path.Combine(_root, "plugins");
    private string ReleaseDir => Path.Combine(_root, "release");
    private const string CatalogUrl = "https://release.test/latest/download/plugins.json";

    // ───── The catalog ─────

    [Fact]
    public async Task The_catalog_is_read_and_each_entry_is_checked_before_anything_is_shown()
    {
        var zip = PublishTrackTracerZip();
        WriteCatalog(Entry("tracktracer", zip));
        using var store = NewStore();

        var catalog = await store.FetchCatalogAsync(CancellationToken.None);

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal("9.9.9", catalog.App);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("tracktracer", plugin.Id);
        Assert.Equal("TrackTracer", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal(Path.GetFileName(zip), plugin.Asset);
        Assert.Equal(Sha256Of(zip), plugin.Sha256);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "plugins": [] }""", "formato 2")]
    [InlineData("""{ "schemaVersion": 1, "plugins": [ { "id": "../x", "version": "1.0.0", "apiVersion": "1.0", "asset": "a.zip", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ] }""", "id inválido")]
    [InlineData("""{ "schemaVersion": 1, "plugins": [ { "id": "x", "version": "1.0.0", "apiVersion": "1.0", "asset": "a.zip", "sha256": "nope" } ] }""", "SHA256")]
    [InlineData("""{ "schemaVersion": 1, "plugins": [ { "id": "x", "version": "um", "apiVersion": "1.0", "asset": "a.zip", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ] }""", "versão ilegível")]
    [InlineData("""not json""", "ilegível")]
    public async Task A_catalog_this_app_cannot_trust_is_refused_with_the_reason(string json, string reason)
    {
        Directory.CreateDirectory(ReleaseDir);
        File.WriteAllText(Path.Combine(ReleaseDir, "plugins.json"), json);
        using var store = NewStore();

        var ex = await Assert.ThrowsAsync<PluginStoreException>(() => store.FetchCatalogAsync(CancellationToken.None));

        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public async Task A_release_without_a_catalog_says_so_instead_of_failing_on_a_404()
    {
        Directory.CreateDirectory(ReleaseDir);   // nothing in it
        using var store = NewStore();

        var ex = await Assert.ThrowsAsync<PluginStoreException>(() => store.FetchCatalogAsync(CancellationToken.None));

        Assert.Contains("não tem catálogo", ex.Message);
    }

    // ───── Install ─────

    [Fact]
    public async Task Install_downloads_verifies_unpacks_and_the_catalog_loads_it_right_away()
    {
        var zip = PublishTrackTracerZip();
        var entry = Entry("tracktracer", zip);
        WriteCatalog(entry);
        using var store = NewStore();
        var catalog = NewCatalog();
        catalog.Initialize();
        Assert.Empty(catalog.Plugins);
        var progress = new ProgressRecorder();   // synchronous: Progress<T> would post through xunit's context, racing the asserts

        PluginInstallOutcome outcome;
        using (var staged = await store.DownloadAsync(entry, progress, CancellationToken.None))
        {
            Assert.StartsWith(Path.Combine(PluginsDir, PluginStore.StagingPrefix), staged.Folder);   // under plugins/, in a dot-folder
            Assert.Equal("tracktracer", staged.Manifest.Id);
            Assert.Equal("Pixie.TrackTracer.TrackTracerPlugin", staged.Manifest.EntryType);
            outcome = catalog.Install(staged.Folder);
        }

        Assert.Equal(PluginInstallOutcome.Installed, outcome);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("tracktracer", plugin.Id);
        Assert.Equal(PluginStatus.Loaded, plugin.Status);
        Assert.True(File.Exists(Path.Combine(PluginsDir, "tracktracer", "Pixie.TrackTracer.dll")));
        Assert.True(File.Exists(Path.Combine(PluginsDir, "tracktracer", "TagLibSharp.dll")));
        Assert.Empty(Directory.GetDirectories(PluginsDir, PluginStore.StagingPrefix + "*"));   // the staging is gone with the StagedPlugin
        Assert.True(progress.Reports > 0);
        Assert.Equal(100, progress.Last);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_catalog_hash_is_refused_and_leaves_nothing()
    {
        var zip = PublishTrackTracerZip();
        var entry = Entry("tracktracer", zip) with { Sha256 = new string('0', 64) };
        using var store = NewStore();
        Directory.CreateDirectory(PluginsDir);

        var ex = await Assert.ThrowsAsync<PluginStoreException>(() => store.DownloadAsync(entry, null, CancellationToken.None));

        Assert.Contains("hash", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(PluginsDir));   // no staging left behind either
    }

    [Fact]
    public async Task A_zip_that_does_not_open_in_the_plugin_folder_is_refused()
    {
        var zip = PublishTrackTracerZip(folderName: "something-else");
        var entry = Entry("tracktracer", zip);
        using var store = NewStore();
        Directory.CreateDirectory(PluginsDir);

        var ex = await Assert.ThrowsAsync<PluginStoreException>(() => store.DownloadAsync(entry, null, CancellationToken.None));

        Assert.Contains("pasta 'tracktracer'", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(PluginsDir));
    }

    [Fact]
    public async Task A_zip_whose_contents_are_not_a_plugin_is_refused()
    {
        Directory.CreateDirectory(ReleaseDir);
        var zip = Path.Combine(ReleaseDir, "PixieDownloader-plugin-tracktracer-v1.0.0.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry("tracktracer/readme.txt");   // a folder with the right name and nothing that loads
        var entry = Entry("tracktracer", zip);
        using var store = NewStore();
        Directory.CreateDirectory(PluginsDir);

        var ex = await Assert.ThrowsAsync<PluginStoreException>(() => store.DownloadAsync(entry, null, CancellationToken.None));

        Assert.Contains("não é um plugin", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(PluginsDir));
    }

    [Fact]
    public async Task An_entry_for_an_api_this_app_lacks_is_refused_before_any_download()
    {
        var zip = PublishTrackTracerZip();
        var entry = Entry("tracktracer", zip) with { ApiVersion = "9.0" };
        var handler = new FileHandler(ReleaseDir);
        using var store = new PluginStore(new Uri(CatalogUrl), PluginsDir, "PixieDownloader/test", handler);

        var ex = await Assert.ThrowsAsync<PluginStoreException>(() => store.DownloadAsync(entry, null, CancellationToken.None));

        Assert.Contains("atualize o app", ex.Message);
        Assert.Empty(handler.Requested);
    }

    // ───── Update ─────

    [Fact]
    public async Task Updating_a_plugin_that_is_loaded_parks_the_new_version_for_the_next_start()
    {
        var zip = PublishTrackTracerZip();
        var entry = Entry("tracktracer", zip);
        using var store = NewStore();
        var catalog = NewCatalog();
        catalog.Initialize();
        using (var first = await store.DownloadAsync(entry, null, CancellationToken.None))
            catalog.Install(first.Folder);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginStatus.Loaded, plugin.Status);   // its DLL is mapped from here on
        var changes = 0;
        catalog.Changed += (_, _) => changes++;

        PluginInstallOutcome outcome;
        using (var again = await store.DownloadAsync(entry, null, CancellationToken.None))
            outcome = catalog.Install(again.Folder);

        Assert.Equal(PluginInstallOutcome.PendingRestart, outcome);
        Assert.Equal("1.0.0", plugin.PendingUpdateVersion);
        Assert.Equal(1, changes);
        Assert.True(File.Exists(Path.Combine(PluginsDir, PluginCatalog.UpdateFolderPrefix + "tracktracer", "Pixie.TrackTracer.dll")));
        Assert.Equal(PluginStatus.Loaded, plugin.Status);   // untouched until the restart
        catalog.Rescan();
        Assert.Single(catalog.Plugins);                      // the parked copy is never listed as a plugin of its own
        Assert.Equal("1.0.0", catalog.Plugins[0].PendingUpdateVersion);
    }

    [Fact]
    public void A_parked_update_takes_the_plugins_place_at_the_next_start()
    {
        // An old install that this run never loaded (so nothing holds its files), and the update the store parked.
        var current = Path.Combine(PluginsDir, "tracktracer");
        CopyOutput(TrackTracerOutput, current);
        File.WriteAllText(Path.Combine(current, "old.txt"), "");
        var parked = Path.Combine(PluginsDir, PluginCatalog.UpdateFolderPrefix + "tracktracer");
        CopyOutput(TrackTracerOutput, parked);
        File.WriteAllText(Path.Combine(parked, "new.txt"), "");
        var catalog = NewCatalog();

        catalog.Initialize();

        Assert.False(Directory.Exists(parked));
        Assert.True(File.Exists(Path.Combine(current, "new.txt")));
        Assert.False(File.Exists(Path.Combine(current, "old.txt")));
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginStatus.Loaded, plugin.Status);
        Assert.Null(plugin.PendingUpdateVersion);
    }

    [Fact]
    public void Dot_folders_under_plugins_are_the_hosts_own_and_a_staging_left_by_a_crash_is_removed()
    {
        Directory.CreateDirectory(Path.Combine(PluginsDir, PluginStore.StagingPrefix + "abc", "unpacked"));
        Directory.CreateDirectory(Path.Combine(PluginsDir, ".hidden"));
        var catalog = NewCatalog();

        catalog.Initialize();

        Assert.Empty(catalog.Plugins);
        Assert.Empty(Directory.GetDirectories(PluginsDir, PluginStore.StagingPrefix + "*"));
        Assert.True(Directory.Exists(Path.Combine(PluginsDir, ".hidden")));   // not ours to delete, not a plugin either
    }

    // ───── The row ─────

    [Fact]
    public void A_store_row_compares_itself_with_what_is_installed()
    {
        var offered = new StorePluginViewModel(new StorePlugin("x", "X", "1.1.0", "1.0", null, "x.zip", new string('a', 64), null));

        offered.Refresh(null);
        Assert.Equal(StorePluginState.NotInstalled, offered.State);
        Assert.Equal("Instalar", offered.ActionText);

        offered.Refresh(Installed("1.0.0"));
        Assert.Equal(StorePluginState.UpdateAvailable, offered.State);
        Assert.Equal("Atualizar", offered.ActionText);
        Assert.Equal("1.0.0", offered.InstalledVersion);

        offered.Refresh(Installed("1.1.0"));
        Assert.Equal(StorePluginState.Installed, offered.State);
        Assert.False(offered.HasAction);

        offered.Refresh(Installed("2.0.0-dev"));   // newer than the catalog, however it is spelled
        Assert.Equal(StorePluginState.Installed, offered.State);

        offered.Refresh(Installed("1.0.0", pendingUpdate: "1.1.0"));
        Assert.Equal(StorePluginState.PendingRestart, offered.State);
        Assert.False(offered.HasAction);

        var tooNew = new StorePluginViewModel(new StorePlugin("x", "X", "1.1.0", "9.0", null, "x.zip", new string('a', 64), null));
        tooNew.Refresh(null);
        Assert.Equal(StorePluginState.Incompatible, tooNew.State);
        Assert.False(tooNew.HasAction);
        tooNew.Refresh(Installed("1.0.0"));
        Assert.Equal(StorePluginState.Incompatible, tooNew.State);
    }

    // ───── Helpers ─────

    /// <summary>Zips the built TrackTracer as the release script does: the files inside one folder named after the id, no .pdb.</summary>
    private string PublishTrackTracerZip(string folderName = "tracktracer")
    {
        Directory.CreateDirectory(ReleaseDir);
        var stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
        CopyOutput(TrackTracerOutput, Path.Combine(stage, folderName));
        var zip = Path.Combine(ReleaseDir, "PixieDownloader-plugin-tracktracer-v1.0.0.zip");
        ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(stage, recursive: true);
        return zip;
    }

    private static void CopyOutput(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

    private static StorePlugin Entry(string id, string zip) =>
        new(id, "TrackTracer", "1.0.0", "1.1", "Acha a tracklist.", Path.GetFileName(zip), Sha256Of(zip), new FileInfo(zip).Length);

    private void WriteCatalog(params StorePlugin[] plugins)
    {
        Directory.CreateDirectory(ReleaseDir);
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, app = "9.9.9", plugins }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(ReleaseDir, "plugins.json"), json);
    }

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static InstalledPlugin Installed(string version, string? pendingUpdate = null)
    {
        var plugin = InstalledPlugin.InFolder(Path.Combine("plugins", "x"));
        plugin.Manifest = new PixieDownloader.Sdk.PluginManifest { Id = "x", Name = "X", Version = version, ApiVersion = "1.0", AssemblyFile = "x.dll", EntryType = "X.Plugin" };
        plugin.PendingUpdateVersion = pendingUpdate;
        return plugin;
    }

    private PluginStore NewStore() => new(new Uri(CatalogUrl), PluginsDir, "PixieDownloader/test", new FileHandler(ReleaseDir));

    private PluginCatalog NewCatalog()
    {
        var service = new YtDlpService(logger: null, toolsDirectory: Path.Combine(_root, "tools"), cacheDirectory: Path.Combine(_root, "cache"));
        _disposables.Add(service);
        return new PluginCatalog(PluginsDir, Path.Combine(_root, "data"), service, new PluginSettings(), logger: null);
    }

    /// <summary>Records reports on the thread that makes them — the download's last one is 100, before DownloadAsync returns.</summary>
    private sealed class ProgressRecorder : IProgress<double>
    {
        public int Reports { get; private set; }
        public double Last { get; private set; }

        public void Report(double value)
        {
            Reports++;
            Last = value;
        }
    }

    /// <summary>Serves the "release": the file named by the request's last path segment, or 404.</summary>
    private sealed class FileHandler(string directory) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(request.RequestUri!.LocalPath);
            Requested.Add(name);
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) };
            response.Content.Headers.ContentLength = new FileInfo(path).Length;
            return Task.FromResult(response);
        }
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        // A loaded plugin keeps its DLL mapped for the life of the process, so this often can't finish.
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
