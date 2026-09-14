using System.Windows;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Hello;

/// <summary>
/// The smallest plugin that touches every part of the contract: it is configured by the host, registers a
/// capability, logs, and contributes a tab whose view calls <see cref="IPluginHost.Downloads"/>.
/// </summary>
public sealed class HelloPlugin : IPixiePlugin, IUiContribution
{
    public const string GreetCapability = "hello.greet";

    private IPluginHost _host = null!;

    public void Configure(IPluginHost host)
    {
        _host = host;
        // Func<string, string>: give it a name, get a greeting. Any plugin can reach it through its own host.
        host.RegisterCapability(GreetCapability, (Func<string, string>)(name => $"Olá, {name}!"));
        host.Log(LogLevel.Info, $"configurado: {host.Manifest.Name} {host.Manifest.Version}");
    }

    public string TabHeader => "Hello";

    public FrameworkElement CreateView() => new HelloView(_host);
}
