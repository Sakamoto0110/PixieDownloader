using System.IO;
using System.Threading;
using System.Windows;
using PixieDownloader.Plugins;
using PixieDownloader.ViewModels;
using YtDlpCore;

namespace PixieDownloader;

public partial class App : Application
{
    private const string MutexName = "PixieDownloader.SingleInstance.v1";
    private const string ActivateEventName = "PixieDownloader.Activate.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;

    // Composition root: services are created by hand (no DI container) and owned by App.
    private SessionLogger? _logger;
    private SettingsService? _settings;
    private YtDlpService? _ytDlpService;
    private PluginCatalog? _plugins;
    private PluginStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ───── Single instance (optional): focus the running window instead of opening a 2nd ─────
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!isFirstInstance)
        {
            _activateEvent.Set();
            Shutdown();
            return;
        }
        StartActivationListener();

        // ───── Composition root: wire the object graph by hand (no DI container) ─────
        _logger = new SessionLogger();
        _settings = new SettingsService(log: _logger.Log);

        // Load settings synchronously before constructing the VM — it copies some settings
        // (e.g. TreatAsPlaylist) into plain fields at construction time, so loading late would
        // leave those fields stuck on AppSettings' hardcoded defaults instead of the saved values.
        _settings.LoadAsync().GetAwaiter().GetResult();

        _ytDlpService = new YtDlpService(_logger, appVersion: AppInfo.Version);
        var pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
        _plugins = new PluginCatalog(
            pluginsDirectory,
            dataDirectory: Path.Combine(AppContext.BaseDirectory, "data"),
            _ytDlpService, _settings.Current.Plugins, _logger);
        // The official plugins come from the latest GitHub Release (or wherever settings point); same UA as the service.
        _store = new PluginStore(PluginStore.ResolveCatalogUrl(_settings.Current.Plugins.CatalogUrl), pluginsDirectory, $"PixieDownloader/{AppInfo.Version}");
        var viewModel = new MainViewModel(_ytDlpService, _settings, _logger, _plugins, _store);

        // Plugins get their Configure here, on the UI thread, before the window exists; the window then
        // builds a tab for each one that has a UI. The VM is already listening, so their log lines land in the Logs tab.
        _plugins.Initialize();

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    private void StartActivationListener()
    {
        var thread = new Thread(() =>
        {
            while (_activateEvent!.WaitOne())
            {
                Dispatcher.Invoke(() =>
                {
                    if (MainWindow is { } w)
                    {
                        if (w.WindowState == WindowState.Minimized)
                            w.WindowState = WindowState.Normal;
                        w.Activate();
                        w.Topmost = true;
                        w.Topmost = false;
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceActivationListener"
        };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose in reverse dependency order; the logger flushes its JSON last.
        _plugins?.ShutdownAll();
        _store?.Dispose();
        _settings?.Dispose();
        _ytDlpService?.Dispose();
        _logger?.Dispose();
        _activateEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
