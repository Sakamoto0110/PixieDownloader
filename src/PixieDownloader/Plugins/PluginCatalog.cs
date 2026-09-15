using System.IO;
using System.Reflection;
using System.Text.Json;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace PixieDownloader.Plugins;

/// <summary>
/// Everything the host does with plugins: finds them (<c>plugins/&lt;id&gt;/plugin.json</c>, read without loading
/// any code), refuses the ones that can't run and says why in the logs, loads the rest in dependency order,
/// and switches them off, on and out again while the app runs. All of it on the UI thread — plugins get their
/// <c>Configure</c> there. Disabling is immediate (tab gone, token cancelled, capabilities dropped); the
/// assembly stays in memory because WPF can't really let go of it (docs/ROADMAP.md). Uninstalling only
/// marks the folder: the file is in use until the process dies, so the next start deletes it.
/// </summary>
public sealed class PluginCatalog
{
    public const string ManifestFileName = "plugin.json";
    public const string UninstallMarkerFileName = ".uninstall";
    /// <summary>
    /// <c>plugins/.~update-&lt;id&gt;/</c>: a newer version the store downloaded while the current one was loaded
    /// (its DLL is mapped until the process ends). The next <see cref="Initialize"/> swaps it in. Any folder
    /// starting with a dot is the host's own and never listed as a plugin.
    /// </summary>
    public const string UpdateFolderPrefix = ".~update-";
    private const string LogSource = "Plugins";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _dataDirectory;
    private readonly IYtDlpService _downloads;
    private readonly PluginSettings _settings;
    private readonly SessionLogger? _logger;
    private readonly List<InstalledPlugin> _plugins = [];

    public PluginCatalog(string pluginsDirectory, string dataDirectory, IYtDlpService downloads, PluginSettings settings, SessionLogger? logger)
    {
        PluginsDirectory = pluginsDirectory;
        _dataDirectory = dataDirectory;
        _downloads = downloads;
        _settings = settings;
        _logger = logger;
    }

    public string PluginsDirectory { get; }

    /// <summary>Every folder under <c>plugins/</c>, in name order, whatever its state.</summary>
    public IReadOnlyList<InstalledPlugin> Plugins => _plugins;

    internal CapabilityRegistry Capabilities { get; } = new();

    /// <summary>Catalog and plugin log lines, same shape as <see cref="IYtDlpService.LogEmitted"/>; the Logs tab listens.</summary>
    public event EventHandler<LogEntry>? LogEmitted;

    /// <summary>A plugin just got loaded and configured — the shell adds its tab if it has one.</summary>
    public event EventHandler<InstalledPlugin>? PluginEnabled;

    /// <summary>A loaded plugin was just shut down — the shell removes its tab.</summary>
    public event EventHandler<InstalledPlugin>? PluginDisabled;

    /// <summary>Any status change, for whatever lists the plugins.</summary>
    public event EventHandler? Changed;

    // ───── Startup ─────

    /// <summary>
    /// Once, at startup: deletes the folders marked on the previous run, reads every manifest, loads what is
    /// enabled and compatible. A plugin loads only after everything it <c>dependsOn</c> did; one whose
    /// dependency is missing, refused, disabled or failed is refused with that reason.
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(PluginsDirectory);
        ApplyPendingUninstalls();
        ApplyStagedUpdates();
        DropStagingLeftovers();

        foreach (var plugin in FindInstalled())
        {
            ReadManifest(plugin);
            _plugins.Add(plugin);
        }
        LoadPending();
        RefreshPendingUpdates();

        if (_plugins.Count == 0)
            Emit(LogLevel.Debug, $"Nenhum plugin em {PluginsDirectory}.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Looks at the folder again while the app runs: a folder dropped in gets read and loaded, a fixed
    /// plugin.json (or a replaced DLL) gets another chance, a folder that is gone leaves the list — unless
    /// its plugin is running, which stays until the app closes. Loaded plugins are never re-read.
    /// </summary>
    public void Rescan()
    {
        Directory.CreateDirectory(PluginsDirectory);
        var found = FindInstalled();

        foreach (var plugin in _plugins.ToList())
        {
            if (found.Any(f => string.Equals(f.Location, plugin.Location, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (plugin.Status == PluginStatus.Loaded)
                plugin.Detail = (plugin.IsLoose ? "o arquivo sumiu" : "a pasta sumiu") + " — continua rodando até o app fechar";
            else
            {
                _plugins.Remove(plugin);
                Emit(LogLevel.Info, $"Plugin '{plugin.Id}' saiu da lista: {plugin.Location} não existe mais.");
            }
        }

        foreach (var candidate in found)
        {
            var plugin = _plugins.FirstOrDefault(p => string.Equals(p.Location, candidate.Location, StringComparison.OrdinalIgnoreCase));
            if (plugin is null)
            {
                plugin = candidate;
                _plugins.Add(plugin);
                Emit(LogLevel.Info, $"Plugin '{plugin.Id}' encontrado em {plugin.Location}.");
            }
            else if (plugin.Status == PluginStatus.Loaded)
                continue;
            ReadManifest(plugin);
        }
        _plugins.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        LoadPending();
        RefreshPendingUpdates();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Loads every <see cref="PluginStatus.Pending"/> plugin, each only after everything it depends on.</summary>
    private void LoadPending()
    {
        var pending = _plugins.Where(p => p.Status == PluginStatus.Pending).ToList();
        bool progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var plugin in pending.ToList())
            {
                var missing = plugin.Manifest!.DependsOn.FirstOrDefault(dep => Find(dep)?.Status != PluginStatus.Loaded);
                if (missing is not null)
                {
                    var dep = Find(missing);
                    if (dep is not null && dep.Status == PluginStatus.Pending)
                        continue;   // its turn comes later in this pass or the next
                    Refuse(plugin, $"depende de '{missing}', que {Describe(dep)}");
                }
                else
                {
                    Load(plugin);
                }
                pending.Remove(plugin);
                progress = true;
            }
        }
        foreach (var plugin in pending)   // a depends on b depends on a
            Refuse(plugin, $"dependência circular: {string.Join(", ", plugin.Manifest!.DependsOn)}");
    }

    /// <summary>
    /// What is under <c>plugins/</c>, in id order: every subfolder (a plugin with dependencies of its own) and
    /// every <c>.dll</c> loose in the root (a plugin that needs nothing else). A loose DLL that is somebody's
    /// dependency rather than a plugin gets refused when read — the hint to give that plugin a folder.
    /// </summary>
    private List<InstalledPlugin> FindInstalled() =>
        PluginFolders().Select(InstalledPlugin.InFolder)
            .Concat(Directory.GetFiles(PluginsDirectory, "*.dll").Select(InstalledPlugin.Loose))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The subfolders that are plugins: a dot-folder (<c>.~update-…</c>, the store's staging) is the host's own.</summary>
    private IEnumerable<string> PluginFolders() =>
        Directory.GetDirectories(PluginsDirectory).Where(d => !Path.GetFileName(d).StartsWith('.'));

    private string UpdateFolder(string id) => Path.Combine(PluginsDirectory, UpdateFolderPrefix + id);

    /// <summary>The version waiting in <c>plugins/.~update-&lt;id&gt;/</c>, read off its DLL; null when nothing waits.</summary>
    private string? StagedUpdateVersion(string id) =>
        Directory.Exists(UpdateFolder(id)) ? PluginManifestReader.TryRead(UpdateFolder(id), out _)?.Version : null;

    private void RefreshPendingUpdates()
    {
        foreach (var plugin in _plugins)
            plugin.PendingUpdateVersion = StagedUpdateVersion(plugin.Id);
    }

    /// <summary>Start of a run: what the store left in <c>.~update-&lt;id&gt;</c> takes the place of <c>&lt;id&gt;</c>, now that nothing holds its files.</summary>
    private void ApplyStagedUpdates()
    {
        foreach (var staged in Directory.GetDirectories(PluginsDirectory, UpdateFolderPrefix + "*"))
        {
            var id = Path.GetFileName(staged)[UpdateFolderPrefix.Length..];
            var target = Path.Combine(PluginsDirectory, id);
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.Move(staged, target);
                Emit(LogLevel.Info, $"Plugin '{id}' atualizado: a versão baixada entrou no lugar.");
            }
            catch (Exception ex)
            {
                // Still staged: the row keeps saying an update waits, and the next start tries again.
                Emit(LogLevel.Warning, $"Não consegui aplicar a atualização do plugin '{id}': {ex.Message}. Fica para o próximo start.", ex);
            }
        }
    }

    /// <summary>A <c>.~store-…</c> folder at startup is a download the previous run never finished.</summary>
    private void DropStagingLeftovers()
    {
        foreach (var staging in Directory.GetDirectories(PluginsDirectory, PluginStore.StagingPrefix + "*"))
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) { Emit(LogLevel.Warning, $"Não consegui apagar {staging}: {ex.Message}", ex); }
        }
    }

    /// <summary>
    /// The last step of a store install: the folder <see cref="PluginStore.DownloadAsync"/> verified and unpacked
    /// (<c>plugins/.~store-…/&lt;id&gt;</c>) becomes <c>plugins/&lt;id&gt;/</c>. Now, when nothing of that id was
    /// loaded this run — a plugin that is present but was never loaded has no file mapped, so its folder can be
    /// replaced and the catalog rescans; a loaded one (disabled included: the assembly stays) keeps its files
    /// until the process ends, so the new version waits in <c>.~update-&lt;id&gt;</c> for the next start.
    /// </summary>
    public PluginInstallOutcome Install(string stagedFolder)
    {
        var id = Path.GetFileName(stagedFolder);
        var existing = Find(id);
        if (existing?.LoadContext is not null)
        {
            var waiting = UpdateFolder(id);
            if (Directory.Exists(waiting))
                Directory.Delete(waiting, recursive: true);
            Directory.Move(stagedFolder, waiting);
            existing.PendingUpdateVersion = StagedUpdateVersion(id);
            Emit(LogLevel.Info, $"Plugin '{id}' v{existing.PendingUpdateVersion} baixado: entra no próximo start (a versão atual está em uso).");
            Changed?.Invoke(this, EventArgs.Empty);
            return PluginInstallOutcome.PendingRestart;
        }

        var target = Path.Combine(PluginsDirectory, id);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(stagedFolder, target);
        Emit(LogLevel.Info, $"Plugin '{id}' instalado em {target}.");
        Rescan();
        return PluginInstallOutcome.Installed;
    }

    /// <summary>The uninstall marker: <c>.uninstall</c> inside a folder plugin, <c>&lt;name&gt;.dll.uninstall</c> next to a loose one.</summary>
    private static string MarkerPath(InstalledPlugin plugin) =>
        plugin.IsLoose ? plugin.LooseAssemblyPath + UninstallMarkerFileName : Path.Combine(plugin.Directory, UninstallMarkerFileName);

    private void ApplyPendingUninstalls()
    {
        foreach (var dir in PluginFolders())
        {
            if (!File.Exists(Path.Combine(dir, UninstallMarkerFileName)))
                continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                Emit(LogLevel.Info, $"Plugin '{Path.GetFileName(dir)}' desinstalado (pasta removida).");
            }
            catch (Exception ex)
            {
                // Still marked: it shows up as PendingUninstall and the next start tries again.
                Emit(LogLevel.Warning, $"Não consegui remover a pasta do plugin '{Path.GetFileName(dir)}': {ex.Message}. Fica para o próximo start.", ex);
            }
        }

        // A loose plugin: the DLL, the build-output siblings a person may have dropped next to it, and the marker.
        foreach (var marker in Directory.GetFiles(PluginsDirectory, "*.dll" + UninstallMarkerFileName))
        {
            var id = Path.GetFileNameWithoutExtension(marker[..^UninstallMarkerFileName.Length]);
            try
            {
                foreach (var suffix in new[] { ".dll", ".pdb", ".deps.json", ".runtimeconfig.json", ".xml", ".dll" + UninstallMarkerFileName })
                    File.Delete(Path.Combine(PluginsDirectory, id + suffix));   // no-op when absent
                Emit(LogLevel.Info, $"Plugin '{id}' desinstalado (arquivo removido).");
            }
            catch (Exception ex)
            {
                Emit(LogLevel.Warning, $"Não consegui remover o plugin '{id}': {ex.Message}. Fica para o próximo start.", ex);
            }
        }
    }

    // ───── Manifest ─────

    /// <summary>
    /// Reads and validates <c>plugin.json</c> without touching the assembly. Leaves the plugin
    /// <see cref="PluginStatus.Pending"/> (ready to load) or <see cref="PluginStatus.Disabled"/>, or refuses it
    /// with the reason.
    /// </summary>
    private void ReadManifest(InstalledPlugin plugin)
    {
        plugin.Manifest = null;
        if (File.Exists(MarkerPath(plugin)))
        {
            plugin.Status = PluginStatus.PendingUninstall;
            plugin.Detail = (plugin.IsLoose ? "o arquivo" : "a pasta") + " não pôde ser removido no último start (em uso?) — tento de novo no próximo";
            return;
        }
        if (_plugins.Any(p => !ReferenceEquals(p, plugin) && string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase) && p.Status != PluginStatus.Refused))
        {
            Refuse(plugin, $"já existe um plugin com o id '{plugin.Id}' ({(plugin.IsLoose ? "uma pasta" : "um .dll solto")} de mesmo nome)");
            return;
        }

        // plugin.json when there is one; otherwise the assembly's own metadata says everything it would.
        // A loose DLL never has a manifest — it is the convention, or a folder.
        PluginManifest manifest;
        var manifestPath = plugin.IsLoose ? null : Path.Combine(plugin.Directory, ManifestFileName);
        if (manifestPath is not null && File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), ManifestJson)
                    ?? throw new JsonException("arquivo vazio");
            }
            catch (JsonException ex)
            {
                Refuse(plugin, $"{ManifestFileName} inválido: {ex.Message}");
                return;
            }
        }
        else if ((plugin.IsLoose
                     ? PluginManifestReader.TryReadAssembly(plugin.LooseAssemblyPath!, plugin.Id, out var problem)
                     : PluginManifestReader.TryRead(plugin.Directory, out problem)) is { } derived)
        {
            manifest = derived;
        }
        else
        {
            Refuse(plugin, problem!);
            return;
        }
        plugin.Manifest = manifest;   // kept even when refused below, so the tab shows the declared name

        if (!string.Equals(manifest.Id, plugin.Id, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(plugin, $"o id '{manifest.Id}' do {ManifestFileName} não bate com a pasta '{plugin.Id}'");
            return;
        }
        if (!SdkVersion.IsCompatible(manifest.ApiVersion))
        {
            Refuse(plugin, $"apiVersion {manifest.ApiVersion} não é compatível com o SDK {SdkVersion.Current} deste app");
            return;
        }
        if (!File.Exists(Path.Combine(plugin.Directory, manifest.AssemblyFile)))
        {
            Refuse(plugin, $"'{manifest.AssemblyFile}' não está na pasta");
            return;
        }

        plugin.Status = IsUserDisabled(plugin.Id) ? PluginStatus.Disabled : PluginStatus.Pending;
        plugin.Detail = null;
    }

    private void Refuse(InstalledPlugin plugin, string reason)
    {
        plugin.Status = PluginStatus.Refused;
        plugin.Detail = reason;
        Emit(LogLevel.Warning, $"Plugin '{plugin.Id}' recusado: {reason}.");
    }

    // ───── Load / unload ─────

    private void Load(InstalledPlugin plugin)
    {
        var manifest = plugin.Manifest!;
        PluginHost? host = null;
        try
        {
            var assemblyPath = plugin.LooseAssemblyPath ?? Path.Combine(plugin.Directory, manifest.AssemblyFile);
            // Re-enabling reuses the context: the assembly is already there and can't be loaded twice anyway.
            plugin.LoadContext ??= new PluginLoadContext(assemblyPath, manifest.Id);
            var assembly = plugin.LoadContext.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(manifest.EntryType, throwOnError: false)
                ?? throw new InvalidOperationException($"o tipo '{manifest.EntryType}' não existe em {manifest.AssemblyFile}");
            if (Activator.CreateInstance(type) is not IPixiePlugin instance)
                throw new InvalidOperationException($"'{manifest.EntryType}' não implementa IPixiePlugin");

            host = new PluginHost(this, manifest, _downloads, Path.Combine(_dataDirectory, manifest.Id));
            instance.Configure(host);

            plugin.Instance = instance;
            plugin.Host = host;
            plugin.Status = PluginStatus.Loaded;
            plugin.Detail = null;
            Emit(LogLevel.Info, $"Plugin '{manifest.Id}' carregado: {manifest.Name} {manifest.Version} (API {manifest.ApiVersion}).");
            PluginEnabled?.Invoke(this, plugin);
        }
        catch (Exception ex)
        {
            // Configure may have registered a capability, asked for comments or started work on the token
            // before it threw. A failed plugin leaves nothing behind — least of all a capability that its own
            // retry (Habilitar) would then collide with.
            host?.Shutdown();
            plugin.Instance = null;
            plugin.Host = null;
            plugin.Status = PluginStatus.Failed;
            // A constructor that throws reaches here wrapped by Activator ("Exception has been thrown by the
            // target of an invocation"); what the row must say is the cause — a missing dependency, say.
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            plugin.Detail = cause.Message;
            Emit(LogLevel.Error, $"Plugin '{plugin.Id}' falhou ao carregar: {cause.Message}", ex);
        }
    }

    private void Unload(InstalledPlugin plugin, PluginStatus newStatus, string? detail)
    {
        plugin.Host?.Shutdown();
        plugin.Instance = null;
        plugin.Host = null;   // the load context stays — see the class summary
        plugin.Status = newStatus;
        plugin.Detail = detail;
        PluginDisabled?.Invoke(this, plugin);
    }

    // ───── User actions ─────

    /// <summary>
    /// Switches a plugin on: forgets the user's "off", re-reads the manifest from disk (a fixed plugin.json
    /// counts) and loads it. Also retries the plugins that were refused only because they depend on this one.
    /// Returns whether it is loaded now.
    /// </summary>
    public bool Enable(string id)
    {
        if (Find(id) is not { } plugin)
            return false;
        SetUserDisabled(plugin.Id, false);
        if (plugin.Status == PluginStatus.Loaded)
            return true;

        var loaded = TryLoadNow(plugin);
        if (loaded)
        {
            foreach (var dependent in _plugins.Where(p => p.Status == PluginStatus.Refused && DependsOn(p, plugin.Id) && !IsUserDisabled(p.Id)))
                TryLoadNow(dependent);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return loaded;
    }

    private bool TryLoadNow(InstalledPlugin plugin)
    {
        ReadManifest(plugin);
        if (plugin.Status != PluginStatus.Pending)
            return false;
        var missing = plugin.Manifest!.DependsOn.FirstOrDefault(dep => Find(dep)?.Status != PluginStatus.Loaded);
        if (missing is not null)
        {
            Refuse(plugin, $"depende de '{missing}', que {Describe(Find(missing))}");
            return false;
        }
        Load(plugin);
        return plugin.Status == PluginStatus.Loaded;
    }

    /// <summary>
    /// Switches a plugin off, now: token cancelled, capabilities dropped, tab removed by the shell. Whatever
    /// depends on it is shut down too (it couldn't load without it on the next start either).
    /// </summary>
    public void Disable(string id)
    {
        if (Find(id) is not { } plugin)
            return;
        SetUserDisabled(plugin.Id, true);
        if (plugin.Status == PluginStatus.Loaded)
        {
            Unload(plugin, PluginStatus.Disabled, null);
            Emit(LogLevel.Info, $"Plugin '{plugin.Id}' desabilitado.");
            foreach (var dependent in _plugins.Where(p => p.Status == PluginStatus.Loaded && DependsOn(p, plugin.Id)).ToList())
            {
                Unload(dependent, PluginStatus.Refused, $"depende de '{plugin.Id}', que foi desabilitado");
                Emit(LogLevel.Info, $"Plugin '{dependent.Id}' desligado junto: depende de '{plugin.Id}'.");
            }
        }
        else if (plugin.Status is PluginStatus.Pending or PluginStatus.Failed)
        {
            plugin.Status = PluginStatus.Disabled;
            plugin.Detail = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shuts the plugin down and marks its folder; the files are in use until the process ends, so the next start removes it.</summary>
    public void Uninstall(string id)
    {
        if (Find(id) is not { } plugin)
            return;
        // The status line already says "será removido ao reiniciar"; the detail adds what that does and doesn't touch.
        var detail = (plugin.IsLoose ? $"o arquivo {Path.GetFileName(plugin.LooseAssemblyPath)}" : $"a pasta plugins\\{plugin.Id}")
                     + $" some no próximo start; o que estiver em data\\{plugin.Id} fica";
        File.WriteAllText(MarkerPath(plugin), "");
        if (plugin.Status == PluginStatus.Loaded)
            Unload(plugin, PluginStatus.PendingUninstall, detail);
        else
        {
            plugin.Status = PluginStatus.PendingUninstall;
            plugin.Detail = detail;
        }
        Emit(LogLevel.Info, $"Plugin '{plugin.Id}' marcado para desinstalar; a pasta some no próximo start.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes <see cref="Uninstall"/> before the restart. The plugin comes back disabled; enabling it is one more click.</summary>
    public void CancelUninstall(string id)
    {
        if (Find(id) is not { Status: PluginStatus.PendingUninstall } plugin)
            return;
        File.Delete(MarkerPath(plugin));
        SetUserDisabled(plugin.Id, true);
        plugin.Status = PluginStatus.Disabled;
        plugin.Detail = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>App exit: every plugin's token is cancelled. No events — nothing is listening any more.</summary>
    public void ShutdownAll()
    {
        foreach (var plugin in _plugins.Where(p => p.Status == PluginStatus.Loaded))
            plugin.Host?.Shutdown();
    }

    // ───── What the app tells the plugins (API 1.1) ─────
    // The view model calls these at the two moments a plugin can act on: an analysis just finished, a
    // download just delivered its file. Each loaded plugin gets its own event through its PluginHost, which
    // also isolates a throwing handler. All on the UI thread.

    /// <summary>True while any loaded plugin asked for comments: the app's single-video analyses then pass <c>fetchComments</c>.</summary>
    public bool WantsAnalysisComments => _plugins.Any(p => p.Status == PluginStatus.Loaded && p.Host?.WantsAnalysisComments == true);

    public void RaiseAnalysisCompleted(string url, UrlInfo info)
    {
        var e = new AnalysisCompletedEventArgs(url, info);
        foreach (var plugin in _plugins.Where(p => p.Status == PluginStatus.Loaded).ToList())
            plugin.Host?.RaiseAnalysisCompleted(e);
    }

    public void RaiseDownloadCompleted(DownloadRequest request, DownloadResult result)
    {
        var e = new DownloadCompletedEventArgs(request, result);
        foreach (var plugin in _plugins.Where(p => p.Status == PluginStatus.Loaded).ToList())
            plugin.Host?.RaiseDownloadCompleted(e);
    }

    // ───── Helpers ─────

    public InstalledPlugin? Find(string id) =>
        _plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool DependsOn(InstalledPlugin plugin, string id) =>
        plugin.Manifest?.DependsOn.Contains(id, StringComparer.OrdinalIgnoreCase) == true;

    private bool IsUserDisabled(string id) =>
        _settings.DisabledIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    private void SetUserDisabled(string id, bool disabled)
    {
        for (int i = _settings.DisabledIds.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_settings.DisabledIds[i], id, StringComparison.OrdinalIgnoreCase))
                _settings.DisabledIds.RemoveAt(i);
        }
        if (disabled)
            _settings.DisabledIds.Add(id);
    }

    private static string Describe(InstalledPlugin? dep) => dep?.Status switch
    {
        null => "não está instalado",
        PluginStatus.Disabled => "está desabilitado",
        PluginStatus.Failed => "falhou ao carregar",
        PluginStatus.PendingUninstall => "está marcado para desinstalar",
        _ => "foi recusado",
    };

    /// <summary>Catalog and plugin lines: to the session log on disk, if any, and to whoever listens (the Logs tab).</summary>
    internal void Emit(LogEntry entry)
    {
        _logger?.Log(entry);
        LogEmitted?.Invoke(this, entry);
    }

    private void Emit(LogLevel level, string message, Exception? ex = null) =>
        Emit(LogEntry.Now(level, LogSource, message, ex: ex));
}
