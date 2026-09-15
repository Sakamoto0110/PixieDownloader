using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PixieDownloader.Sdk;

namespace PixieDownloader.Plugins;

/// <summary>
/// The official plugins, as the latest GitHub Release describes them. <c>plugins.json</c> is an asset the
/// release script writes next to the plugin zips — id, name, version, apiVersion, description, asset name,
/// sha256 — and the app reads it from <c>releases/latest/download/</c>: no GitHub API, no rate limit, and the
/// release that holds the zips is the one describing them, so the two can't drift. Installing one downloads
/// the zip into a staging dot-folder under <c>plugins/</c> (never listed as a plugin), checks the hash, unpacks,
/// reads the manifest off the DLL's metadata the way the catalog would and refuses an incompatible apiVersion
/// — all before anything reaches <c>plugins/&lt;id&gt;/</c>, which is <see cref="PluginCatalog.Install"/>'s step.
/// The catalog URL is a setting, and asset names resolve relative to it, so a private catalog works the same way.
/// </summary>
public sealed class PluginStore : IDisposable
{
    public const string DefaultCatalogUrl = AppInfo.RepositoryUrl + "/releases/latest/download/plugins.json";

    /// <summary><c>plugins/.~store-&lt;guid&gt;/</c>: one per download, gone when the install is over (or at the next start, after a crash).</summary>
    public const string StagingPrefix = ".~store-";

    private const int SupportedSchema = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // The id becomes a folder name under plugins/: what the release script produces and nothing that could walk.
    private static readonly Regex SafeId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _pluginsDirectory;

    /// <param name="handler">The tests hand in one that serves files from disk; the app leaves it null.</param>
    public PluginStore(Uri catalogUrl, string pluginsDirectory, string userAgent, HttpMessageHandler? handler = null)
    {
        CatalogUrl = catalogUrl;
        _pluginsDirectory = pluginsDirectory;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public Uri CatalogUrl { get; }

    /// <summary>The app's default, or what <c>Settings.Plugins.CatalogUrl</c> says when it is an http(s) URL.</summary>
    public static Uri ResolveCatalogUrl(string? configured) =>
        Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var custom) && custom.Scheme is "http" or "https"
            ? custom
            : new Uri(DefaultCatalogUrl);

    /// <summary>Fetches and validates the catalog. <see cref="PluginStoreException"/> says, in words, why it can't be used.</summary>
    public async Task<StoreCatalog> FetchCatalogAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PluginStoreException("a última release não tem catálogo de plugins (plugins.json)");
        response.EnsureSuccessStatusCode();

        StoreCatalog? catalog;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            try
            {
                catalog = await JsonSerializer.DeserializeAsync<StoreCatalog>(stream, Json, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new PluginStoreException($"catálogo ilegível: {ex.Message}");
            }
        }
        if (catalog is null)
            throw new PluginStoreException("catálogo vazio");
        if (catalog.SchemaVersion != SupportedSchema)
            throw new PluginStoreException($"catálogo no formato {catalog.SchemaVersion}; este app entende o {SupportedSchema} — atualize o app");

        foreach (var plugin in catalog.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Id) || !SafeId.IsMatch(plugin.Id))
                throw new PluginStoreException($"catálogo com um id inválido: '{plugin.Id}'");
            if (string.IsNullOrWhiteSpace(plugin.Asset) || plugin.Asset.Contains('/') || plugin.Asset.Contains('\\'))
                throw new PluginStoreException($"catálogo sem o nome do arquivo do plugin '{plugin.Id}'");
            if (string.IsNullOrWhiteSpace(plugin.Sha256) || !Sha256Hex.IsMatch(plugin.Sha256))
                throw new PluginStoreException($"catálogo sem o SHA256 do plugin '{plugin.Id}'");
            if (!Version.TryParse(plugin.Version, out _))
                throw new PluginStoreException($"catálogo com uma versão ilegível para '{plugin.Id}': '{plugin.Version}'");
        }
        return catalog;
    }

    /// <summary>
    /// Downloads one plugin's zip, verifies it against the catalog's hash, unpacks it into a staging folder and
    /// checks that what came out is a plugin this app can load. The result's <see cref="StagedPlugin.Folder"/>
    /// goes to <see cref="PluginCatalog.Install"/>; disposing the result removes whatever is left of the
    /// staging. Anything wrong is a <see cref="PluginStoreException"/> (or an <see cref="HttpRequestException"/>
    /// for the network), with nothing left behind.
    /// </summary>
    public async Task<StagedPlugin> DownloadAsync(StorePlugin plugin, IProgress<double>? progress, CancellationToken ct)
    {
        if (!SdkVersion.IsCompatible(plugin.ApiVersion))
            throw new PluginStoreException($"precisa da API {plugin.ApiVersion} e este app tem a {SdkVersion.Current} — atualize o app");

        var staging = Path.Combine(_pluginsDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, "plugin.zip");
            await DownloadFileAsync(new Uri(CatalogUrl, plugin.Asset), zip, progress, ct).ConfigureAwait(false);

            var hash = await Sha256Async(zip, ct).ConfigureAwait(false);
            if (!hash.Equals(plugin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PluginStoreException($"o arquivo baixado não bate com o hash do catálogo ({Short(hash)} em vez de {Short(plugin.Sha256)})");

            // ExtractToDirectory refuses entries that would land outside the folder.
            var unpacked = Path.Combine(staging, "unpacked");
            ZipFile.ExtractToDirectory(zip, unpacked);
            var folders = Directory.GetDirectories(unpacked);
            if (Directory.GetFiles(unpacked).Length > 0 || folders.Length != 1
                || !string.Equals(Path.GetFileName(folders[0]), plugin.Id, StringComparison.OrdinalIgnoreCase))
                throw new PluginStoreException($"o zip deveria abrir numa única pasta '{plugin.Id}'");

            var folder = folders[0];
            var manifest = PluginManifestReader.TryRead(folder, out var problem)
                ?? throw new PluginStoreException($"o conteúdo não é um plugin que este app carregue: {problem}");
            if (!SdkVersion.IsCompatible(manifest.ApiVersion))
                throw new PluginStoreException($"o plugin foi feito para a API {manifest.ApiVersion} e este app tem a {SdkVersion.Current} — atualize o app");

            return new StagedPlugin(staging, folder, manifest);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private async Task DownloadFileAsync(Uri url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PluginStoreException($"a release não tem o arquivo {Path.GetFileName(url.LocalPath)}");
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total is > 0)
                progress?.Report(done * 100.0 / total.Value);
        }
        progress?.Report(100);
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] + "…" : hash;

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    internal static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for DropStagingLeftovers at the next start.
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>What the catalog says about the plugins of a release. Shape: see <c>scripts/release.ps1</c>, which writes it.</summary>
public sealed record StoreCatalog(int SchemaVersion, string? App, IReadOnlyList<StorePlugin> Plugins)
{
    public IReadOnlyList<StorePlugin> Plugins { get; init; } = Plugins ?? [];
}

/// <summary>One plugin in the catalog: enough to show it, decide whether it fits this app, and verify the download.</summary>
public sealed record StorePlugin(string Id, string Name, string Version, string ApiVersion, string? Description, string Asset, string Sha256, long? Size);

/// <summary>A downloaded, verified, unpacked plugin waiting in <c>plugins/.~store-…/&lt;id&gt;</c> for <see cref="PluginCatalog.Install"/>.</summary>
public sealed class StagedPlugin(string staging, string folder, PluginManifest manifest) : IDisposable
{
    /// <summary>The plugin's folder, named after its id, ready to be moved into <c>plugins/</c>.</summary>
    public string Folder { get; } = folder;

    /// <summary>What the DLL's metadata says — the same the catalog would read once the folder is in place.</summary>
    public PluginManifest Manifest { get; } = manifest;

    /// <summary>Removes the staging folder and whatever is still in it (nothing, after a successful install).</summary>
    public void Dispose() => PluginStore.TryDelete(staging);
}

/// <summary>Something the store can explain to the user: bad catalog, hash mismatch, wrong contents, incompatible API.</summary>
public sealed class PluginStoreException(string message) : Exception(message);
