using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PixieDownloader.ViewModels;
using YtDlpCore;

namespace PixieDownloader;

public partial class App : Application
{
    private const string MutexName = "PixieDownloader.SingleInstance.v1";
    private const string ActivateEventName = "PixieDownloader.Activate.v1";

    private IHost? _host;
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services ?? throw new InvalidOperationException("Host not built.");

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

        // ───── Composition root ─────
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<SessionLogger>();
        builder.Services.AddSingleton<SettingsService>(sp =>
            new SettingsService(log: sp.GetRequiredService<SessionLogger>().Log));
        builder.Services.AddSingleton<IYtDlpService>(sp =>
            new YtDlpService(sp.GetRequiredService<SessionLogger>()));
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();

        // Load settings synchronously before the UI binds (fast, one small file, no window yet).
        _host.Services.GetRequiredService<SettingsService>().LoadAsync().GetAwaiter().GetResult();

        var window = _host.Services.GetRequiredService<MainWindow>();
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
        _host?.Dispose();
        _activateEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
