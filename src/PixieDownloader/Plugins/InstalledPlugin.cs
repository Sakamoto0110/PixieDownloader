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

/// <summary>What <see cref="PluginCatalog.Install"/> did with the folder the store handed it.</summary>
public enum PluginInstallOutcome
{
    /// <summary>In <c>plugins/&lt;id&gt;/</c> and rescanned — loaded unless the user had it disabled.</summary>
    Installed,
    /// <summary>The current version is loaded (its files are mapped), so the new one waits for the next start.</summary>
    PendingRestart,
}

/// <summary>
/// One plugin as found under <c>plugins/</c> — a folder (a plugin that carries dependencies of its own, kept
/// apart so they never mix with anyone else's) or a single <c>.dll</c> loose in the root (a plugin that needs
/// nothing but itself). Listed even when nothing loads, so the Plugins tab can show a broken or refused plugin
/// and offer to uninstall it. Mutated only by <see cref="PluginCatalog"/>, on the UI thread.
/// </summary>
public sealed class InstalledPlugin
{
    private InstalledPlugin(string id, string directory, string? looseAssemblyPath)
    {
        Id = id;
        Directory = directory;
        LooseAssemblyPath = looseAssemblyPath;
    }

    /// <summary>A folder plugin: the folder name is the id.</summary>
    public static InstalledPlugin InFolder(string directory) => new(Path.GetFileName(directory), directory, null);

    /// <summary>A loose plugin: <c>plugins/Pixie.Hello.dll</c> has the id <c>Pixie.Hello</c>.</summary>
    public static InstalledPlugin Loose(string assemblyPath) => new(Path.GetFileNameWithoutExtension(assemblyPath), Path.GetDirectoryName(assemblyPath)!, assemblyPath);

    /// <summary>The folder name, or the file name without <c>.dll</c> for a loose plugin. Equal to <see cref="PluginManifest.Id"/> whenever the manifest is accepted.</summary>
    public string Id { get; }

    /// <summary>The plugin's own folder — for a loose plugin, <c>plugins/</c> itself.</summary>
    public string Directory { get; }

    /// <summary>The DLL of a loose plugin; null for a folder plugin.</summary>
    public string? LooseAssemblyPath { get; }

    public bool IsLoose => LooseAssemblyPath is not null;

    /// <summary>What to show and open: the folder, or the DLL itself.</summary>
    public string Location => LooseAssemblyPath ?? Directory;

    /// <summary>Set as soon as <c>plugin.json</c> parses, even if the plugin is then refused — the tab still shows its name.</summary>
    public PluginManifest? Manifest { get; internal set; }

    public PluginStatus Status { get; internal set; }

    /// <summary>Why it is refused / failed / pending uninstall, for people. Null when there is nothing to explain.</summary>
    public string? Detail { get; internal set; }

    /// <summary>The version the store downloaded into <c>plugins/.~update-&lt;id&gt;/</c>, waiting for the next start; null when none.</summary>
    public string? PendingUpdateVersion { get; internal set; }

    public IPixiePlugin? Instance { get; internal set; }

    internal PluginHost? Host { get; set; }

    internal PluginLoadContext? LoadContext { get; set; }

    public string Name => Manifest?.Name ?? Id;

    public string Version => Manifest?.Version ?? "?";

    public bool HasUi => Instance is IUiContribution;
}
