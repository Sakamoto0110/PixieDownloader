namespace PixieDownloader.Sdk;

/// <summary>
/// What <c>plugins/&lt;id&gt;/plugin.json</c> declares — the same names, camelCase, one JSON object:
/// <code>
/// {
///   "id": "hello",
///   "name": "Hello",
///   "version": "0.1.0",
///   "apiVersion": "1.0",
///   "assemblyFile": "Pixie.Hello.dll",
///   "entryType": "Pixie.Hello.HelloPlugin",
///   "dependsOn": []
/// }
/// </code>
/// The host reads it before loading any code, so a plugin can be listed, disabled or refused for a wrong
/// <see cref="ApiVersion"/> without its assembly ever being touched.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>The folder name under <c>plugins/</c> and the plugin's key everywhere else. Lowercase, no spaces.</summary>
    public required string Id { get; init; }

    /// <summary>What the Plugins tab shows.</summary>
    public required string Name { get; init; }

    /// <summary>The plugin's own version. Informational — the host never compares it.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// The <c>PixieDownloader.Sdk</c> version the plugin was built against, as <c>"major.minor"</c>. The host refuses
    /// the plugin when <see cref="SdkVersion.IsCompatible(string?)"/> says no.
    /// </summary>
    public required string ApiVersion { get; init; }

    /// <summary>File name of the plugin assembly inside its folder, e.g. <c>"Pixie.Hello.dll"</c>.</summary>
    public required string AssemblyFile { get; init; }

    /// <summary>Full name of the class implementing <see cref="IPixiePlugin"/>, e.g. <c>"Pixie.Hello.HelloPlugin"</c>.</summary>
    public required string EntryType { get; init; }

    /// <summary>Ids of plugins that must be installed and enabled for this one to load. Empty for most plugins.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}
