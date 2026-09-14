using System.Diagnostics.CodeAnalysis;
using YtDlpCore;

namespace PixieDownloader.Sdk;

/// <summary>
/// Everything a plugin may use from the host — one instance per plugin, handed to
/// <see cref="IPixiePlugin.Configure"/>. Plugins never talk to each other directly: a capability registered here
/// is the only way one can be reached, and always through the host, so a disabled plugin simply is not found.
/// </summary>
public interface IPluginHost
{
    /// <summary>This plugin's manifest, as read from its <c>plugin.json</c>.</summary>
    PluginManifest Manifest { get; }

    /// <summary>The download service the app itself uses: URL analysis, downloads, thumbnails, tool management.</summary>
    IYtDlpService Downloads { get; }

    /// <summary>
    /// A folder for this plugin's own files — <c>data/&lt;id&gt;/</c> next to the executable, created on first use.
    /// Disabling or uninstalling the plugin leaves it alone: what is in there is the user's, not the plugin's.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>Cancelled when the plugin is disabled or the app closes. Long-running work must watch it.</summary>
    CancellationToken ShutdownToken { get; }

    /// <summary>Writes to the Logs tab and to the session log on disk, with the plugin id as the source.</summary>
    void Log(LogLevel level, string message, Exception? exception = null);

    /// <summary>
    /// Publishes something other plugins may call, under an id such as <c>"tracklist.extract"</c>. The delegate's
    /// signature is the contract — document it together with the id. Disposing the result unregisters it; the host
    /// does that on its own when the plugin is disabled.
    /// </summary>
    IDisposable RegisterCapability(string id, Delegate implementation);

    /// <summary>
    /// Looks up a capability by id. <see langword="false"/> is the normal path, not an error: the plugin that
    /// provides it may not be installed, or may be disabled right now.
    /// </summary>
    bool TryGetCapability(string id, [NotNullWhen(true)] out Delegate? implementation);
}
