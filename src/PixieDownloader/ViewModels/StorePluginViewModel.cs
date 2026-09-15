using System.Text.RegularExpressions;
using PixieDownloader.Plugins;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>Where a catalog plugin stands next to what is installed — decided by <see cref="MainViewModel"/> from the catalog.</summary>
public enum StorePluginState
{
    NotInstalled,
    /// <summary>Installed at the catalog's version or a newer one.</summary>
    Installed,
    UpdateAvailable,
    /// <summary>Downloaded while the current version was loaded; it takes over at the next start.</summary>
    PendingRestart,
    /// <summary>Built for an API this app doesn't have — needs a newer app, never a download.</summary>
    Incompatible,
}

/// <summary>
/// One row of "Plugins oficiais" in the Plugins tab: a catalog entry, what the local catalog says about it,
/// and the progress or error of an install in flight. Unlike the installed rows this one has state of its own,
/// because a download outlives any catalog change.
/// </summary>
public sealed class StorePluginViewModel : ObservableObject
{
    private StorePluginState _state;
    private string? _installedVersion;
    private bool _isBusy;
    private double _progress;
    private string? _error;

    public StorePluginViewModel(StorePlugin entry)
    {
        Entry = entry;
    }

    public StorePlugin Entry { get; }

    public string Id => Entry.Id;
    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Id : Entry.Name;
    public string Version => Entry.Version;
    public string VersionLabel => $"v{Entry.Version} · API {Entry.ApiVersion}";
    public string? Description => string.IsNullOrWhiteSpace(Entry.Description) ? null : Entry.Description;
    public bool HasDescription => Description is not null;
    public bool IsCompatible => SdkVersion.IsCompatible(Entry.ApiVersion);

    public StorePluginState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                Notify(nameof(StatusText), nameof(StatusBrushKey), nameof(ActionText), nameof(HasAction), nameof(CanAct));
        }
    }

    /// <summary>The version in <c>plugins/</c>, when there is one.</summary>
    public string? InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (SetProperty(ref _installedVersion, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => State switch
    {
        StorePluginState.NotInstalled => "Não instalado",
        StorePluginState.Installed => $"Instalado (v{InstalledVersion})",
        StorePluginState.UpdateAvailable => $"Instalado v{InstalledVersion} — v{Version} disponível",
        StorePluginState.PendingRestart => $"v{Version} baixado — entra no próximo start do app",
        StorePluginState.Incompatible => $"Precisa de uma versão mais nova do app (API {Entry.ApiVersion}; este app tem a {SdkVersion.Current})",
        _ => "",
    };

    public string StatusBrushKey => State switch
    {
        StorePluginState.Installed => "Brush.Success",
        StorePluginState.UpdateAvailable or StorePluginState.PendingRestart => "Brush.Warning",
        StorePluginState.Incompatible => "Brush.Error",
        _ => "Brush.Text.Tertiary",
    };

    public string ActionText => State switch
    {
        StorePluginState.NotInstalled => "Instalar",
        StorePluginState.UpdateAvailable => "Atualizar",
        _ => "",
    };

    public bool HasAction => ActionText.Length > 0;
    public bool CanAct => HasAction && !IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanAct));
        }
    }

    /// <summary>Download progress, 0–100, while <see cref="IsBusy"/>.</summary>
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

    /// <summary>Why the last install didn't happen, in the store's words; cleared when the next one starts.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>
    /// Where this entry stands against the plugin of the same id in <c>plugins/</c> (null = not installed):
    /// what is installed at the catalog's version or newer is "installed", older is an update, a version the
    /// store already parked for the next start is "pending restart", and an API this app doesn't have is
    /// incompatible whether or not something is installed.
    /// </summary>
    public void Refresh(InstalledPlugin? installed)
    {
        InstalledVersion = installed?.Manifest?.Version;
        State = StateAgainst(installed);
    }

    private StorePluginState StateAgainst(InstalledPlugin? installed)
    {
        if (installed is null)
            return IsCompatible ? StorePluginState.NotInstalled : StorePluginState.Incompatible;
        if (installed.PendingUpdateVersion is { } waiting && SameOrNewer(waiting, Version))
            return StorePluginState.PendingRestart;
        if (installed.Manifest?.Version is { } current && SameOrNewer(current, Version))
            return StorePluginState.Installed;
        return IsCompatible ? StorePluginState.UpdateAvailable : StorePluginState.Incompatible;
    }

    /// <summary>
    /// "1.1.0" against "1.0.0" — true when what is installed is at least the catalog's. Compared on the numeric
    /// part ("2.0.0-dev" counts as 2.0.0: a hand-written plugin.json may carry a suffix); equal strings count
    /// even when neither parses.
    /// </summary>
    private static bool SameOrNewer(string installed, string offered) =>
        string.Equals(installed, offered, StringComparison.OrdinalIgnoreCase)
        || (Numeric(installed) is { } a && Numeric(offered) is { } b && a >= b);

    private static readonly Regex NumericPrefix = new(@"^\d+(\.\d+){1,3}", RegexOptions.Compiled);

    private static System.Version? Numeric(string version) =>
        NumericPrefix.Match(version.Trim()) is { Success: true } m && System.Version.TryParse(m.Value, out var parsed) ? parsed : null;

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            OnPropertyChanged(name);
    }
}
