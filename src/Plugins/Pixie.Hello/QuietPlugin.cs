using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Hello;

/// <summary>
/// The other extreme of the contract: nothing but <see cref="Configure"/> — no tab, no capability. A manifest
/// pointing here (<c>"entryType": "Pixie.Hello.QuietPlugin"</c>) is what the host tests use when they need a
/// second plugin that doesn't collide with <see cref="HelloPlugin"/>.
/// </summary>
public sealed class QuietPlugin : IPixiePlugin
{
    public void Configure(IPluginHost host) => host.Log(LogLevel.Debug, "configurado, sem UI");
}
