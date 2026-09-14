using System.IO;
using PixieDownloader.Sdk;

namespace PixieDownloader.Plugins;

public enum PluginStatus
{
    /// <summary>Manifest read and accepted, not loaded yet — only ever seen while <see cref="PluginCatalog.Initialize"/> runs.</summary>
    Pending,
    /// <summary>Won't load and the reason is in <see cref="InstalledPlugin.Detail"/>: bad manifest, wrong apiVersion, missing dependency.</summary>
    Refused,
    /// <summary>The user switched it off (<c>settings.json</c>). Nothing of it runs.</summary>
    Disabled,
    /// <summary>Loaded and configured; <see cref="InstalledPlugin.Instance"/> is live.</summary>
    Loaded,
    /// <summary>Loading or <c>Configure</c> threw; the message is in <see cref="InstalledPlugin.Detail"/>.</summary>
    Failed,
    /// <summary>Marked for removal; the folder is deleted on the next start, when nothing holds its files.</summary>
    PendingUninstall,
}

/// <summary>
/// One folder under <c>plugins/</c>, whatever is in it — listed even when nothing loads, so the Plugins tab can
/// show a broken or refused plugin and offer to uninstall it. Mutated only by <see cref="PluginCatalog"/>, on
/// the UI thread.
/// </summary>
public sealed class InstalledPlugin
{
    public InstalledPlugin(string directory)
    {
        Directory = directory;
        Id = Path.GetFileName(directory);
    }

    /// <summary>The folder name. Equal to <see cref="PluginManifest.Id"/> whenever the manifest is accepted.</summary>
    public string Id { get; }

    public string Directory { get; }

    /// <summary>Set as soon as <c>plugin.json</c> parses, even if the plugin is then refused — the tab still shows its name.</summary>
    public PluginManifest? Manifest { get; internal set; }

    public PluginStatus Status { get; internal set; }

    /// <summary>Why it is refused / failed / pending uninstall, for people. Null when there is nothing to explain.</summary>
    public string? Detail { get; internal set; }

    public IPixiePlugin? Instance { get; internal set; }

    internal PluginHost? Host { get; set; }

    internal PluginLoadContext? LoadContext { get; set; }

    public string Name => Manifest?.Name ?? Id;

    public string Version => Manifest?.Version ?? "?";

    public bool HasUi => Instance is IUiContribution;
}
