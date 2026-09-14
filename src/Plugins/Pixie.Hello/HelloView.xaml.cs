using System.Windows;
using System.Windows.Controls;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Hello;

public partial class HelloView : UserControl
{
    private readonly IPluginHost _host;

    public HelloView(IPluginHost host)
    {
        _host = host;
        InitializeComponent();
        Info.Text = $"{host.Manifest.Name} {host.Manifest.Version} — API {host.Manifest.ApiVersion} — dados em {host.DataDirectory}";
    }

    private async void OnCheckYtDlpClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await _host.Downloads.CheckYtDlpAsync(_host.ShutdownToken);
            Result.Text = status.Installed ? $"yt-dlp {status.Version} em {status.Path}" : $"yt-dlp não encontrado: {status.Error}";
            _host.Log(LogLevel.Info, Result.Text);
        }
        catch (OperationCanceledException)
        {
            // disabled while waiting — nothing to show
        }
        catch (Exception ex)
        {
            Result.Text = ex.Message;
            _host.Log(LogLevel.Error, "CheckYtDlp falhou", ex);
        }
    }

    private void OnGreetClick(object sender, RoutedEventArgs e)
    {
        // The same path another plugin would take: by id, through the host, false when it isn't there.
        Result.Text = _host.TryGetCapability(HelloPlugin.GreetCapability, out var greet) && greet is Func<string, string> f
            ? f("Pixie")
            : "capacidade 'hello.greet' não registrada";
    }
}
