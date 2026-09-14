using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Shell;
using System.Windows.Threading;
using PixieDownloader.Mvvm;
using PixieDownloader.Plugins;
using YtDlpCore;

namespace PixieDownloader.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IYtDlpService _service;
    private readonly SettingsService _settings;
    private readonly SessionLogger _logger;
    private readonly PluginCatalog _plugins;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _analyzeCts;
    private CancellationTokenSource? _autoAnalyzeCts;
    private CancellationTokenSource? _gifPreviewCts;
    private string? _currentPlaylistUrl;
    private string? _lastAnalyzedUrl;
    private bool _isImportedList;        // true when the list came from an imported .txt / pasted links (each row is a distinct URL)
    private string? _importFolder;       // output subfolder name for the imported list (null = output dir itself)
    private bool _importNumbered;        // imported rows get a "N - " file-name prefix (N = position in the list)

    public MainViewModel(IYtDlpService service, SettingsService settings, SessionLogger logger, PluginCatalog plugins)
    {
        _service = service;
        _settings = settings;
        _logger = logger;
        _plugins = plugins;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        // Plugins load before the window exists; listening from the start keeps their load lines in the Logs tab,
        // and the Plugins tab rebuilds its rows on every status change (Initialize included).
        _plugins.LogEmitted += OnLogEmitted;
        _plugins.Changed += (_, _) => RefreshPluginItems();

        // ───────────────────────── Commands ─────────────────────────
        AddTokenCommand = new RelayCommand<TokenOption>(AddToken);
        RemoveLastTokenCommand = new RelayCommand(RemoveLastToken, () => HasTokens);
        ClearTokensCommand = new RelayCommand(ClearTokens, () => HasTokens);
        TogglePlaylistCollapsedCommand = new RelayCommand(TogglePlaylistCollapsed);
        TogglePreviewCollapsedCommand = new RelayCommand(TogglePreviewCollapsed);
        OpenPreviewCommand = new RelayCommand(OpenPreview);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, CanAnalyze);
        UseRecentCommand = new RelayCommand<RecentUrl>(UseRecent);
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        InvertSelectionCommand = new RelayCommand(InvertSelection);
        DownloadCommand = new RelayCommand(Download, CanDownload);
        ImportQueueCommand = new AsyncRelayCommand(ImportQueueAsync, CanImport);
        PasteLinksCommand = new AsyncRelayCommand(PasteLinksAsync, CanImport);
        CancelCommand = new RelayCommand(CancelTopJob, CanCancel);
        ShowQueueCommand = new RelayCommand(() => SelectedTabIndex = QueueTabIndex);
        ClearFinishedJobsCommand = new RelayCommand(ClearFinishedJobs, () => HasFinishedJobs);
        CancelAllJobsCommand = new RelayCommand(CancelAllJobs, () => HasPendingJobs);
        JobActionCommand = new RelayCommand<DownloadJobViewModel>(JobAction);
        RetryJobCommand = new RelayCommand<DownloadJobViewModel>(RetryJob);
        ContinuePendingCommand = new RelayCommand(StartQueue, () => RestoredJobCount > 0);
        DiscardPendingCommand = new RelayCommand(DiscardRestoredJobs, () => RestoredJobCount > 0);
        DismissNoticeCommand = new RelayCommand(() => StartupNotice = null);
        InstallYtDlpCommand = new AsyncRelayCommand(InstallYtDlpAsync, () => !YtDlp.IsWorking);
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync, () => !Ffmpeg.IsWorking);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);
        UpdateYtDlpCommand = new AsyncRelayCommand(UpdateYtDlpAsync);
        BrowseOutputDirectoryCommand = new RelayCommand(BrowseOutputDirectory);
        BrowseCookiesFileCommand = new RelayCommand(BrowseCookiesFile);
        ClearCookiesCommand = new RelayCommand(ClearCookies);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        EnablePluginCommand = new RelayCommand<PluginItemViewModel>(p => { if (p is not null) _plugins.Enable(p.Id); });
        DisablePluginCommand = new RelayCommand<PluginItemViewModel>(p => { if (p is not null) _plugins.Disable(p.Id); });
        UninstallPluginCommand = new RelayCommand<PluginItemViewModel>(p => { if (p is not null) _plugins.Uninstall(p.Id); });
        CancelUninstallPluginCommand = new RelayCommand<PluginItemViewModel>(p => { if (p is not null) _plugins.CancelUninstall(p.Id); });
        OpenPluginFolderCommand = new RelayCommand<PluginItemViewModel>(p => { if (p is not null) OpenFolderPath?.Invoke(p.Directory); });
        OpenPluginsFolderCommand = new RelayCommand(() => OpenFolderPath?.Invoke(_plugins.PluginsDirectory));
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
        SimulateCommand = new AsyncRelayCommand(Simulate);
        GetFilenameCommand = new AsyncRelayCommand(GetFilename);
        GetTitleCommand = new AsyncRelayCommand(GetTitle);
        GetDurationCommand = new AsyncRelayCommand(GetDuration);
        GetThumbnailCommand = new AsyncRelayCommand(GetThumbnail);
        GetDirectUrlCommand = new AsyncRelayCommand(GetDirectUrl);
        GetIdCommand = new AsyncRelayCommand(GetId);
        ListFormatsCommand = new AsyncRelayCommand(ListFormats);
        DumpJsonCommand = new AsyncRelayCommand(DumpJson);
        RunRawCommand = new AsyncRelayCommand(RunRaw);
        ClearOutputCommand = new RelayCommand(ClearOutput);
        CopyOutputCommand = new RelayCommand(CopyOutput);
        AnalyzeGifPreviewCommand = new AsyncRelayCommand(AnalyzeGifPreviewAsync, CanAnalyzeGifPreview);
        OpenGifPreviewCommand = new RelayCommand(OpenGifPreview, () => HasGifPreview);

        // Restore persisted UI toggles (direct field writes → no side effects on startup).
        _treatAsPlaylist = Settings.Ui.TreatAsPlaylist;
        _isPreviewCollapsed = Settings.Ui.PreviewCollapsed;

        PlaylistView = CollectionViewSource.GetDefaultView(PlaylistItems);
        PlaylistView.Filter = FilterPlaylistItem;

        LogsView = CollectionViewSource.GetDefaultView(Logs);
        LogsView.Filter = FilterLogEntry;

        Jobs.CollectionChanged += (_, _) => RefreshQueueState();

        // Persist + re-render when settings nodes change.
        Settings.Advanced.PropertyChanged += OnAdvancedSettingsChanged;
        Settings.Paths.PropertyChanged += OnPathsSettingsChanged;
        Settings.Audio.PropertyChanged += OnAudioSettingsChanged;

        // Show the progress bar while a tool install/update is running, too.
        YtDlp.PropertyChanged += OnToolStatusChanged;
        Ffmpeg.PropertyChanged += OnToolStatusChanged;

        // Advanced template builder: seed the token stack and keep the stack buttons in sync.
        TemplateTokens.CollectionChanged += (_, _) =>
        {
            RemoveLastTokenCommand.NotifyCanExecuteChanged();
            ClearTokensCommand.NotifyCanExecuteChanged();
        };
        SyncTokensFromTemplate();
    }

    // ───────────────────────── Commands (initialized in the constructor) ─────────────────────────
    public RelayCommand<TokenOption> AddTokenCommand { get; }
    public RelayCommand RemoveLastTokenCommand { get; }
    public RelayCommand ClearTokensCommand { get; }
    public RelayCommand TogglePlaylistCollapsedCommand { get; }
    public RelayCommand TogglePreviewCollapsedCommand { get; }
    public RelayCommand OpenPreviewCommand { get; }
    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand<RecentUrl> UseRecentCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand InvertSelectionCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public AsyncRelayCommand ImportQueueCommand { get; }
    public AsyncRelayCommand PasteLinksCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ShowQueueCommand { get; }
    public RelayCommand ClearFinishedJobsCommand { get; }
    public RelayCommand CancelAllJobsCommand { get; }
    public RelayCommand<DownloadJobViewModel> JobActionCommand { get; }
    public RelayCommand<DownloadJobViewModel> RetryJobCommand { get; }
    public RelayCommand ContinuePendingCommand { get; }
    public RelayCommand DiscardPendingCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }
    public AsyncRelayCommand InstallYtDlpCommand { get; }
    public AsyncRelayCommand InstallFfmpegCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }
    public AsyncRelayCommand UpdateYtDlpCommand { get; }
    public RelayCommand BrowseOutputDirectoryCommand { get; }
    public RelayCommand BrowseCookiesFileCommand { get; }
    public RelayCommand ClearCookiesCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand<PluginItemViewModel> EnablePluginCommand { get; }
    public RelayCommand<PluginItemViewModel> DisablePluginCommand { get; }
    public RelayCommand<PluginItemViewModel> UninstallPluginCommand { get; }
    public RelayCommand<PluginItemViewModel> CancelUninstallPluginCommand { get; }
    public RelayCommand<PluginItemViewModel> OpenPluginFolderCommand { get; }
    public RelayCommand OpenPluginsFolderCommand { get; }
    public RelayCommand OpenReleasePageCommand { get; }
    public AsyncRelayCommand SimulateCommand { get; }
    public AsyncRelayCommand GetFilenameCommand { get; }
    public AsyncRelayCommand GetTitleCommand { get; }
    public AsyncRelayCommand GetDurationCommand { get; }
    public AsyncRelayCommand GetThumbnailCommand { get; }
    public AsyncRelayCommand GetDirectUrlCommand { get; }
    public AsyncRelayCommand GetIdCommand { get; }
    public AsyncRelayCommand ListFormatsCommand { get; }
    public AsyncRelayCommand DumpJsonCommand { get; }
    public AsyncRelayCommand RunRawCommand { get; }
    public RelayCommand ClearOutputCommand { get; }
    public RelayCommand CopyOutputCommand { get; }
    public AsyncRelayCommand AnalyzeGifPreviewCommand { get; }
    public RelayCommand OpenGifPreviewCommand { get; }

    // ───────────────────────── View interaction hooks (set by MainWindow) ─────────────────────────
    public Func<string?, string?>? PickFolder { get; set; }
    public Func<string?>? PickCookiesFile { get; set; }
    public Func<string?>? PickQueueFile { get; set; }
    /// <summary>Opens the "Colar links" dialog; null when cancelled.</summary>
    public Func<PastedLinks?>? PickPastedLinks { get; set; }
    public Func<Task<FfmpegInstallKind?>>? ChooseFfmpegKind { get; set; }
    public Action<string>? CopyToClipboard { get; set; }
    public Action<string>? OpenFolderPath { get; set; }
    public Action<string>? OpenUrl { get; set; }

    /// <summary>
    /// Index of the selected tab: 0 = Baixar, 1 = Fila, then one tab per loaded plugin that has a UI, then
    /// Plugins, Debug and Logs. Only the queue is addressed by index — the rest shifts as plugin tabs come and go.
    /// </summary>
    public const int QueueTabIndex = 1;

    /// <summary>The plugins found next to the executable; the window builds their tabs from it (MainWindow.WirePluginTabs).</summary>
    public PluginCatalog Plugins => _plugins;

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    // ───────────────────────── Exposed settings & options ─────────────────────────
    public AppSettings Settings => _settings.Current;
    public string[] BitrateOptions { get; } = ["128k", "192k", "320k"];
    public LogLevel[] LogLevelOptions { get; } = [LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error];

    /// <summary>Friendly output-organization choices; each maps to a yt-dlp output template.</summary>
    public IReadOnlyList<TemplatePreset> TemplatePresets { get; } =
    [
        new("Sem subpastas (tudo junto)", "%(title)s.%(ext)s"),
        new("Subpasta por playlist", "%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s"),
        new("Subpasta por canal", "%(uploader)s/%(title)s.%(ext)s"),
        new("Prefixo por data", "%(upload_date)s - %(title)s.%(ext)s"),
    ];

    /// <summary>
    /// The organization preset that matches the current template, or <c>null</c> when the template
    /// was hand-edited (in the advanced field) to something custom. Setting it rewrites the template.
    /// </summary>
    public TemplatePreset? SelectedOrganization
    {
        get => TemplatePresets.FirstOrDefault(p => string.Equals(p.Template, Settings.Paths.LastTemplate, StringComparison.Ordinal));
        set
        {
            if (value is not null && !string.Equals(value.Template, Settings.Paths.LastTemplate, StringComparison.Ordinal))
                Settings.Paths.LastTemplate = value.Template;  // Paths change handler refreshes preview + this prop
        }
    }

    /// <summary>True when the template doesn't match any organization preset (edited in Advanced).</summary>
    public bool IsCustomTemplate => SelectedOrganization is null;

    // ───────────────────────── Advanced template: incremental token stack ─────────────────────────

    /// <summary>Palette of building blocks shown in the advanced editor (fields + separators).</summary>
    public IReadOnlyList<TokenOption> TokenPalette { get; } =
    [
        new("Título", "%(title)s"),
        new("Canal", "%(uploader)s"),
        new("Playlist", "%(playlist_title)s"),
        new("Nº playlist", "%(playlist_index)02d"),
        new("Data", "%(upload_date)s"),
        new("ID", "%(id)s"),
        new("Extensão", "%(ext)s"),
        new("/ (subpasta)", "/"),
        new("- (hífen)", " - "),
        new(". (ponto)", "."),
        new("_ (underline)", "_"),
    ];

    /// <summary>The template broken into an ordered stack of segments (fields + literals).</summary>
    public ObservableCollection<string> TemplateTokens { get; } = [];

    private bool _suppressTokenSync;
    private bool HasTokens => TemplateTokens.Count > 0;

    private void AddToken(TokenOption? token)
    {
        if (token is null) return;
        TemplateTokens.Add(token.Value);
        RebuildTemplateFromTokens();
    }

    private void RemoveLastToken()
    {
        if (TemplateTokens.Count == 0) return;
        TemplateTokens.RemoveAt(TemplateTokens.Count - 1);
        RebuildTemplateFromTokens();
    }

    private void ClearTokens()
    {
        TemplateTokens.Clear();
        RebuildTemplateFromTokens();
    }

    /// <summary>Writes the concatenated stack back to the template (guarded against re-tokenizing).</summary>
    private void RebuildTemplateFromTokens()
    {
        _suppressTokenSync = true;
        Settings.Paths.LastTemplate = string.Concat(TemplateTokens);
        _suppressTokenSync = false;
    }

    /// <summary>Re-derives the token stack from the template (combo pick, manual edit, or load).</summary>
    private void SyncTokensFromTemplate()
    {
        if (_suppressTokenSync) return;
        _suppressTokenSync = true;
        TemplateTokens.Clear();
        foreach (var seg in TokenizeTemplate(Settings.Paths.LastTemplate))
            TemplateTokens.Add(seg);
        _suppressTokenSync = false;
    }

    /// <summary>Splits a template into yt-dlp field tokens (e.g. <c>%(title)s</c>) and the literals between them.</summary>
    private static IEnumerable<string> TokenizeTemplate(string template)
    {
        if (string.IsNullOrEmpty(template))
            yield break;

        int last = 0;
        foreach (Match m in Regex.Matches(template, @"%\(\w+\)(?:0\d+)?[sd]"))
        {
            if (m.Index > last)
                yield return template[last..m.Index];   // literal chunk
            yield return m.Value;                        // field token
            last = m.Index + m.Length;
        }
        if (last < template.Length)
            yield return template[last..];
    }

    // ───────────────────────── URL analysis state ─────────────────────────
    private string _url = "";
    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
            {
                AnalyzeCommand.NotifyCanExecuteChanged();
                OnUrlChanged(value);
            }
        }
    }

    private void OnUrlChanged(string value) => ScheduleAutoAnalyze(value);

    /// <summary>When true, a URL with a list is analysed as a playlist; when false, as a single video.</summary>
    private bool _treatAsPlaylist = true;
    public bool TreatAsPlaylist
    {
        get => _treatAsPlaylist;
        set
        {
            if (SetProperty(ref _treatAsPlaylist, value))
                OnTreatAsPlaylistChanged(value);
        }
    }

    private void OnTreatAsPlaylistChanged(bool value)
    {
        Settings.Ui.TreatAsPlaylist = value;   // persisted (debounced) by SettingsService
        // Re-analyse the current URL under the new mode.
        if (!string.IsNullOrWhiteSpace(Url) && AnalyzeCommand.CanExecute(null))
            AnalyzeCommand.Execute(null);
    }

    /// <summary>
    /// When true, downloads the actual video (MP4) and reveals the video options section; when false
    /// the app extracts MP3 (its original behavior). Proxies the persisted <c>Settings.Video.DownloadVideo</c>.
    /// </summary>
    public bool DownloadVideo
    {
        get => Settings.Video.DownloadVideo;
        set
        {
            if (Settings.Video.DownloadVideo == value)
                return;
            Settings.Video.DownloadVideo = value;   // persisted (debounced) by SettingsService
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadButtonLabel));
            UpdateTemplatePreview();                 // output extension flips between .mp3 and .mp4
        }
    }

    /// <summary>
    /// Label of the main action button: what the current analysis would enqueue, or — with nothing
    /// analysed and downloads restored from the previous session — the offer to resume them.
    /// </summary>
    public string DownloadButtonLabel
    {
        get
        {
            if (HasResult && IsPlaylist)
                return $"Baixar selecionados ({SelectedCount})";
            if (!HasResult && RestoredJobCount > 0)
                return ContinueLabel;
            return DownloadVideo ? "Baixar vídeo" : "Baixar como MP3";
        }
    }

    /// <summary>
    /// Extracts a short trimmed clip as an animated .gif (no audio) instead of an MP4. Only takes
    /// effect for a single (non-playlist) video — see <see cref="CanTrim"/>. Proxies the persisted
    /// <c>Settings.Video.ExtractGif</c>.
    /// </summary>
    public bool ExtractGif
    {
        get => Settings.Video.ExtractGif;
        set
        {
            if (Settings.Video.ExtractGif == value)
                return;
            Settings.Video.ExtractGif = value;   // persisted (debounced) by SettingsService
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowGenericTrim));
            OnPropertyChanged(nameof(ShowGifTrim));
            OnPropertyChanged(nameof(GifRangeText));
            AnalyzeGifPreviewCommand.NotifyCanExecuteChanged();
            UpdateTemplatePreview();              // output extension flips to .gif
        }
    }

    /// <summary>Length (1..20s) of the GIF clip, starting at <see cref="TrimStartSeconds"/>.</summary>
    public double GifDurationSeconds
    {
        get => Settings.Video.GifDurationSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 20);
            if (Settings.Video.GifDurationSeconds == clamped)
                return;
            Settings.Video.GifDurationSeconds = clamped;   // persisted (debounced) by SettingsService
            OnPropertyChanged();
            OnPropertyChanged(nameof(GifRangeText));
            OnPropertyChanged(nameof(GifDurationText));
        }
    }

    /// <summary>
    /// Text proxy for exact entry of <see cref="GifDurationSeconds"/> — a real repeating loop rarely
    /// lands on a round number (1.25s, 1.1s, ...), and the slider alone can only get you close.
    /// </summary>
    public string GifDurationText
    {
        get => GifDurationSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                GifDurationSeconds = seconds;
            else
                OnPropertyChanged();   // revert the textbox to the last valid value
        }
    }

    /// <summary>
    /// Auto-analyzes a freshly pasted/typed URL after a short debounce — there is no "Analisar" button;
    /// Enter re-runs the analysis of the current URL (e.g. to retry after a network error).
    /// A new URL supersedes an analysis still running for the previous one.
    /// </summary>
    private void ScheduleAutoAnalyze(string? value)
    {
        _autoAnalyzeCts?.Cancel();

        var url = value?.Trim() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        if (string.Equals(url, _lastAnalyzedUrl, StringComparison.OrdinalIgnoreCase))
            return;

        _analyzeCts?.Cancel();   // the running analysis (if any) is for a URL the user just replaced

        _autoAnalyzeCts = new CancellationTokenSource();
        var token = _autoAnalyzeCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(700, token); }
            catch (OperationCanceledException) { return; }

            _ = _dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested && AnalyzeCommand.CanExecute(null))
                    AnalyzeCommand.Execute(null);
            });
        });
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AnalyzeCommand.NotifyCanExecuteChanged();
                DownloadCommand.NotifyCanExecuteChanged();
                ImportQueueCommand.NotifyCanExecuteChanged();
                PasteLinksCommand.NotifyCanExecuteChanged();
                AnalyzeGifPreviewCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ShowProgress));
            }
        }
    }

    private bool _hasResult;
    public bool HasResult
    {
        get => _hasResult;
        set
        {
            if (SetProperty(ref _hasResult, value))
            {
                DownloadCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(DownloadButtonLabel));
            }
        }
    }

    private bool _isPlaylist;
    public bool IsPlaylist
    {
        get => _isPlaylist;
        set
        {
            if (SetProperty(ref _isPlaylist, value))
            {
                OnPropertyChanged(nameof(CanTrim));
                OnPropertyChanged(nameof(ShowGenericTrim));
                OnPropertyChanged(nameof(ShowGifTrim));
                OnPropertyChanged(nameof(DownloadButtonLabel));
                AnalyzeGifPreviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private VideoInfo? _singleVideo;
    public VideoInfo? SingleVideo
    {
        get => _singleVideo;
        set
        {
            if (SetProperty(ref _singleVideo, value))
            {
                OnPropertyChanged(nameof(PreviewUrl));
                OnPropertyChanged(nameof(TrimMaxSeconds));
                OnPropertyChanged(nameof(CanTrim));
                OnPropertyChanged(nameof(ShowGenericTrim));
                OnPropertyChanged(nameof(ShowGifTrim));
                AnalyzeGifPreviewCommand.NotifyCanExecuteChanged();
                ResetTrimRange();
                RebuildMetadataItems();
            }
        }
    }

    // ───────────────────────── Video trim (start/end window, single video only) ─────────────────────────
    private const double MinTrimGapSeconds = 0.5;

    private double _trimStartSeconds;
    public double TrimStartSeconds
    {
        get => _trimStartSeconds;
        set
        {
            var max = Math.Max(0, _trimEndSeconds - MinTrimGapSeconds);
            value = Math.Clamp(value, 0, max);
            if (SetProperty(ref _trimStartSeconds, value))
            {
                OnPropertyChanged(nameof(TrimRangeText));
                OnPropertyChanged(nameof(GifRangeText));
                OnPropertyChanged(nameof(TrimStartText));
            }
        }
    }

    /// <summary>
    /// Text proxy for exact entry of <see cref="TrimStartSeconds"/> as h:mm:ss.fff — for a video many
    /// hours long, dragging the slider alone can't land on a precise (sub-second) position.
    /// </summary>
    public string TrimStartText
    {
        get => TimeSpan.FromSeconds(TrimStartSeconds).ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        set
        {
            var text = value?.Trim() ?? "";
            if (TimeSpan.TryParseExact(text, @"h\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var ts) ||
                TimeSpan.TryParseExact(text, @"h\:mm\:ss", CultureInfo.InvariantCulture, out ts))
                TrimStartSeconds = ts.TotalSeconds;
            else
                OnPropertyChanged();   // revert the textbox to the last valid value
        }
    }

    private double _trimEndSeconds;
    public double TrimEndSeconds
    {
        get => _trimEndSeconds;
        set
        {
            var min = Math.Min(_trimStartSeconds + MinTrimGapSeconds, TrimMaxSeconds);
            value = Math.Clamp(value, min, TrimMaxSeconds);
            if (SetProperty(ref _trimEndSeconds, value))
                OnPropertyChanged(nameof(TrimRangeText));
        }
    }

    /// <summary>Upper bound for both trim sliders — the analyzed single video's duration, or 0 when unknown.</summary>
    public double TrimMaxSeconds => SingleVideo?.Duration?.TotalSeconds ?? 0;

    /// <summary>Trim only applies to a single (non-playlist) video whose duration we know.</summary>
    public bool CanTrim => !IsPlaylist && TrimMaxSeconds > MinTrimGapSeconds;

    /// <summary>Generic start/end trim controls — shown for a plain trimmed download (not GIF mode).</summary>
    public bool ShowGenericTrim => CanTrim && !ExtractGif;

    /// <summary>Start + friendly duration controls — shown when extracting a GIF clip.</summary>
    public bool ShowGifTrim => CanTrim && ExtractGif;

    public string GifRangeText =>
        $"{FormatTrimTime(TrimStartSeconds)} + {GifDurationSeconds:0.#}s → {FormatTrimTime(Math.Min(TrimStartSeconds + GifDurationSeconds, TrimMaxSeconds))}";

    public string TrimRangeText =>
        $"{FormatTrimTime(TrimStartSeconds)} – {FormatTrimTime(TrimEndSeconds)}  ({FormatTrimTime(TrimEndSeconds - TrimStartSeconds)})";

    private static string FormatTrimTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>Resets the trim window to the full video whenever a new single video is analyzed.</summary>
    private void ResetTrimRange()
    {
        _trimStartSeconds = 0;
        _trimEndSeconds = TrimMaxSeconds;
        OnPropertyChanged(nameof(TrimStartSeconds));
        OnPropertyChanged(nameof(TrimEndSeconds));
        OnPropertyChanged(nameof(TrimRangeText));
        OnPropertyChanged(nameof(TrimStartText));
        GifPreviewPath = null;   // any cached preview belonged to the previous video
    }

    // ───────────────────────── GIF preview cache ─────────────────────────
    private const int GifPreviewWindowSeconds = 20;

    private string? _gifPreviewPath;
    public string? GifPreviewPath
    {
        get => _gifPreviewPath;
        private set
        {
            if (SetProperty(ref _gifPreviewPath, value))
            {
                OnPropertyChanged(nameof(HasGifPreview));
                OpenGifPreviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasGifPreview => !string.IsNullOrEmpty(GifPreviewPath);

    private bool CanAnalyzeGifPreview() => !IsBusy && !IsDownloading && ShowGifTrim;

    /// <summary>
    /// Downloads just a ~20s window starting at <see cref="TrimStartSeconds"/> (via the same
    /// <c>--download-sections</c> mechanism as the real download) into a small local cache file, then
    /// opens it in the OS default video player so the exact loop boundaries can be scrubbed frame by
    /// frame — much cheaper than re-downloading from the source on every timing tweak.
    /// </summary>
    private async Task AnalyzeGifPreviewAsync()
    {
        if (SingleVideo is not { } v)
            return;

        _gifPreviewCts = new CancellationTokenSource();
        IsBusy = true;
        IsIndeterminate = true;
        StatusText = "Baixando prévia do GIF...";
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, ".~gif-preview");
            Directory.CreateDirectory(dir);
            var previewPath = Path.Combine(dir, "preview.mp4");
            try { if (File.Exists(previewPath)) File.Delete(previewPath); } catch { /* leave the stale file, still overwritten by yt-dlp */ }

            var end = Math.Min(TrimStartSeconds + GifPreviewWindowSeconds, TrimMaxSeconds);
            // Speed is applied here too (via the regular, non-GIF speed pass) so the cached preview
            // plays back at the same rate the final GIF will — useful for spotting the loop in slow-mo.
            var previewVideo = new VideoOptions(
                DownloadVideo: true,
                IncludeAudio: false,
                Speed: Settings.Video.GifSpeed,
                StartTime: TimeSpan.FromSeconds(TrimStartSeconds),
                EndTime: TimeSpan.FromSeconds(end));
            var previewAudio = new AudioOptions(EmbedThumbnail: false, EmbedMetadata: false);
            var previewAdvanced = new AdvancedOptions(Settings.Advanced.Retries, Settings.Advanced.TimeoutSeconds, null, Settings.Advanced.CookiesFilePath);
            var req = new DownloadRequest(v.WebpageUrl, dir, "preview.%(ext)s", previewAudio, previewAdvanced) { Video = previewVideo };

            var result = await _service.DownloadAsync(req, null, _gifPreviewCts.Token);
            // Use the known template path rather than the parsed stdout destination — yt-dlp's console
            // output for a --download-sections clip has extra ffmpeg/merge lines that make the generic
            // "Destination:" parsing unreliable here, but we already know exactly where this one lands.
            if (result.Success && File.Exists(previewPath))
            {
                GifPreviewPath = previewPath;
                OpenFolderPath?.Invoke(previewPath);   // opens with the OS default video player
                StatusText = "Prévia baixada — ajuste os tempos e repita se precisar.";
            }
            else
            {
                GifPreviewPath = null;
                StatusText = result.Success
                    ? "Prévia baixada, mas o arquivo não foi encontrado."
                    : $"Falha na prévia: {result.ErrorMessage}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Prévia cancelada.";
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
        }
    }

    private void OpenGifPreview()
    {
        if (GifPreviewPath is { } path && File.Exists(path))
        {
            OpenFolderPath?.Invoke(path);
        }
        else
        {
            GifPreviewPath = null;   // stale pointer — the cached file is gone
            StatusText = "Prévia não encontrada — clique em Analisar GIF de novo.";
        }
    }

    private string? _playlistTitle;
    public string? PlaylistTitle
    {
        get => _playlistTitle;
        set => SetProperty(ref _playlistTitle, value);
    }

    private string? _playlistUploader;
    public string? PlaylistUploader
    {
        get => _playlistUploader;
        set => SetProperty(ref _playlistUploader, value);
    }

    public ObservableCollection<PlaylistItemViewModel> PlaylistItems { get; } = [];
    public ICollectionView PlaylistView { get; }

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                OnFilterTextChanged(value);
        }
    }
    private void OnFilterTextChanged(string value)
    {
        // "%[a:b]" in the search box is a selection command, not a text filter.
        if (TryParseRangeCommand(value, out var start, out var end))
            ApplyRangeSelection(start, end);
        PlaylistView.Refresh();
    }

    private PlaylistItemViewModel? _selectedPlaylistItem;
    public PlaylistItemViewModel? SelectedPlaylistItem
    {
        get => _selectedPlaylistItem;
        set
        {
            if (SetProperty(ref _selectedPlaylistItem, value))
            {
                OnPropertyChanged(nameof(PreviewUrl));
                OnSelectedPlaylistItemChanged(value);
            }
        }
    }
    private void OnSelectedPlaylistItemChanged(PlaylistItemViewModel? value)
    {
        if (value is not null)
            _ = LoadPreviewThumbnailAsync(value.Id, value.ThumbnailUrl);
        RebuildMetadataItems();
    }

    public int SelectedCount => PlaylistItems.Count(i => i.IsSelected);
    public string SelectedCountText => $"{SelectedCount} de {PlaylistItems.Count} selecionados";

    // Collapsible list: ~5 rows when collapsed, full (scrollable) when expanded.
    private bool _isPlaylistCollapsed;
    public bool IsPlaylistCollapsed
    {
        get => _isPlaylistCollapsed;
        set
        {
            if (SetProperty(ref _isPlaylistCollapsed, value))
            {
                OnPropertyChanged(nameof(PlaylistMaxHeight));
                OnPropertyChanged(nameof(PlaylistToggleLabel));
            }
        }
    }
    public double PlaylistMaxHeight => IsPlaylistCollapsed ? 268 : 520;
    public string PlaylistToggleLabel => IsPlaylistCollapsed ? "Expandir" : "Recolher";

    private void TogglePlaylistCollapsed() => IsPlaylistCollapsed = !IsPlaylistCollapsed;

    // ───────────────────────── Preview ─────────────────────────
    private string? _previewThumbnailPath;
    public string? PreviewThumbnailPath
    {
        get => _previewThumbnailPath;
        set => SetProperty(ref _previewThumbnailPath, value);
    }

    /// <summary>Webpage URL of the currently previewed item (opened when the preview is clicked).</summary>
    public string? PreviewUrl => SelectedPlaylistItem?.WebpageUrl ?? SingleVideo?.WebpageUrl;

    private bool _isPreviewCollapsed;
    public bool IsPreviewCollapsed
    {
        get => _isPreviewCollapsed;
        set
        {
            if (SetProperty(ref _isPreviewCollapsed, value))
            {
                OnPropertyChanged(nameof(PreviewColumnWidth));
                OnIsPreviewCollapsedChanged(value);
            }
        }
    }
    public GridLength PreviewColumnWidth => IsPreviewCollapsed ? new GridLength(40) : new GridLength(370);

    private void OnIsPreviewCollapsedChanged(bool value) => Settings.Ui.PreviewCollapsed = value;

    private void TogglePreviewCollapsed() => IsPreviewCollapsed = !IsPreviewCollapsed;

    private void OpenPreview()
    {
        if (!string.IsNullOrWhiteSpace(PreviewUrl))
            OpenUrl?.Invoke(PreviewUrl);
    }

    // ───────────────────────── Output template ─────────────────────────
    private string _templatePreview = "";
    public string TemplatePreview
    {
        get => _templatePreview;
        set => SetProperty(ref _templatePreview, value);
    }

    // ───────────────────────── Advanced (max duration text proxy) ─────────────────────────
    public string MaxDurationText
    {
        get => Settings.Advanced.MaxDurationMinutes is { } m
            ? TimeSpan.FromMinutes(m).ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : "";
        set
        {
            int? minutes = null;
            if (!string.IsNullOrWhiteSpace(value) && TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var ts))
                minutes = (int)ts.TotalMinutes;
            Settings.Advanced.MaxDurationMinutes = minutes;
            OnPropertyChanged();
        }
    }

    public bool ParallelWarning => Settings.Advanced.ParallelDownloads > 5;

    // ───────────────────────── Status bar / progress ─────────────────────────
    private string _statusText = "Pronto";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (SetProperty(ref _progressValue, value))
                OnPropertyChanged(nameof(TaskbarProgress));
        }
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set
        {
            if (SetProperty(ref _isIndeterminate, value))
                OnPropertyChanged(nameof(ShowProgress));
        }
    }

    /// <summary>The progress bar is only shown while something is actually running (idle = hidden).</summary>
    public bool ShowProgress => IsBusy || IsDownloading || IsIndeterminate || YtDlp.IsWorking || Ffmpeg.IsWorking;

    /// <summary>True while at least one queue request is downloading or being processed by ffmpeg.</summary>
    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
                AnalyzeGifPreviewCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(TaskbarState));
            }
        }
    }

    private string? _speedText;
    public string? SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    private string? _etaText;
    public string? EtaText
    {
        get => _etaText;
        set => SetProperty(ref _etaText, value);
    }

    /// <summary>Overall queue progress mirrored on the taskbar button (0..1).</summary>
    public double TaskbarProgress => ProgressValue / 100.0;
    public TaskbarItemProgressState TaskbarState => IsDownloading ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;

    // ───────────────────────── Tools / updates ─────────────────────────
    public ToolStatusViewModel YtDlp { get; } = new("yt-dlp");
    public ToolStatusViewModel Ffmpeg { get; } = new("ffmpeg");

    private UpdateInfo? _updateInfo;
    public UpdateInfo? UpdateInfo
    {
        get => _updateInfo;
        set => SetProperty(ref _updateInfo, value);
    }

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set => SetProperty(ref _updateAvailable, value);
    }

    // ───────────────────────── Plugins tab ─────────────────────────
    // One row per folder under plugins/, whatever its state, so a refused or broken plugin is visible and
    // can be uninstalled. The rows are plain snapshots of the catalog's InstalledPlugin objects, rebuilt on
    // PluginCatalog.Changed — few items, and the row VM keeps no state of its own. The commands above call
    // straight into the catalog; disable is immediate, uninstall only marks the folder (see PluginCatalog).

    public ObservableCollection<PluginItemViewModel> PluginItems { get; } = [];

    /// <summary>Where plugins live (plugins/ next to the executable), for the empty state and "Abrir pasta".</summary>
    public string PluginsDirectory => _plugins.PluginsDirectory;

    private void RefreshPluginItems()
    {
        PluginItems.Clear();
        foreach (var plugin in _plugins.Plugins)
            PluginItems.Add(new PluginItemViewModel(plugin));
    }

    // ───────────────────────── Debug tab ─────────────────────────
    private string _debugUrl = "";
    public string DebugUrl
    {
        get => _debugUrl;
        set => SetProperty(ref _debugUrl, value);
    }

    private string _debugOutput = "";
    public string DebugOutput
    {
        get => _debugOutput;
        set => SetProperty(ref _debugOutput, value);
    }

    private string _rawArgs = "";
    public string RawArgs
    {
        get => _rawArgs;
        set => SetProperty(ref _rawArgs, value);
    }

    private bool _debugVerbose;
    public bool DebugVerbose
    {
        get => _debugVerbose;
        set => SetProperty(ref _debugVerbose, value);
    }

    private bool _debugSkipDownload;
    public bool DebugSkipDownload
    {
        get => _debugSkipDownload;
        set => SetProperty(ref _debugSkipDownload, value);
    }

    // ───────────────────────── Logs tab ─────────────────────────
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ICollectionView LogsView { get; }

    private LogLevel _minLogLevel = LogLevel.Debug;
    public LogLevel MinLogLevel
    {
        get => _minLogLevel;
        set
        {
            if (SetProperty(ref _minLogLevel, value))
                OnMinLogLevelChanged(value);
        }
    }
    private void OnMinLogLevelChanged(LogLevel value) => LogsView.Refresh();

    // ═════════════════════════ Lifecycle ═════════════════════════
    public async Task InitializeAsync()
    {
        _service.LogEmitted += OnLogEmitted;
        UpdateTemplatePreview();

        // The pending file says which job folders belong to a processing that can resume; everything
        // else left in the staging folder is a leftover of a session that died mid-download (a clean
        // exit never leaves any): tell the user and throw it away.
        await LoadPendingJobsAsync();
        var removed = _service.PurgeStaging(keep: PreservedWorkDirs());
        if (removed > 0)
            StartupNotice = $"A sessão anterior foi interrompida com downloads em andamento — {removed} pasta(s) temporária(s) foram apagadas. O que ficou pendente pode ser retomado pela fila.";

        await RefreshToolsAsync();

        // Missing tools are fetched without asking (first run of a package that ships none); a yt-dlp
        // that was just downloaded is the latest by definition, so the update check only runs for one
        // that was already there.
        var freshYtDlp = await EnsureToolsAsync();
        if (Settings.Tools.AutoCheckUpdatesOnStartup && YtDlp.Installed && !freshYtDlp)
            _ = CheckUpdateAsync();
    }

    /// <summary>
    /// Downloads whatever is missing — yt-dlp first (small), then ffmpeg in its "Completo" build (the
    /// installer dialog's recommended pick). A failure (offline, blocked) just leaves the red dot and the
    /// manual "Instalar" button, exactly as before. Returns true when yt-dlp was downloaded now.
    /// </summary>
    private async Task<bool> EnsureToolsAsync()
    {
        bool freshYtDlp = false;
        if (!YtDlp.Installed)
        {
            StatusText = "yt-dlp não encontrado — baixando...";
            await InstallYtDlpAsync();
            freshYtDlp = YtDlp.Installed;
        }
        if (!Ffmpeg.Installed)
        {
            StatusText = "ffmpeg não encontrado — baixando...";
            await InstallFfmpegAsync(FfmpegInstallKind.Full);
        }
        return freshYtDlp;
    }

    /// <summary>
    /// Orderly shutdown: remember what was still pending (before cancelling, so interrupted downloads
    /// resume next time), stop every running job, wait briefly for their processes to die and their
    /// job folders to be deleted, then purge whatever is left in the staging folder.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _service.LogEmitted -= OnLogEmitted;
        _plugins.LogEmitted -= OnLogEmitted;
        _autoAnalyzeCts?.Cancel();
        _analyzeCts?.Cancel();
        _gifPreviewCts?.Cancel();

        // Freeze the debounced saves: the only write from here on is the final snapshot below, taken
        // after the cancellations so it reflects exactly what got cut off.
        _pendingSaveCts?.Cancel();
        _pendingFrozen = true;

        // Mark what is being interrupted (both rows of each running request) and let the service keep the
        // job folder of anything already past the download, so the processing can resume next session.
        foreach (var row in Jobs.Where(j => _runs.ContainsKey(j.RequestId)))
            row.InterruptedByShutdown = true;
        _service.KeepWorkDirsOnCancel = true;

        var runs = _runs.Values.ToList();   // cancelling can complete a job inline, which edits _runs
        foreach (var run in runs)
            run.Cts.Cancel();
        _queueArmed = false;
        if (runs.Count > 0)
        {
            var all = Task.WhenAll(runs.Select(r => r.Task));
            try { await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(4))); } catch { /* jobs report their own faults */ }
        }

        await SavePendingJobsAsync(final: true);
        var keep = Jobs.Where(j => j.Kind == JobKind.Download && IsShutdownPending(j))
                       .Select(ResumableWorkDir)
                       .Where(d => d is not null)
                       .Select(d => d!)
                       .ToList();
        _service.PurgeStaging(keep);
        await _settings.SaveAsync();
    }

    // ═════════════════════════ Analyze ═════════════════════════
    private bool CanAnalyze() => !IsBusy && !string.IsNullOrWhiteSpace(Url);

    private async Task AnalyzeAsync()
    {
        var url = Url.Trim();
        _lastAnalyzedUrl = url;
        _analyzeCts = new CancellationTokenSource();
        var cts = _analyzeCts;
        IsBusy = true;
        IsIndeterminate = true;
        StatusText = "Analisando URL...";
        try
        {
            var info = await _service.AnalyzeUrlAsync(url, TreatAsPlaylist, fetchComments: TracklistDebugReport, cts.Token);
            ApplyUrlInfo(info);
            _settings.AddRecentUrl(url, TitleOf(info));
            StatusText = "Pronto";
            if (TracklistDebugReport && info is VideoUrlInfo single)
                ReportTracklist(single.Video);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(cts, _analyzeCts))
                StatusText = "Análise cancelada.";
            _lastAnalyzedUrl = null;   // let the same URL be analysed again
        }
        catch (Exception ex)
        {
            StatusText = $"Erro: {ex.Message}";
            _lastAnalyzedUrl = null;   // a retype/Enter retries instead of being swallowed as "already analysed"
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
        }
    }

    private void ApplyUrlInfo(UrlInfo info)
    {
        DetachPlaylistHandlers();
        PlaylistItems.Clear();
        SingleVideo = null;
        PlaylistTitle = null;
        PlaylistUploader = null;
        _currentPlaylistUrl = null;
        _isImportedList = false;     // a fresh analysis replaces any imported list
        _importFolder = null;
        _importNumbered = false;

        switch (info)
        {
            case VideoUrlInfo v:
                IsPlaylist = false;
                SingleVideo = v.Video;
                _ = LoadPreviewThumbnailAsync(v.Video.Id, v.Video.ThumbnailUrl);
                break;

            case PlaylistUrlInfo p:
                IsPlaylist = true;
                PlaylistTitle = p.Playlist.Title;
                PlaylistUploader = p.Playlist.Uploader;
                _currentPlaylistUrl = info.OriginalUrl;
                int idx = 1;
                foreach (var item in p.Playlist.Items)
                {
                    var vm = new PlaylistItemViewModel(item, idx++);
                    vm.PropertyChanged += OnPlaylistItemChanged;
                    PlaylistItems.Add(vm);
                }
                SelectedPlaylistItem = PlaylistItems.FirstOrDefault();
                _ = LoadAllThumbnailsAsync();
                break;
        }

        HasResult = true;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(DownloadButtonLabel));
        UpdateTemplatePreview();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private void UseRecent(RecentUrl? recent)
    {
        if (recent is not null)
            Url = recent.Url;
    }

    // ═════════════════════════ Selection ═════════════════════════
    private void SelectAll() => SetAllSelected(true);
    private void ClearSelection() => SetAllSelected(false);

    private void InvertSelection()
    {
        foreach (var i in PlaylistItems)
            i.IsSelected = !i.IsSelected;
    }

    private void SetAllSelected(bool value)
    {
        foreach (var i in PlaylistItems)
            i.IsSelected = value;
    }

    private void OnPlaylistItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistItemViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(DownloadButtonLabel));
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private void DetachPlaylistHandlers()
    {
        foreach (var i in PlaylistItems)
            i.PropertyChanged -= OnPlaylistItemChanged;
    }

    private bool FilterPlaylistItem(object obj)
    {
        // A range command ("%[a:b]") drives selection, not visibility — keep every row showing.
        if (obj is not PlaylistItemViewModel vm || string.IsNullOrWhiteSpace(FilterText) || IsRangeCommand(FilterText))
            return true;
        var f = FilterText.Trim();
        return vm.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
               || (vm.Uploader?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ── Range selection command: "%[a:b]" selects items a..b (1-based, inclusive) and deselects the
    //    rest. Open ends ("%[5:]", "%[:10]") and a single item ("%[7]") are supported. ──
    private static readonly Regex RangeCommandRegex =
        new(@"^\s*%\[\s*(?<a>\d+)?\s*(?<colon>:)?\s*(?<b>\d+)?\s*\]\s*$", RegexOptions.Compiled);

    private static bool IsRangeCommand(string? text) => RangeCommandRegex.IsMatch(text ?? "");

    private static bool TryParseRangeCommand(string? text, out int start, out int end)
    {
        start = 0; end = 0;
        var m = RangeCommandRegex.Match(text ?? "");
        if (!m.Success)
            return false;

        bool hasA = m.Groups["a"].Success, hasColon = m.Groups["colon"].Success, hasB = m.Groups["b"].Success;
        if (hasColon)
        {
            start = hasA && int.TryParse(m.Groups["a"].Value, out var a) ? a : 1;
            end = hasB && int.TryParse(m.Groups["b"].Value, out var b) ? b : int.MaxValue;
        }
        else
        {
            if (!hasA || !int.TryParse(m.Groups["a"].Value, out var only))
                return false;              // "%[]" — nothing to select
            start = end = only;            // "%[7]" — a single item
        }

        if (start < 1) start = 1;
        if (end < start) (start, end) = (end, start);   // tolerate "%[20:5]"
        return true;
    }

    private void ApplyRangeSelection(int start, int end)
    {
        foreach (var i in PlaylistItems)
            i.IsSelected = i.Index >= start && i.Index <= end;
    }

    // ═════════════════════════ Download (enqueue) ═════════════════════════
    private bool CanDownload()
        => !IsBusy
           && YtDlp.Installed
           && Ffmpeg.Installed
           && (HasResult
               ? (!IsPlaylist || SelectedCount > 0) && !string.IsNullOrWhiteSpace(Settings.Paths.LastOutputDirectory)
               : RestoredJobCount > 0);

    /// <summary>
    /// "Baixar": turns the current analysis into queue jobs and starts the queue. With nothing analysed
    /// it is the "Continuar downloads" button: it just starts the jobs restored from the previous session.
    /// </summary>
    private void Download()
    {
        if (HasResult)
        {
            var added = EnqueueCurrentSelection();
            StatusText = added switch
            {
                0 => "Nada novo — esses itens já estão na fila.",
                1 => "1 item adicionado à fila.",
                _ => $"{added} itens adicionados à fila.",
            };
        }
        StartQueue();
    }

    private AudioOptions BuildAudioOptions()
        => new(Settings.Audio.Bitrate, Settings.Audio.EmbedThumbnail, Settings.Audio.EmbedMetadata,
               Settings.Audio.EmbedMetadata ? Settings.Audio.ExcludedMetadataFields.ToArray() : null);

    private AdvancedOptions BuildAdvancedOptions()
        => new(Settings.Advanced.Retries,
               Settings.Advanced.TimeoutSeconds,
               Settings.Advanced.MaxDurationMinutes is { } md ? TimeSpan.FromMinutes(md) : null,
               Settings.Advanced.CookiesFilePath);

    /// <summary>The video options for a list item: no GIF/trim — those only ever apply to a single video.</summary>
    private VideoOptions BuildListVideoOptions()
        => new(Settings.Video.DownloadVideo, Settings.Video.IncludeAudio, Settings.Video.ExtractAudioSeparate, Speed: Settings.Video.Speed);

    /// <summary>Builds one request per selected item and adds each to the queue. Returns how many were new.</summary>
    private int EnqueueCurrentSelection()
    {
        var audio = BuildAudioOptions();
        var advanced = BuildAdvancedOptions();
        var video = BuildListVideoOptions();
        var outDir = Settings.Paths.LastOutputDirectory!;
        var template = Settings.Paths.LastTemplate;
        int added = 0;

        if (_isImportedList)
        {
            // Imported list: every row is its own URL. Files go to the list's subfolder (the .txt name)
            // with a flat template, optionally numbered "N - " by position in the list.
            var dir = string.IsNullOrEmpty(_importFolder) ? outDir : Path.Combine(outDir, _importFolder);
            foreach (var item in PlaylistItems.Where(i => i.IsSelected))
            {
                var tpl = _importNumbered ? OutputTemplate.PrefixFileName("%(title)s.%(ext)s", $"{item.Index} - ") : "%(title)s.%(ext)s";
                var req = new DownloadRequest(item.WebpageUrl, dir, tpl, audio, advanced) { Video = video, SourceDuration = item.Duration };
                if (AddJob(req, item.Title)) added++;
            }
        }
        else if (IsPlaylist)
        {
            // Playlist: each item downloads by its own URL (no playlist re-fetch per job); the playlist
            // tokens of the template are baked in as literals so the file names come out the same.
            var perPlaylist = OutputTemplate.SubstituteLiteral(template, "playlist_title", PlaylistTitle ?? "Playlist");
            foreach (var item in PlaylistItems.Where(i => i.IsSelected))
            {
                var tpl = OutputTemplate.SubstituteLiteral(perPlaylist, "playlist_index", item.Index.ToString(CultureInfo.InvariantCulture));
                var req = new DownloadRequest(item.WebpageUrl, outDir, tpl, audio, advanced) { Video = video, SourceDuration = item.Duration };
                if (AddJob(req, item.Title)) added++;
            }
        }
        else if (SingleVideo is { } v)
        {
            var trimmedVideo = video;
            var length = v.Duration;
            if (CanTrim && ExtractGif)
            {
                var end = Math.Min(TrimStartSeconds + GifDurationSeconds, TrimMaxSeconds);
                trimmedVideo = video with
                {
                    ExtractGif = true,
                    IncludeAudio = false,       // a .gif has no audio track
                    ExtractAudioSeparate = false,
                    GifSpeed = Settings.Video.GifSpeed,
                    StartTime = TimeSpan.FromSeconds(TrimStartSeconds),
                    EndTime = TimeSpan.FromSeconds(end)
                };
                length = TimeSpan.FromSeconds(end - TrimStartSeconds);
            }
            else if (CanTrim && (TrimStartSeconds > 0 || TrimEndSeconds < TrimMaxSeconds))
            {
                // Only pass a trim window when it's actually a subset of the full video.
                trimmedVideo = video with { StartTime = TimeSpan.FromSeconds(TrimStartSeconds), EndTime = TimeSpan.FromSeconds(TrimEndSeconds) };
                length = TimeSpan.FromSeconds(TrimEndSeconds - TrimStartSeconds);
            }
            // A single video is not part of a playlist here: strip the playlist tokens instead of letting
            // yt-dlp print "NA" into the path.
            var tpl = OutputTemplate.WithoutPlaylistTokens(template);
            var req = new DownloadRequest(v.WebpageUrl, outDir, tpl, audio, advanced) { Video = trimmedVideo, SourceDuration = length };
            if (AddJob(req, v.Title)) added++;
        }

        return added;
    }

    // ═════════════════════════ Fila de downloads ═════════════════════════
    // Every request is one or two rows in Jobs: the download itself and, when the video needs our ffmpeg
    // pass, a linked "processing" row right after it. Rows of a pair share RequestId and move together.
    // The runner starts queued downloads in list order, up to Settings.Advanced.ParallelDownloads at a
    // time, and everything below runs on the UI thread (progress callbacks are marshalled by Progress<T>).

    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = [];

    private readonly Dictionary<Guid, (CancellationTokenSource Cts, Task Task)> _runs = [];
    private bool _queueArmed;   // set by "Baixar"/"Continuar"; restored jobs wait for it

    /// <summary>Rows not yet finished (queued or running) — what "Ver fila (N)" and the tab header count.</summary>
    public int PendingJobCount => Jobs.Count(j => !j.IsFinished);
    public bool HasPendingJobs => PendingJobCount > 0;
    public bool HasFinishedJobs => Jobs.Any(j => j.IsFinished);
    public string QueueTabHeader => PendingJobCount > 0 ? $"Fila ({PendingJobCount})" : "Fila";
    public string QueueButtonLabel => PendingJobCount > 0 ? $"Ver fila ({PendingJobCount})" : "Ver fila";

    /// <summary>Requests restored from the previous session that haven't been started yet.</summary>
    public int RestoredJobCount => _queueArmed ? 0 : RestoredDownloadCount + RestoredProcessingCount;
    /// <summary>Restored requests whose download still has to run (it never finished, or the download itself was the interrupted step).</summary>
    public int RestoredDownloadCount => _queueArmed ? 0 : Jobs.Count(j => j.Kind == JobKind.Download && j.IsRestored && j.IsQueued);
    /// <summary>Restored requests whose download is already on disk — only the interrupted ffmpeg pass is left.</summary>
    public int RestoredProcessingCount => _queueArmed ? 0 : Jobs.Count(j => j.Kind == JobKind.Download && j.IsRestored && j.IsResumableProcessing);
    public bool HasRestoredJobs => RestoredJobCount > 0;

    /// <summary>Banner text: says which step was interrupted, because a pending re-encode is a very different wait from a pending download.</summary>
    public string RestoredBanner
    {
        get
        {
            var d = RestoredDownloadCount;
            var p = RestoredProcessingCount;
            var downloads = d == 1 ? "1 download pendente" : $"{d} downloads pendentes";
            var processing = p == 1 ? "1 processamento pendente" : $"{p} processamentos pendentes";
            if (p == 0) return $"{downloads} da sessão anterior.";
            if (d == 0) return $"{processing} da sessão anterior — o download já foi feito, falta só o ffmpeg.";
            return $"{downloads} e {processing} da sessão anterior.";
        }
    }

    /// <summary>Label of the main button while it means "resume what the previous session left".</summary>
    private string ContinueLabel => (RestoredDownloadCount, RestoredProcessingCount) switch
    {
        (> 0, 0) => $"Continuar downloads ({RestoredJobCount})",
        (0, > 0) => $"Continuar processamento ({RestoredJobCount})",
        _ => $"Continuar pendências ({RestoredJobCount})",
    };

    private string? _startupNotice;
    /// <summary>Dismissable banner shown when the previous session left temp files behind.</summary>
    public string? StartupNotice
    {
        get => _startupNotice;
        set => SetProperty(ref _startupNotice, value);
    }

    /// <summary>"Manter pendências entre sessões" — mirrors the persisted setting and (un)writes the file at once.</summary>
    public bool QueuePersistent
    {
        get => Settings.Ui.QueuePersistent;
        set
        {
            if (Settings.Ui.QueuePersistent == value)
                return;
            Settings.Ui.QueuePersistent = value;   // persisted (debounced) by SettingsService
            OnPropertyChanged();
            SchedulePendingSave();
        }
    }

    /// <summary>A request is pending while either of its rows is unfinished (the download row is already "Baixado" during the ffmpeg pass).</summary>
    private static bool IsRequestPending(DownloadJobViewModel download)
        => !download.IsFinished || download.Partner is { IsFinished: false };

    /// <summary>The runner can start this request: a queued download, or a restored one whose download is done and only the ffmpeg pass is waiting.</summary>
    private static bool IsRequestStartable(DownloadJobViewModel download)
        => download.IsQueued || download.IsResumableProcessing;

    /// <summary>
    /// Adds a request to the queue. <paramref name="resumeWorkDir"/> is the preserved job folder of a
    /// processing interrupted last session: the download row comes in already done and only the
    /// processing row waits. Returns false when the same request is already pending.
    /// </summary>
    private bool AddJob(DownloadRequest request, string title, bool restored = false, string? resumeWorkDir = null)
    {
        // Same URL to the same place with the same name, still pending → not a new job.
        if (Jobs.Any(j => j.Kind == JobKind.Download && IsRequestPending(j)
                          && string.Equals(j.Request.Url, request.Url, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(j.Request.OutputDirectory, request.OutputDirectory, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(j.Request.OutputTemplate, request.OutputTemplate, StringComparison.Ordinal)))
            return false;

        var id = Guid.NewGuid();
        var download = new DownloadJobViewModel(id, JobKind.Download, title, request, restored);
        Jobs.Add(download);
        if (request.Video.NeedsFfmpegPass)
        {
            var processing = new DownloadJobViewModel(id, JobKind.Processing, title, request, restored);
            download.Partner = processing;
            processing.Partner = download;
            Jobs.Add(processing);
            if (resumeWorkDir is not null)
            {
                download.WorkDirectory = resumeWorkDir;
                download.MarkDone("Baixado na sessão anterior");
                processing.StatusText = "Processamento interrompido na sessão anterior — retoma daqui, sem baixar de novo";
            }
        }
        SchedulePendingSave();
        return true;
    }

    /// <summary>Arms the runner and starts as many queued downloads as the parallelism allows.</summary>
    private void StartQueue()
    {
        _queueArmed = true;
        PumpQueue();
    }

    private void PumpQueue()
    {
        if (_queueArmed)
        {
            var maxParallel = Math.Clamp(Settings.Advanced.ParallelDownloads, 1, 10);
            while (_runs.Count < maxParallel)
            {
                var next = Jobs.FirstOrDefault(j => j.Kind == JobKind.Download && IsRequestStartable(j));
                if (next is null)
                    break;
                StartJob(next);
            }
            if (_runs.Count == 0)
                _queueArmed = false;   // drained — the next "Baixar" arms it again
        }
        RefreshQueueState();
    }

    private void StartJob(DownloadJobViewModel job)
    {
        var cts = new CancellationTokenSource();
        if (job.IsResumableProcessing)
        {
            // The download is already on disk: the processing row is the one that runs.
            job.Partner!.MarkActive("Retomando o processamento...");
            job.Partner.Percent = 0;
            job.Partner.IsIndeterminate = true;
        }
        else
        {
            job.MarkActive("Iniciando...");
            job.Percent = 0;
            // A trimmed section is fetched by yt-dlp's ffmpeg downloader, which reports no percentage:
            // show activity instead of a bar stuck at 0% (a real progress line switches it back).
            job.IsIndeterminate = job.Request.Video.StartTime is not null || job.Request.Video.EndTime is not null;
        }
        var task = RunJobAsync(job, cts);
        _runs[job.RequestId] = (cts, task);
    }

    private async Task RunJobAsync(DownloadJobViewModel job, CancellationTokenSource cts)
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            ApplyJobProgress(job, p);
            RefreshQueueProgress();
        });
        try
        {
            var result = await _service.DownloadAsync(job.Request, progress, cts.Token);
            if (result.Success)
            {
                job.MarkDone(job.Partner is null ? "Concluído" : "Baixado");
                job.Partner?.MarkDone();
            }
            else
            {
                // Once the download row is done, whatever went wrong belongs to the processing row.
                if (job.Status == JobStatus.Done && job.Partner is { } proc)
                    proc.MarkFailed(result.ErrorMessage);
                else
                {
                    job.MarkFailed(result.ErrorMessage);
                    job.Partner?.MarkCancelled("Não executado (o download falhou)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (job.Status != JobStatus.Done)
                job.MarkCancelled(job.InterruptedByShutdown ? "Download interrompido ao fechar o app" : "Cancelado");
            if (job.Partner is { IsFinished: false } partner)
                partner.MarkCancelled(partner.InterruptedByShutdown ? "Processamento interrompido ao fechar o app" : "Cancelado");
        }
        catch (Exception ex)
        {
            if (job.Status == JobStatus.Done && job.Partner is { } proc)
                proc.MarkFailed(ex.Message);
            else
            {
                job.MarkFailed(ex.Message);
                job.Partner?.MarkCancelled("Não executado (o download falhou)");
            }
        }
        finally
        {
            _runs.Remove(job.RequestId);
            cts.Dispose();
            SchedulePendingSave();
            PumpQueue();
        }
    }

    private void ApplyJobProgress(DownloadJobViewModel job, DownloadProgress p)
    {
        // The job folder becomes known when the ffmpeg pass starts — remember it (and persist it) so an
        // abrupt end during the pass can resume from the downloaded file instead of downloading again.
        if (p.WorkDirectory is not null && !string.Equals(job.WorkDirectory, p.WorkDirectory, StringComparison.OrdinalIgnoreCase))
        {
            job.WorkDirectory = p.WorkDirectory;
            SchedulePendingSave();
        }

        switch (p.Stage)
        {
            case DownloadStage.Processing:
                // yt-dlp is done; our ffmpeg pass owns the linked row from here on.
                if (!job.IsFinished)
                    job.MarkDone("Baixado");
                if (job.Partner is { } proc)
                {
                    if (!proc.IsActive)
                        proc.MarkActive("Processando");
                    proc.IsIndeterminate = p.IsIndeterminate;
                    proc.Percent = p.PercentDone;
                    proc.StatusText = p.IsIndeterminate
                        ? (p.Detail ?? "Processando (ffmpeg)")
                        : $"{p.Detail ?? "Processando (ffmpeg)"} — {p.PercentDone:0}%";
                }
                break;

            case DownloadStage.Done:
            case DownloadStage.Failed:
            case DownloadStage.Cancelled:
                break;   // the final state comes from the DownloadResult / exception in RunJobAsync

            default:
                if (job.IsFinished)
                    break;
                job.IsIndeterminate = false;
                job.Percent = p.PercentDone;
                job.SpeedText = p.SpeedText;
                job.EtaText = p.EtaText;
                job.StatusText = p.Stage switch
                {
                    DownloadStage.Converting => "Convertendo MP3",
                    DownloadStage.EmbeddingMetadata => "Aplicando metadados",
                    _ => p.SpeedText is null
                        ? $"Baixando — {p.PercentDone:0}%"
                        : $"Baixando — {p.PercentDone:0}% · {p.SpeedText}{(p.EtaText is null ? "" : $" · ETA {p.EtaText}")}",
                };
                break;
        }
    }

    /// <summary>Recomputes every aggregate the UI shows for the queue (counts, labels, status bar, taskbar).</summary>
    private void RefreshQueueState()
    {
        var wasDownloading = IsDownloading;
        IsDownloading = _runs.Count > 0;
        RefreshQueueProgress();

        if (wasDownloading && !IsDownloading)
        {
            var run = Jobs.Where(j => j.Kind == JobKind.Download && !j.IsSettled).ToList();
            var ok = run.Count(j => j.Status == JobStatus.Done);
            var failed = run.Count(j => j.Status == JobStatus.Failed);
            StatusText = failed == 0 ? $"Fila concluída — {ok} ok." : $"Fila concluída — {ok} ok, {failed} falha(s).";
            // Everything that finished belongs to the run that just ended — the next run's progress starts fresh.
            foreach (var j in Jobs.Where(j => j.IsFinished))
                j.IsSettled = true;
        }

        OnPropertyChanged(nameof(PendingJobCount));
        OnPropertyChanged(nameof(HasPendingJobs));
        OnPropertyChanged(nameof(HasFinishedJobs));
        OnPropertyChanged(nameof(QueueTabHeader));
        OnPropertyChanged(nameof(QueueButtonLabel));
        OnPropertyChanged(nameof(RestoredJobCount));
        OnPropertyChanged(nameof(RestoredDownloadCount));
        OnPropertyChanged(nameof(RestoredProcessingCount));
        OnPropertyChanged(nameof(HasRestoredJobs));
        OnPropertyChanged(nameof(RestoredBanner));
        OnPropertyChanged(nameof(DownloadButtonLabel));
        DownloadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearFinishedJobsCommand.NotifyCanExecuteChanged();
        CancelAllJobsCommand.NotifyCanExecuteChanged();
        ContinuePendingCommand.NotifyCanExecuteChanged();
        DiscardPendingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The cheap part, run on every progress line: overall percentage (taskbar + status bar), the
    /// "N baixando · M processando · K na fila" summary and the lead download's speed/ETA.
    /// </summary>
    private void RefreshQueueProgress()
    {
        if (!IsDownloading)
        {
            ProgressValue = 0;
            SpeedText = null;
            EtaText = null;
            QueueSummary = null;
            return;
        }

        var downloads = Jobs.Where(j => j.Kind == JobKind.Download && !j.IsSettled).ToList();
        var active = downloads.Count(j => j.IsActive);
        var processing = Jobs.Count(j => j.IsActive && j.Kind == JobKind.Processing);
        var queued = downloads.Count(j => j.IsQueued);

        // Overall progress over this run: each request weighs the same; a pair splits its weight between its two rows.
        double sum = 0;
        foreach (var d in downloads)
        {
            var share = d.Partner is null ? 1.0 : 0.5;
            sum += RowFraction(d) * share;
            if (d.Partner is { } p) sum += RowFraction(p) * share;
        }
        ProgressValue = downloads.Count == 0 ? 0 : sum * 100.0 / downloads.Count;
        IsIndeterminate = false;

        var parts = new List<string>();
        if (active > 0) parts.Add(active == 1 ? "1 baixando" : $"{active} baixando");
        if (processing > 0) parts.Add(processing == 1 ? "1 processando" : $"{processing} processando");
        if (queued > 0) parts.Add(queued == 1 ? "1 na fila" : $"{queued} na fila");
        QueueSummary = string.Join(" · ", parts);

        var lead = downloads.FirstOrDefault(j => j.IsActive);
        SpeedText = lead?.SpeedText;
        EtaText = lead?.EtaText;

        static double RowFraction(DownloadJobViewModel j) => j.Status switch
        {
            JobStatus.Done => 1.0,
            JobStatus.Active => j.IsIndeterminate ? 0.5 : Math.Clamp(j.Percent, 0, 100) / 100.0,
            _ => 0.0,
        };
    }

    private string? _queueSummary;
    /// <summary>Status-bar summary of what the queue is doing right now (null when idle).</summary>
    public string? QueueSummary
    {
        get => _queueSummary;
        set => SetProperty(ref _queueSummary, value);
    }

    private bool CanCancel() => IsDownloading;

    /// <summary>"Cancelar" on the main page: stops the first running item in list order (and its linked row).</summary>
    private void CancelTopJob()
    {
        var top = Jobs.FirstOrDefault(j => j.IsActive);
        if (top is null)
            return;
        CancelRequest(top.RequestId);
        StatusText = $"Cancelando: {top.Title}";
    }

    private void CancelRequest(Guid requestId)
    {
        if (_runs.TryGetValue(requestId, out var run))
            run.Cts.Cancel();   // RunJobAsync marks the rows; the service deletes the job's temp folder
    }

    private void CancelAllJobs()
    {
        _queueArmed = false;
        // A resumable processing that never started keeps a job folder on disk — gone with the cancel.
        var resumable = Jobs.Where(j => j.Kind == JobKind.Download && j.IsResumableProcessing).ToList();
        foreach (var job in Jobs.Where(j => j.IsQueued).ToList())
            job.MarkCancelled();
        foreach (var download in resumable)
            DropPreservedWorkDir(download);
        foreach (var run in _runs.Values.ToList())
            run.Cts.Cancel();
        SchedulePendingSave();
        RefreshQueueState();
        StatusText = "Cancelando tudo...";
    }

    private void ClearFinishedJobs()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished).ToList())
            Jobs.Remove(job);
        RefreshQueueState();
    }

    /// <summary>Row button: cancels a running request, removes a queued/finished one (both rows of the pair).</summary>
    private void JobAction(DownloadJobViewModel? job)
    {
        if (job is null)
            return;
        if (job.IsActive || job.Partner is { IsActive: true })
        {
            CancelRequest(job.RequestId);
            return;
        }
        RemoveRequest(job.RequestId);
        SchedulePendingSave();
        RefreshQueueState();
    }

    /// <summary>Takes both rows of a request out of the list, deleting a preserved job folder it never got to use.</summary>
    private void RemoveRequest(Guid requestId)
    {
        foreach (var row in Jobs.Where(j => j.RequestId == requestId).ToList())
        {
            if (row.Kind == JobKind.Download && row.IsResumableProcessing)
                DropPreservedWorkDir(row);
            Jobs.Remove(row);
        }
    }

    private void DropPreservedWorkDir(DownloadJobViewModel download)
    {
        var dir = download.WorkDirectory ?? download.Request.ResumeWorkDirectory;
        download.WorkDirectory = null;
        if (dir is not null)
            _service.DeleteWorkDirectory(dir);
    }

    /// <summary>
    /// "Tentar de novo" on a failed/cancelled row: the request goes back to the queue as new (both rows)
    /// and the runner is armed. A resumable processing whose folder is gone simply downloads again — the
    /// service falls back on its own when the folder has no video.
    /// </summary>
    private void RetryJob(DownloadJobViewModel? job)
    {
        if (job is null || job.IsRequestActive)
            return;
        foreach (var row in Jobs.Where(j => j.RequestId == job.RequestId))
            row.ResetForRetry();
        StartQueue();
        StatusText = $"Tentando de novo: {job.Title}";
    }

    /// <summary>
    /// Drag-and-drop reorder: moves the dragged row (with its linked row, as one block) so it lands just
    /// before or after <paramref name="target"/>. A block never gets inserted between the two rows of
    /// another pair — it snaps to that pair's outer edge instead.
    /// </summary>
    public void MoveJob(DownloadJobViewModel dragged, DownloadJobViewModel target, bool insertAfter)
    {
        if (ReferenceEquals(dragged, target) || dragged.RequestId == target.RequestId)
            return;

        var block = Jobs.Where(j => j.RequestId == dragged.RequestId).ToList();
        foreach (var row in block)
            Jobs.Remove(row);

        var targetBlock = Jobs.Where(j => j.RequestId == target.RequestId).ToList();
        var index = insertAfter
            ? Jobs.IndexOf(targetBlock[^1]) + 1
            : Jobs.IndexOf(targetBlock[0]);

        foreach (var row in block)
            Jobs.Insert(index++, row);

        SchedulePendingSave();
        RefreshQueueState();
    }

    private void DiscardRestoredJobs()
    {
        foreach (var download in Jobs.Where(j => j.Kind == JobKind.Download && j.IsRestored && IsRequestStartable(j)).ToList())
            RemoveRequest(download.RequestId);
        SchedulePendingSave();
        RefreshQueueState();
    }

    /// <summary>Job folders that must survive a staging purge: the ones a not-yet-started resumable processing points at.</summary>
    private IReadOnlyList<string> PreservedWorkDirs()
        => Jobs.Where(j => j.Kind == JobKind.Download && j.IsResumableProcessing)
               .Select(j => j.WorkDirectory ?? j.Request.ResumeWorkDirectory!)
               .ToList();

    // ───── Persistence: pending-downloads.txt ─────
    private readonly string _pendingPath = PendingQueueFile.DefaultPath;
    private CancellationTokenSource? _pendingSaveCts;
    private bool _pendingFrozen;   // set at shutdown: only the final snapshot (after the cancellations) may write

    /// <summary>Debounced rewrite of the pending file (queued + running downloads) — or its removal.</summary>
    private void SchedulePendingSave()
    {
        if (_pendingFrozen)
            return;
        _pendingSaveCts?.Cancel();
        _pendingSaveCts = new CancellationTokenSource();
        var token = _pendingSaveCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { return; }
            await _dispatcher.InvokeAsync(async () =>
            {
                if (!token.IsCancellationRequested)
                    await SavePendingJobsAsync();
            });
        });
    }

    /// <summary>
    /// Writes what still has to run. While the app is up that is every unfinished request; at shutdown
    /// (<paramref name="final"/>) it is what the cancellations left behind: queued requests, downloads that
    /// were cut off (they download again), and processings that were cut off — those carry the preserved
    /// job folder so the next session skips the download.
    /// </summary>
    private async Task SavePendingJobsAsync(bool final = false)
    {
        if (_pendingFrozen && !final)
            return;
        try
        {
            if (!QueuePersistent)
            {
                PendingQueueFile.Delete(_pendingPath);
                return;
            }
            var entries = Jobs
                .Where(j => j.Kind == JobKind.Download && (final ? IsShutdownPending(j) : IsRequestPending(j)))
                .Select(j => new PendingDownload(
                    j.Request.Url, j.Title, j.Request.OutputDirectory, j.Request.OutputTemplate,
                    j.Request.Audio, j.Request.Video, j.Request.SourceDuration?.TotalSeconds,
                    ResumableWorkDir(j)))
                .ToList();
            await PendingQueueFile.SaveAsync(_pendingPath, entries);
        }
        catch (Exception ex)
        {
            _logger.Log(LogEntry.Now(LogLevel.Warning, "Core", $"Falha ao salvar {PendingQueueFile.FileName}: {ex.Message}"));
        }
    }

    /// <summary>After the shutdown cancellations: what the next session should pick up.</summary>
    private static bool IsShutdownPending(DownloadJobViewModel d)
        => d.IsQueued                                                                     // never started
           || (d.InterruptedByShutdown && d.Status == JobStatus.Cancelled)                // download cut off → downloads again
           || (d.Status == JobStatus.Done && d.Partner is { IsQueued: true })             // restored resume, never started
           || (d.Status == JobStatus.Done && d.Partner is { InterruptedByShutdown: true, Status: JobStatus.Cancelled });   // processing cut off → resumes

    /// <summary>The job folder to resume from — only when the download is done, the pass isn't, and the folder is really there.</summary>
    private static string? ResumableWorkDir(DownloadJobViewModel d)
    {
        if (d.Status != JobStatus.Done || d.Partner is null || d.Partner.Status == JobStatus.Done)
            return null;
        var dir = d.WorkDirectory ?? d.Request.ResumeWorkDirectory;
        return dir is not null && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Loads the previous session's pending downloads as queued (not started) jobs.</summary>
    private async Task LoadPendingJobsAsync()
    {
        IReadOnlyList<PendingDownload> entries;
        try { entries = await PendingQueueFile.LoadAsync(_pendingPath); }
        catch (Exception ex)
        {
            _logger.Log(LogEntry.Now(LogLevel.Warning, "Core", $"Falha ao ler {PendingQueueFile.FileName}: {ex.Message}"));
            return;
        }
        if (entries.Count == 0)
            return;

        var advanced = BuildAdvancedOptions();
        int resumed = 0;
        foreach (var e in entries)
        {
            // A hand-added bare URL has no options of its own: use the current settings, with a flat
            // file name (the current template may carry playlist tokens that mean nothing here).
            var video = e.Video ?? BuildListVideoOptions();
            // A preserved job folder means the download finished and only the ffmpeg pass is pending —
            // but only if it is still on disk and the options still call for a pass.
            var resumeDir = e.WorkDirectory is { Length: > 0 } wd && video.NeedsFfmpegPass && Directory.Exists(wd) ? wd : null;
            var req = new DownloadRequest(
                e.Url,
                e.OutputDirectory ?? Settings.Paths.LastOutputDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                e.OutputTemplate ?? "%(title)s.%(ext)s",
                e.Audio ?? BuildAudioOptions(),
                advanced)
            {
                Video = video,
                SourceDuration = e.SourceDurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
                ResumeWorkDirectory = resumeDir,
            };
            var title = string.IsNullOrWhiteSpace(e.Title) ? e.Url : e.Title;
            if (AddJob(req, title, restored: true, resumeWorkDir: resumeDir))
            {
                if (resumeDir is not null)
                    resumed++;
                else if (e.WorkDirectory is not null)
                {
                    // The pass was the interrupted step, but its folder is gone: be explicit about it.
                    var row = Jobs.Last(j => j.Kind == JobKind.Download && ReferenceEquals(j.Request, req));
                    row.StatusText = "Processamento interrompido na sessão anterior; o arquivo baixado se perdeu — vai baixar de novo";
                }
            }
        }
        RefreshQueueState();
        _logger.Log(LogEntry.Now(LogLevel.Info, "Core",
            $"{entries.Count} pendência(s) restaurada(s) de {PendingQueueFile.FileName}" + (resumed > 0 ? $" ({resumed} retomando só o processamento)." : ".")));
    }

    // ═════════════════════════ Import (.txt / pasted links) ═════════════════════════
    private bool CanImport()
        => !IsBusy
           && YtDlp.Installed
           && Ffmpeg.Installed
           && !string.IsNullOrWhiteSpace(Settings.Paths.LastOutputDirectory);

    /// <summary>
    /// Imports a .txt (1 URL per line) and populates the same checkbox list as a playlist would, so the
    /// user can review/select and then press Download (which queues the selected distinct URLs). The first
    /// comment line "# Download as mp3|mp4" seeds the mode (mp3 = audio only, mp4 = video+audio) and the file
    /// name becomes the output subfolder. Comment (#) and blank lines are ignored.
    /// </summary>
    private async Task ImportQueueAsync()
    {
        var path = PickQueueFile?.Invoke();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path); }
        catch (Exception ex) { StatusText = $"Erro lendo .txt: {ex.Message}"; return; }

        var (asVideo, urls) = ParseQueueFile(lines);
        if (urls.Count == 0)
        {
            StatusText = "O .txt não tem nenhuma URL.";
            return;
        }

        var folder = SanitizeFolderName(Path.GetFileNameWithoutExtension(path));

        // Seed the mode from the directive; the user can still tweak the video options before downloading.
        // Go through the VM property (not Settings.Video directly) so the "Baixar vídeo" checkbox and the
        // video-options section — both bound to this VM property — reflect the imported mode.
        DownloadVideo = asVideo;
        if (asVideo)
            Settings.Video.IncludeAudio = true;

        await ImportUrlListAsync(urls, folder, numbered: false, listTitle: folder);
    }

    /// <summary>"Colar links": the pasted URLs become a reviewable list, straight into the output folder.</summary>
    private async Task PasteLinksAsync()
    {
        if (PickPastedLinks?.Invoke() is not { } pasted || pasted.Urls.Count == 0)
            return;
        await ImportUrlListAsync(pasted.Urls, importFolder: null, numbered: pasted.Numbered, listTitle: "Links colados");
    }

    /// <summary>
    /// Resolves each URL (a few in parallel, list order preserved) into the checkbox list. The download
    /// button then queues the selected rows as individual jobs.
    /// </summary>
    private async Task ImportUrlListAsync(IReadOnlyList<string> urls, string? importFolder, bool numbered, string listTitle)
    {
        _analyzeCts?.Cancel();
        _analyzeCts = new CancellationTokenSource();
        var ct = _analyzeCts.Token;
        IsBusy = true;
        IsIndeterminate = true;
        try
        {
            DetachPlaylistHandlers();
            PlaylistItems.Clear();
            SingleVideo = null;
            _currentPlaylistUrl = null;
            _isImportedList = true;
            _importFolder = importFolder;
            _importNumbered = numbered;
            IsPlaylist = true;
            PlaylistTitle = listTitle;
            PlaylistUploader = null;

            int done = 0;
            StatusText = $"Analisando 0 de {urls.Count}...";
            using var gate = new SemaphoreSlim(3);
            var tasks = urls.Select(async url =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var info = await ResolveVideoInfoAsync(url, ct);
                    var n = Interlocked.Increment(ref done);
                    if (n < urls.Count)   // the last one is followed by the "pronta" status below — don't race it
                        _ = _dispatcher.BeginInvoke(() => StatusText = $"Analisando {n} de {urls.Count}...");
                    return info;
                }
                finally { gate.Release(); }
            }).ToList();
            var infos = await Task.WhenAll(tasks);

            int idx = 1;
            foreach (var raw in infos)
            {
                ct.ThrowIfCancellationRequested();
                var info = numbered ? raw with { Title = $"{idx} - {raw.Title}" } : raw;
                var vm = new PlaylistItemViewModel(info, idx++);
                vm.PropertyChanged += OnPlaylistItemChanged;
                PlaylistItems.Add(vm);
            }

            SelectedPlaylistItem = PlaylistItems.FirstOrDefault();
            HasResult = true;
            _ = LoadAllThumbnailsAsync();
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(DownloadButtonLabel));
            UpdateTemplatePreview();
            DownloadCommand.NotifyCanExecuteChanged();
            StatusText = $"Lista '{listTitle}' pronta — {PlaylistItems.Count} itens";
        }
        catch (OperationCanceledException) { StatusText = "Importação cancelada."; }
        catch (Exception ex) { StatusText = $"Erro na importação: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
        }
    }

    /// <summary>Resolves one URL to a <see cref="VideoInfo"/> for the list; falls back to the raw URL on failure.</summary>
    private async Task<VideoInfo> ResolveVideoInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _service.AnalyzeUrlAsync(url, treatAsPlaylist: false, fetchComments: false, ct) switch
            {
                VideoUrlInfo v => v.Video,
                PlaylistUrlInfo p => p.Playlist.Items.FirstOrDefault() ?? FallbackVideo(url),
                _ => FallbackVideo(url),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return FallbackVideo(url); }
    }

    private static VideoInfo FallbackVideo(string url) => new("", url, null, null, null, url);

    /// <summary>Reads the "# Download as mp3|mp4" directive and the URL lines from an imported .txt.</summary>
    private static (bool asVideo, List<string> urls) ParseQueueFile(IEnumerable<string> lines)
    {
        bool asVideo = false, modeFound = false;
        var urls = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#'))
            {
                if (!modeFound)
                {
                    var m = Regex.Match(line, @"download\s+as\s+(mp4|mp3)", RegexOptions.IgnoreCase);
                    if (m.Success) { asVideo = m.Groups[1].Value.Equals("mp4", StringComparison.OrdinalIgnoreCase); modeFound = true; }
                }
                continue;   // comments never become URLs
            }
            urls.Add(line);
        }
        return (asVideo, urls);
    }

    private static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "playlist";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    // ═════════════════════════ Tools / updates ═════════════════════════
    public async Task RefreshToolsAsync()
    {
        try
        {
            var yt = await _service.CheckYtDlpAsync();
            var ff = await _service.CheckFfmpegAsync();
            YtDlp.Update(yt);
            Ffmpeg.Update(ff);
        }
        catch { /* leave indicators as-is */ }
        finally
        {
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task InstallYtDlpAsync()
    {
        YtDlp.IsWorking = true;
        IsIndeterminate = false;
        try
        {
            var progress = new Progress<double>(v => { ProgressValue = v; StatusText = $"Baixando yt-dlp... {v:0}%"; });
            await _service.DownloadYtDlpAsync(progress, CancellationToken.None);
            StatusText = "yt-dlp instalado.";
        }
        catch (Exception ex) { StatusText = $"Erro ao instalar yt-dlp: {ex.Message}"; }
        finally { YtDlp.IsWorking = false; ProgressValue = 0; await RefreshToolsAsync(); }
    }

    /// <summary>The "Instalar" button: asks which build, then installs it.</summary>
    private async Task InstallFfmpegAsync()
    {
        var kind = ChooseFfmpegKind is null ? FfmpegInstallKind.Full : await ChooseFfmpegKind();
        if (kind is not null)
            await InstallFfmpegAsync(kind.Value);
    }

    private async Task InstallFfmpegAsync(FfmpegInstallKind kind)
    {
        Ffmpeg.IsWorking = true;
        IsIndeterminate = false;
        try
        {
            var progress = new Progress<double>(v => { ProgressValue = v; StatusText = $"Baixando ffmpeg... {v:0}%"; });
            await _service.DownloadFfmpegAsync(kind, progress, CancellationToken.None);
            StatusText = "ffmpeg instalado.";
        }
        catch (Exception ex) { StatusText = $"Erro ao instalar ffmpeg: {ex.Message}"; }
        finally { Ffmpeg.IsWorking = false; ProgressValue = 0; await RefreshToolsAsync(); }
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            UpdateInfo = await _service.CheckYtDlpUpdateAsync();
            UpdateAvailable = UpdateInfo.UpdateAvailable;
            Settings.Tools.LastUpdateCheck = DateTimeOffset.Now;
            StatusText = UpdateAvailable
                ? $"Atualização disponível: {UpdateInfo.LatestVersion}"
                : "yt-dlp está atualizado.";
        }
        catch (Exception ex) { StatusText = $"Falha ao checar atualização: {ex.Message}"; }
    }

    private async Task UpdateYtDlpAsync()
    {
        YtDlp.IsWorking = true;
        try
        {
            var progress = new Progress<double>(v => { ProgressValue = v; StatusText = $"Atualizando yt-dlp... {v:0}%"; });
            await _service.UpdateYtDlpAsync(progress, CancellationToken.None);
            UpdateAvailable = false;
            StatusText = "yt-dlp atualizado.";
        }
        catch (Exception ex) { StatusText = $"Erro ao atualizar: {ex.Message}"; }
        finally { YtDlp.IsWorking = false; ProgressValue = 0; await RefreshToolsAsync(); }
    }

    // ═════════════════════════ Pickers / open ═════════════════════════
    private void BrowseOutputDirectory()
    {
        var chosen = PickFolder?.Invoke(Settings.Paths.LastOutputDirectory);
        if (!string.IsNullOrEmpty(chosen))
        {
            Settings.Paths.LastOutputDirectory = chosen;
            UpdateTemplatePreview();
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private void BrowseCookiesFile()
    {
        var chosen = PickCookiesFile?.Invoke();
        if (!string.IsNullOrEmpty(chosen))
            Settings.Advanced.CookiesFilePath = chosen;
    }

    private void ClearCookies() => Settings.Advanced.CookiesFilePath = null;

    private void OpenLogsFolder() => OpenFolderPath?.Invoke(_logger.LogDirectory);

    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(Settings.Paths.LastOutputDirectory))
            OpenFolderPath?.Invoke(Settings.Paths.LastOutputDirectory);
    }

    private void OpenReleasePage()
    {
        if (UpdateInfo is { ReleaseUrl: { Length: > 0 } url })
            OpenUrl?.Invoke(url);
    }

    // ═════════════════════════ Debug commands ═════════════════════════
    private Task Simulate() => RunDebugAsync(["--simulate", DebugUrl]);
    private Task GetFilename() => RunDebugAsync(["--get-filename", "-o", Settings.Paths.LastTemplate, DebugUrl]);
    private Task GetTitle() => RunDebugAsync(["--get-title", DebugUrl]);
    private Task GetDuration() => RunDebugAsync(["--get-duration", DebugUrl]);
    private Task GetThumbnail() => RunDebugAsync(["--get-thumbnail", DebugUrl]);
    private Task GetDirectUrl() => RunDebugAsync(["--get-url", DebugUrl]);
    private Task GetId() => RunDebugAsync(["--get-id", DebugUrl]);
    private Task ListFormats() => RunDebugAsync(["-F", DebugUrl]);
    private Task DumpJson() => RunDebugAsync(["--dump-json", DebugUrl]);

    private Task RunRaw()
    {
        var parsed = SplitArguments(RawArgs);
        return parsed.Length == 0 ? Task.CompletedTask : RunDebugAsync(parsed, applyToggles: false);
    }

    private void ClearOutput() => DebugOutput = "";
    private void CopyOutput() => CopyToClipboard?.Invoke(DebugOutput);

    private async Task RunDebugAsync(string[] baseArgs, bool applyToggles = true)
    {
        var args = baseArgs.ToList();
        if (applyToggles)
        {
            if (DebugVerbose) args.Add("-v");
            if (DebugSkipDownload) args.Add("--skip-download");
        }
        AppendDebug($"$ yt-dlp {string.Join(' ', args)}");
        try
        {
            var result = await _service.RunRawAsync([.. args], CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(result.Stdout)) AppendDebug(result.Stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(result.Stderr)) AppendDebug(result.Stderr.TrimEnd());
            AppendDebug($"[exit {result.ExitCode}]\n");
        }
        catch (Exception ex)
        {
            AppendDebug($"[erro] {ex.Message}\n");
        }
    }

    private void AppendDebug(string text) => DebugOutput += text + Environment.NewLine;

    // ═════════════════════════ Helpers ═════════════════════════
    private void OnToolStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolStatusViewModel.IsWorking))
            OnPropertyChanged(nameof(ShowProgress));
    }

    private void OnPathsSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateTemplatePreview();
        if (e.PropertyName is nameof(PathSettings.LastTemplate) or null)
        {
            OnPropertyChanged(nameof(SelectedOrganization));
            OnPropertyChanged(nameof(IsCustomTemplate));
            SyncTokensFromTemplate();   // no-op while we're the ones rewriting the template
        }
    }

    private void OnAdvancedSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdvancedSettings.ParallelDownloads))
        {
            OnPropertyChanged(nameof(ParallelWarning));
            PumpQueue();   // a higher limit can start more queued jobs right away
        }
        if (e.PropertyName == nameof(AdvancedSettings.MaxDurationMinutes))
            OnPropertyChanged(nameof(MaxDurationText));
        if (e.PropertyName == nameof(AdvancedSettings.CookiesFilePath))
            OnPropertyChanged(nameof(Settings));
    }

    private void OnAudioSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // "Embutir metadados (ID3)" in the advanced options is the same switch as the preview panel's master.
        if (e.PropertyName == nameof(AudioSettings.EmbedMetadata))
            SyncMetadataItemsFromSettings();
    }

    // ───────────────────────── Tracklist (detecção na análise) ─────────────────────────
    // With the switch on, every single-video analysis also fetches the top comments (+1-2 s) and runs the
    // heuristic tracklist detector over the description, the pinned comment and the others, dumping a
    // report (URL / title / what was found) into the debug tab — how the heuristic was tuned against real
    // videos. Off until the detector becomes the 1.6 plugin: nothing shows the result yet, so nobody pays
    // for the comments. Flip it to tune against a new video.
    private const bool TracklistDebugReport = false;

    /// <summary>What the detector found for the last analysed single video (null until one is analysed).</summary>
    public TracklistReport? LastTracklistReport { get; private set; }

    private void ReportTracklist(VideoInfo video)
    {
        try
        {
            LastTracklistReport = TracklistExtractor.Analyze(video);
            AppendDebug(TracklistReportFormatter.Format(video, LastTracklistReport));
        }
        catch (Exception ex)
        {
            // A parsing surprise must never break the analysis itself — it's diagnostic output.
            AppendDebug($"[tracklist] erro ao detectar: {ex.Message}\n");
        }
    }

    // ───────────────────────── Metadados (painel de pré-visualização) ─────────────────────────
    // The fields yt-dlp found for the previewed video, each with "include in the file" + an expander for
    // its value. Unchecking a field adds its key to Settings.Audio.ExcludedMetadataFields (persisted, so
    // it applies to every future download); the tri-state master mirrors Settings.Audio.EmbedMetadata.

    public ObservableCollection<MetadataItemViewModel> MetadataItems { get; } = [];
    public bool HasMetadataItems => MetadataItems.Count > 0;

    private bool _syncingMetadata;

    /// <summary>
    /// Master checkbox: true = every listed field goes in, false = nothing is embedded, null = some fields
    /// are excluded. Setting it true clears the exclusions of the listed fields; false excludes them all and
    /// turns metadata embedding off.
    /// </summary>
    public bool? IncludeMetadataState
    {
        get
        {
            if (!Settings.Audio.EmbedMetadata)
                return false;
            if (MetadataItems.Count == 0)
                return true;
            var included = MetadataItems.Count(m => m.IsIncluded);
            return included == MetadataItems.Count ? true : included == 0 ? false : null;
        }
        set
        {
            var on = value == true;
            _syncingMetadata = true;
            try
            {
                var excluded = Settings.Audio.ExcludedMetadataFields;
                foreach (var item in MetadataItems)
                {
                    if (on) excluded.Remove(item.Key);
                    else if (!excluded.Contains(item.Key)) excluded.Add(item.Key);
                    item.SetIncludedSilently(on);
                }
                Settings.Audio.EmbedMetadata = on;
            }
            finally { _syncingMetadata = false; }
            OnPropertyChanged();
        }
    }

    /// <summary>Rebuilds the list for the video currently in the preview (single video or the selected playlist row).</summary>
    private void RebuildMetadataItems()
    {
        var source = SelectedPlaylistItem?.Video.Metadata ?? SingleVideo?.Metadata;
        MetadataItems.Clear();
        if (source is not null)
        {
            var excluded = Settings.Audio.ExcludedMetadataFields;
            foreach (var def in MetadataFields.Embeddable)
            {
                if (!source.TryGetValue(def.Key, out var value))
                    continue;
                var included = Settings.Audio.EmbedMetadata && !excluded.Contains(def.Key);
                MetadataItems.Add(new MetadataItemViewModel(def, value, included, OnMetadataItemToggled));
            }
        }
        OnPropertyChanged(nameof(HasMetadataItems));
        OnPropertyChanged(nameof(IncludeMetadataState));
    }

    private void OnMetadataItemToggled(MetadataItemViewModel item, bool included)
    {
        if (_syncingMetadata)
            return;
        var excluded = Settings.Audio.ExcludedMetadataFields;
        if (included)
        {
            excluded.Remove(item.Key);
            if (!Settings.Audio.EmbedMetadata)
            {
                // Re-enabling one field turns embedding back on; every other listed field was
                // excluded when the master went off, so only this one comes back.
                _syncingMetadata = true;
                try { Settings.Audio.EmbedMetadata = true; }
                finally { _syncingMetadata = false; }
            }
        }
        else if (!excluded.Contains(item.Key))
        {
            excluded.Add(item.Key);
        }
        OnPropertyChanged(nameof(IncludeMetadataState));
    }

    private void SyncMetadataItemsFromSettings()
    {
        if (_syncingMetadata)
            return;
        var excluded = Settings.Audio.ExcludedMetadataFields;
        foreach (var item in MetadataItems)
            item.SetIncludedSilently(Settings.Audio.EmbedMetadata && !excluded.Contains(item.Key));
        OnPropertyChanged(nameof(IncludeMetadataState));
    }

    private async Task LoadPreviewThumbnailAsync(string id, string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        try
        {
            PreviewThumbnailPath = await _service.GetThumbnailAsync(id, url, CancellationToken.None);
        }
        catch { /* placeholder remains */ }
    }

    private async Task LoadAllThumbnailsAsync()
    {
        using var gate = new SemaphoreSlim(6);
        var items = PlaylistItems.ToList();
        var tasks = items.Select(async item =>
        {
            if (string.IsNullOrEmpty(item.ThumbnailUrl))
                return;
            await gate.WaitAsync();
            try
            {
                var path = await _service.GetThumbnailAsync(item.Id, item.ThumbnailUrl, CancellationToken.None);
                _ = _dispatcher.BeginInvoke(() => item.ThumbnailPath = path);
            }
            catch { }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch { }
    }

    private void UpdateTemplatePreview()
    {
        var sampleTitle = SingleVideo?.Title
                          ?? PlaylistItems.FirstOrDefault()?.Title
                          ?? "Exemplo de Música";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = sampleTitle,
            ["id"] = SingleVideo?.Id ?? PlaylistItems.FirstOrDefault()?.Id ?? "dQw4w9WgXcQ",
            ["uploader"] = SingleVideo?.Uploader ?? PlaylistUploader ?? "Canal",
            ["playlist_title"] = PlaylistTitle ?? "Playlist",
            ["playlist_index"] = "1",
            ["upload_date"] = "20260523",
            ["ext"] = ExtractGif ? "gif" : DownloadVideo ? "mp4" : "mp3",
        };
        var rendered = RenderTemplate(Settings.Paths.LastTemplate, values);
        var dir = Settings.Paths.LastOutputDirectory ?? "";
        TemplatePreview = string.IsNullOrEmpty(dir) ? rendered : Path.Combine(dir, rendered);
    }

    private static string RenderTemplate(string template, IReadOnlyDictionary<string, string> values)
        => Regex.Replace(template, @"%\((?<k>\w+)\)(?<pad>0\d+)?(?<type>[sd])", m =>
        {
            if (!values.TryGetValue(m.Groups["k"].Value, out var val))
                return m.Value;
            if (m.Groups["type"].Value == "d" && m.Groups["pad"].Success && int.TryParse(val, out var n))
            {
                var width = int.Parse(m.Groups["pad"].Value);
                return n.ToString("D" + width);
            }
            return val;
        });

    private bool FilterLogEntry(object obj) => obj is LogEntry e && e.Level >= MinLogLevel;

    // ───── Logs: ring buffer + batched delivery ─────
    // Entries arrive from process-pump threads, often in bursts (yt-dlp progress, ffmpeg -progress).
    // They are queued and drained in one dispatcher pass at Background priority, so a burst costs one
    // layout/render instead of one per line, and the visible list is capped at LogRingSize entries
    // (the SessionLogger still persists everything to disk).
    private const int LogRingSize = 1000;
    private const int LogFlushChunk = 400;   // max entries per pass — keeps the UI responsive under a flood
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private int _logFlushScheduled;           // 0/1, Interlocked

    private void OnLogEmitted(object? sender, LogEntry e)
    {
        _pendingLogs.Enqueue(e);
        if (Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) == 0)
            _ = _dispatcher.BeginInvoke(FlushLogs, DispatcherPriority.Background);
    }

    private void FlushLogs()
    {
        int n = 0;
        while (n < LogFlushChunk && _pendingLogs.TryDequeue(out var entry))
        {
            Logs.Add(entry);
            n++;
        }
        while (Logs.Count > LogRingSize)
            Logs.RemoveAt(0);

        Interlocked.Exchange(ref _logFlushScheduled, 0);
        if (!_pendingLogs.IsEmpty && Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) == 0)
            _ = _dispatcher.BeginInvoke(FlushLogs, DispatcherPriority.Background);   // more arrived: next pass
    }

    private static string TitleOf(UrlInfo info) => info switch
    {
        VideoUrlInfo v => v.Video.Title,
        PlaylistUrlInfo p => p.Playlist.Title,
        _ => ""
    };

    /// <summary>Tokenizes a raw argument string, honoring double quotes. No shell is involved.</summary>
    internal static string[] SplitArguments(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            args.Add(current.ToString());

        return [.. args];
    }
}

public sealed record TemplatePreset(string Label, string Template);

/// <summary>A building block for the advanced template editor: a friendly label + the segment it appends.</summary>
public sealed record TokenOption(string Label, string Value);

/// <summary>Result of the "Colar links" dialog: the URLs (one per line, deduped) and whether to number the files.</summary>
public sealed record PastedLinks(IReadOnlyList<string> Urls, bool Numbered);
