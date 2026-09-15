using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Hello;

/// <summary>
/// The one that breaks. It does everything a plugin may do in <see cref="Configure"/> — registers a
/// capability, asks for comments, hangs a callback on the token — and then throws out of it. A manifest
/// pointing here (<c>"entryType": "Pixie.Hello.FaultyPlugin"</c>) is how the host tests check that a
/// failed Configure leaves nothing of that behind: the capability unreachable, the comments unwanted, the
/// token cancelled (the callback says so in the logs), and a retry failing for its own reason again rather
/// than colliding with the first attempt's leftovers.
/// </summary>
public sealed class FaultyPlugin : IPixiePlugin
{
    public const string BeforeCapability = "faulty.before";
    public const string Reason = "boom no Configure";
    public const string TokenCancelledMessage = "token cancelado";

    public void Configure(IPluginHost host)
    {
        host.RegisterCapability(BeforeCapability, (Func<string>)(() => "sobrou?"));
        host.RequireAnalysisComments();
        host.ShutdownToken.Register(() => host.Log(LogLevel.Info, TokenCancelledMessage));
        throw new InvalidOperationException(Reason);
    }
}
