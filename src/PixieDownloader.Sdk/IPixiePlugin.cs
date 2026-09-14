namespace PixieDownloader.Sdk;

/// <summary>
/// The entry point of a plugin. The host reads <c>plugin.json</c> first (see <see cref="PluginManifest"/>), and
/// only when the manifest is acceptable loads the assembly into a load context of its own, instantiates
/// <see cref="PluginManifest.EntryType"/> through its parameterless constructor and calls <see cref="Configure"/>
/// once. A plugin never sees another plugin: everything it may use comes through the <see cref="IPluginHost"/>
/// it is given. Implement <see cref="IUiContribution"/> on the same class when the plugin has a tab.
/// </summary>
public interface IPixiePlugin
{
    /// <summary>
    /// Called once, on the UI thread, right after the plugin is instantiated. Keep the host for later and register
    /// capabilities here; anything long-running belongs in a task that watches <see cref="IPluginHost.ShutdownToken"/>.
    /// </summary>
    void Configure(IPluginHost host);
}
