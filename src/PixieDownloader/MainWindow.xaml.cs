using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;
using PixieDownloader.Plugins;
using PixieDownloader.Sdk;
using PixieDownloader.ViewModels;
using PixieDownloader.Views;
using YtDlpCore;

namespace PixieDownloader;

public partial class MainWindow : Window
{
    // The layout is designed for this size; below it the content is scaled down rather than clipped.
    private const double DesignWidth = 940;
    private const double DesignHeight = 560;
    private const double CaptionHeight = 48;

    private readonly MainViewModel _vm;
    private bool _geometryRestored;   // SizeChanged fires before Loaded — ignore it until the saved size is applied
    private bool _shutdownDone;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        WireInteractions();
        WirePluginTabs();

        Loaded += OnLoadedAsync;
        Closing += OnClosing;
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

        _vm.PickPastedLinks = () =>
        {
            var dlg = new PasteLinksDialog { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Result : null;
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

    // ───── Plugin tabs ─────
    // One tab per loaded plugin that implements IUiContribution, between "Fila" and "Plugins": features first,
    // then the tab that manages them, then diagnostics. Inserting there is safe because only the queue tab is
    // addressed by index (MainViewModel.QueueTabIndex). The tab's Tag carries the plugin id so disabling can
    // find it again.
    private const int FirstPluginTabIndex = MainViewModel.QueueTabIndex + 1;
    private int _pluginTabCount;

    private void WirePluginTabs()
    {
        foreach (var plugin in _vm.Plugins.Plugins.Where(p => p.Status == PluginStatus.Loaded))
            AddPluginTab(plugin);
        _vm.Plugins.PluginEnabled += (_, plugin) => AddPluginTab(plugin);
        _vm.Plugins.PluginDisabled += (_, plugin) => RemovePluginTab(plugin);
    }

    private void AddPluginTab(InstalledPlugin plugin)
    {
        if (plugin.Instance is not IUiContribution ui)
            return;

        object header;
        FrameworkElement content;
        try
        {
            header = ui.TabHeader;
            content = ui.CreateView();
        }
        catch (Exception ex)
        {
            // The plugin stays loaded (its capabilities may still work); the tab says what went wrong instead of vanishing.
            _vm.Plugins.Emit(LogEntry.Now(LogLevel.Error, "Plugin:" + plugin.Id, $"A aba falhou ao abrir: {ex.Message}", ex: ex));
            header = plugin.Name;
            content = new TextBlock
            {
                Text = $"A aba do plugin \"{plugin.Name}\" falhou ao abrir:\n{ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };
        }
        content.Margin = new Thickness(0, 4, 0, 0);   // same top gap the built-in panels use

        MainTabs.Items.Insert(FirstPluginTabIndex + _pluginTabCount, new TabItem { Header = header, Content = content, Tag = plugin.Id });
        _pluginTabCount++;
    }

    private void RemovePluginTab(InstalledPlugin plugin)
    {
        var tab = MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Tag, plugin.Id));
        if (tab is null)
            return;
        if (ReferenceEquals(MainTabs.SelectedItem, tab))
            MainTabs.SelectedIndex = 0;
        MainTabs.Items.Remove(tab);
        _pluginTabCount--;
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
        UpdateScale();
        try
        {
            await _vm.InitializeAsync();
        }
        catch { /* surfaced via status bar / logs */ }
    }

    /// <summary>
    /// Closing runs the orderly shutdown (cancel running jobs, save the pending queue, purge staging)
    /// before the window actually goes away: the first request is deferred, the shutdown awaited, and
    /// Close() called again once it is done.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownDone)
            return;
        e.Cancel = true;
        try
        {
            IsEnabled = false;   // no new actions while jobs are being torn down
            await _vm.ShutdownAsync();
        }
        catch { /* best effort — the app is leaving anyway */ }
        finally
        {
            _shutdownDone = true;
            Close();
        }
    }

    private void RestoreGeometry()
    {
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
            _geometryRestored = true;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!_geometryRestored)
            return;
        _vm.Settings.Ui.LastWindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateScale();
        if (!_geometryRestored || WindowState != WindowState.Normal)
            return;
        _vm.Settings.Ui.LastWindowSize.W = Width;
        _vm.Settings.Ui.LastWindowSize.H = Height;
    }

    /// <summary>
    /// Shrinking the window below the design size scales the whole content down uniformly (a
    /// LayoutTransform, so layout happens at the design size and nothing wraps or clips). Above the
    /// design size the scale stays 1 and the layout simply gets more room. The chrome caption follows.
    /// </summary>
    private void UpdateScale()
    {
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        var scale = Math.Min(1.0, Math.Min(w / DesignWidth, h / DesignHeight));
        scale = Math.Max(scale, 0.5);
        if (Math.Abs(RootScale.ScaleX - scale) < 0.001)
            return;
        RootScale.ScaleX = scale;
        RootScale.ScaleY = scale;
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = CaptionHeight * scale;
    }

    /// <summary>Any pick in the "Importar" drop-down closes it (the command itself runs via Command binding).</summary>
    private void OnImportMenuClick(object sender, RoutedEventArgs e) => ImportToggle.IsChecked = false;

    private void OnOpenRepository(object sender, RoutedEventArgs e) => ShellOpen(AppInfo.RepositoryUrl);

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
