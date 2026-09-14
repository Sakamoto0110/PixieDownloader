using PixieDownloader.Plugins;
using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>
/// One row of the Plugins tab: what an <see cref="InstalledPlugin"/> looks like to a person — status in words
/// and a colour, the reason when there is one, and which of the actions apply. The catalog mutates the plugin
/// in place and raises <c>Changed</c>; <see cref="MainViewModel"/> rebuilds the rows from that.
/// </summary>
public sealed class PluginItemViewModel : ObservableObject
{
    public PluginItemViewModel(InstalledPlugin plugin)
    {
        Plugin = plugin;
    }

    public InstalledPlugin Plugin { get; }

    public string Id => Plugin.Id;
    public string Name => Plugin.Name;
    public string Directory => Plugin.Directory;

    /// <summary>"v0.1.0 · API 1.0", or the folder name when there is no readable manifest.</summary>
    public string VersionLabel => Plugin.Manifest is { } m ? $"v{m.Version} · API {m.ApiVersion}" : $"pasta {Plugin.Id}";

    public bool HasUi => Plugin.HasUi;

    public string? Detail => Plugin.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Plugin.Detail);

    public string StatusText => Plugin.Status switch
    {
        PluginStatus.Loaded => Plugin.HasUi ? "Ativo — com aba" : "Ativo",
        PluginStatus.Disabled => "Desabilitado",
        PluginStatus.Refused => "Recusado",
        PluginStatus.Failed => "Falhou ao carregar",
        PluginStatus.PendingUninstall => "Será removido ao reiniciar",
        _ => "Carregando…",
    };

    /// <summary>Resource key of the brush for the status text and the row's left edge.</summary>
    public string StatusBrushKey => Plugin.Status switch
    {
        PluginStatus.Loaded => "Brush.Success",
        PluginStatus.Disabled => "Brush.Text.Tertiary",
        PluginStatus.Failed => "Brush.Error",
        _ => "Brush.Warning",
    };

    // Enable also retries a refused or failed plugin: it re-reads the manifest, so a fixed plugin.json counts.
    public bool CanEnable => Plugin.Status is PluginStatus.Disabled or PluginStatus.Refused or PluginStatus.Failed;
    public bool CanDisable => Plugin.Status == PluginStatus.Loaded;
    public bool CanUninstall => Plugin.Status != PluginStatus.PendingUninstall;
    public bool CanCancelUninstall => Plugin.Status == PluginStatus.PendingUninstall;
}
