using System.IO;
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

        foreach (var dir in Directory.GetDirectories(PluginsDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var plugin = new InstalledPlugin(dir);
            ReadManifest(plugin);
            _plugins.Add(plugin);
        }

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

        if (_plugins.Count == 0)
            Emit(LogLevel.Debug, $"Nenhum plugin em {PluginsDirectory}.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPendingUninstalls()
    {
        foreach (var dir in Directory.GetDirectories(PluginsDirectory))
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
        if (File.Exists(Path.Combine(plugin.Directory, UninstallMarkerFileName)))
        {
            plugin.Status = PluginStatus.PendingUninstall;
            plugin.Detail = "marcado para desinstalar — a pasta some no próximo start";
            return;
        }

        var manifestPath = Path.Combine(plugin.Directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Refuse(plugin, $"sem {ManifestFileName}");
            return;
        }

        PluginManifest manifest;
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
        try
        {
            var assemblyPath = Path.Combine(plugin.Directory, manifest.AssemblyFile);
            // Re-enabling reuses the context: the assembly is already there and can't be loaded twice anyway.
            plugin.LoadContext ??= new PluginLoadContext(assemblyPath, manifest.Id);
            var assembly = plugin.LoadContext.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(manifest.EntryType, throwOnError: false)
                ?? throw new InvalidOperationException($"o tipo '{manifest.EntryType}' não existe em {manifest.AssemblyFile}");
            if (Activator.CreateInstance(type) is not IPixiePlugin instance)
                throw new InvalidOperationException($"'{manifest.EntryType}' não implementa IPixiePlugin");

            var host = new PluginHost(this, manifest, _downloads, Path.Combine(_dataDirectory, manifest.Id));
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
            plugin.Status = PluginStatus.Failed;
            plugin.Detail = ex.Message;
            Emit(LogLevel.Error, $"Plugin '{plugin.Id}' falhou ao carregar: {ex.Message}", ex);
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
        const string detail = "será removido ao reiniciar";
        File.WriteAllText(Path.Combine(plugin.Directory, UninstallMarkerFileName), "");
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
        File.Delete(Path.Combine(plugin.Directory, UninstallMarkerFileName));
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
