using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Hello;

/// <summary>
/// The other extreme of the contract: no tab, nothing visible. A manifest pointing here
/// (<c>"entryType": "Pixie.Hello.QuietPlugin"</c>) is what the host tests use when they need a second plugin
/// that doesn't collide with <see cref="HelloPlugin"/>. It does exercise the API 1.1 surface, though: it asks
/// for comments and remembers the last analysis, exposing it as a capability the tests can read back.
/// </summary>
public sealed class QuietPlugin : IPixiePlugin
{
    public const string LastAnalysisCapability = "quiet.lastAnalysis";

    private string? _lastAnalysedUrl;

    public void Configure(IPluginHost host)
    {
        host.RequireAnalysisComments();
        host.AnalysisCompleted += (_, e) => _lastAnalysedUrl = e.Url;
        host.RegisterCapability(LastAnalysisCapability, (Func<string?>)(() => _lastAnalysedUrl));
        host.Log(LogLevel.Debug, "configurado, sem UI");
    }
}
