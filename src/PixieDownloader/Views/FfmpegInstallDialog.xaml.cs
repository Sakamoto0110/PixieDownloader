using System.Windows;
using YtDlpCore;

namespace PixieDownloader.Views;

public partial class FfmpegInstallDialog : Window
{
    public FfmpegInstallKind SelectedKind { get; private set; } = FfmpegInstallKind.Full;

    public FfmpegInstallDialog() => InitializeComponent();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SelectedKind = MinimalOption.IsChecked == true ? FfmpegInstallKind.Minimal : FfmpegInstallKind.Full;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
