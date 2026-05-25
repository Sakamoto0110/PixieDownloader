using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PixieDownloader.ViewModels;
using PixieDownloader.Views;
using YtDlpCore;

namespace PixieDownloader;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _restoringGeometry;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        WireInteractions();

        Loaded += OnLoadedAsync;
        Closing += (_, _) => _vm.OnClosing();
        StateChanged += OnStateChanged;
        SizeChanged += OnSizeChanged;

        TryLoadLogo();
    }

    /// <summary>Uses Assets/logo.ico (if present) for the header badge and window/taskbar icon.</summary>
    private void TryLoadLogo()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.ico"));
            if (info?.Stream is not { } stream)
                return;

            // .ico is multi-resolution — decode and pick the largest frame for a crisp badge.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            if (frame is null)
                return;
            frame.Freeze();

            LogoImage.Source = frame;
            Icon = frame;

            // Logo is transparent — drop the purple badge fill and the "P" so it shows cleanly.
            LogoBadge.Background = System.Windows.Media.Brushes.Transparent;
            LogoFallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // No logo.ico in Assets — the "P" badge stays as the fallback.
        }
    }

    private void WireInteractions()
    {
        _vm.PickFolder = initial =>
        {
            var dlg = new OpenFolderDialog { Title = "Escolha a pasta de saída" };
            if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                dlg.InitialDirectory = initial;
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        };

        _vm.PickCookiesFile = () =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Escolha o arquivo de cookies",
                Filter = "Cookies (*.txt)|*.txt|Todos os arquivos|*.*"
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };

        _vm.PickQueueFile = () =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Importar lista de URLs (.txt)",
                Filter = "Lista de URLs (*.txt)|*.txt|Todos os arquivos|*.*"
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };

        _vm.ChooseFfmpegKind = () =>
        {
            var dlg = new FfmpegInstallDialog { Owner = this };
            var ok = dlg.ShowDialog() == true;
            return Task.FromResult<FfmpegInstallKind?>(ok ? dlg.SelectedKind : null);
        };

        _vm.CopyToClipboard = text =>
        {
            try { Clipboard.SetText(text ?? ""); } catch { }
        };

        _vm.OpenFolderPath = ShellOpen;
        _vm.OpenUrl = ShellOpen;
    }

    private static void ShellOpen(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { /* nothing actionable */ }
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        RestoreGeometry();
        try
        {
            await _vm.InitializeAsync();
        }
        catch { /* surfaced via status bar / logs */ }
    }

    private void RestoreGeometry()
    {
        _restoringGeometry = true;
        try
        {
            var ui = _vm.Settings.Ui;
            if (ui.LastWindowSize.W >= MinWidth) Width = ui.LastWindowSize.W;
            if (ui.LastWindowSize.H >= MinHeight) Height = ui.LastWindowSize.H;
            WindowState = string.Equals(ui.LastWindowState, "Maximized", StringComparison.OrdinalIgnoreCase)
                ? WindowState.Maximized
                : WindowState.Normal;
        }
        finally
        {
            _restoringGeometry = false;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_restoringGeometry)
            return;
        _vm.Settings.Ui.LastWindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_restoringGeometry || WindowState != WindowState.Normal)
            return;
        _vm.Settings.Ui.LastWindowSize.W = Width;
        _vm.Settings.Ui.LastWindowSize.H = Height;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
