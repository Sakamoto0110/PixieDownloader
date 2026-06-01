using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PixieDownloader.Mvvm;
using YtDlpCore;

namespace PixieDownloader.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IYtDlpService _service;
    private readonly SettingsService _settings;
    private readonly SessionLogger _logger;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _analyzeCts;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _autoAnalyzeCts;
    private string? _currentPlaylistUrl;
    private string? _lastAnalyzedUrl;
    private bool _isImportedList;        // true when the list came from an imported .txt (download = batch of distinct URLs)
    private string? _importFolder;       // output subfolder name for the imported list

    public MainViewModel(IYtDlpService service, SettingsService settings, SessionLogger logger)
    {
        _service = service;
        _settings = settings;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

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
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, CanDownload);
        ImportQueueCommand = new AsyncRelayCommand(ImportQueueAsync, CanImportQueue);
        CancelCommand = new RelayCommand(Cancel, CanCancel);
        InstallYtDlpCommand = new AsyncRelayCommand(InstallYtDlpAsync);
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);
        UpdateYtDlpCommand = new AsyncRelayCommand(UpdateYtDlpAsync);
        BrowseOutputDirectoryCommand = new RelayCommand(BrowseOutputDirectory);
        BrowseCookiesFileCommand = new RelayCommand(BrowseCookiesFile);
        ClearCookiesCommand = new RelayCommand(ClearCookies);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
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

        // Restore persisted UI toggles (direct field writes → no side effects on startup).
        _treatAsPlaylist = Settings.Ui.TreatAsPlaylist;
        _isPreviewCollapsed = Settings.Ui.PreviewCollapsed;

        PlaylistView = CollectionViewSource.GetDefaultView(PlaylistItems);
        PlaylistView.Filter = FilterPlaylistItem;

        LogsView = CollectionViewSource.GetDefaultView(Logs);
        LogsView.Filter = FilterLogEntry;

        // Persist + re-render when settings nodes change.
        Settings.Advanced.PropertyChanged += OnAdvancedSettingsChanged;
        Settings.Paths.PropertyChanged += OnPathsSettingsChanged;

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
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand ImportQueueCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand InstallYtDlpCommand { get; }
    public AsyncRelayCommand InstallFfmpegCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }
    public AsyncRelayCommand UpdateYtDlpCommand { get; }
    public RelayCommand BrowseOutputDirectoryCommand { get; }
    public RelayCommand BrowseCookiesFileCommand { get; }
    public RelayCommand ClearCookiesCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
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

    // ───────────────────────── View interaction hooks (set by MainWindow) ─────────────────────────
    public Func<string?, string?>? PickFolder { get; set; }
    public Func<string?>? PickCookiesFile { get; set; }
    public Func<string?>? PickQueueFile { get; set; }
    public Func<Task<FfmpegInstallKind?>>? ChooseFfmpegKind { get; set; }
    public Action<string>? CopyToClipboard { get; set; }
    public Action<string>? OpenFolderPath { get; set; }
    public Action<string>? OpenUrl { get; set; }

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

    /// <summary>Label for the single-video download button — reflects the current mode.</summary>
    public string DownloadButtonLabel => DownloadVideo ? "Baixar vídeo" : "Baixar como MP3";

    /// <summary>Auto-analyzes a freshly pasted/typed URL after a short debounce (no button click needed).</summary>
    private void ScheduleAutoAnalyze(string? value)
    {
        _autoAnalyzeCts?.Cancel();

        var url = value?.Trim() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        if (string.Equals(url, _lastAnalyzedUrl, StringComparison.OrdinalIgnoreCase))
            return;

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
                DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private bool _isPlaylist;
    public bool IsPlaylist
    {
        get => _isPlaylist;
        set => SetProperty(ref _isPlaylist, value);
    }

    private VideoInfo? _singleVideo;
    public VideoInfo? SingleVideo
    {
        get => _singleVideo;
        set
        {
            if (SetProperty(ref _singleVideo, value))
                OnPropertyChanged(nameof(PreviewUrl));
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
        set => SetProperty(ref _progressValue, value);
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

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                DownloadCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                AnalyzeCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ShowProgress));
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

    private int _batchCurrent;
    public int BatchCurrent
    {
        get => _batchCurrent;
        set => SetProperty(ref _batchCurrent, value);
    }

    private int _batchTotal;
    public int BatchTotal
    {
        get => _batchTotal;
        set
        {
            if (SetProperty(ref _batchTotal, value))
                OnBatchTotalChanged(value);
        }
    }
    public bool IsBatch => BatchTotal > 1;
    private void OnBatchTotalChanged(int value) => OnPropertyChanged(nameof(IsBatch));

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
        await RefreshToolsAsync();

        if (Settings.Tools.AutoCheckUpdatesOnStartup && YtDlp.Installed)
            _ = CheckUpdateAsync();
    }

    public void OnClosing()
    {
        _service.LogEmitted -= OnLogEmitted;
        _ = _settings.SaveAsync();
    }

    // ═════════════════════════ Analyze ═════════════════════════
    private bool CanAnalyze() => !IsBusy && !IsDownloading && !string.IsNullOrWhiteSpace(Url);

    private async Task AnalyzeAsync()
    {
        var url = Url.Trim();
        _lastAnalyzedUrl = url;
        _analyzeCts = new CancellationTokenSource();
        IsBusy = true;
        IsIndeterminate = true;
        StatusText = "Analisando URL...";
        try
        {
            var info = await _service.AnalyzeUrlAsync(url, TreatAsPlaylist, _analyzeCts.Token);
            ApplyUrlInfo(info);
            _settings.AddRecentUrl(url, TitleOf(info));
            StatusText = "Pronto";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Análise cancelada.";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro: {ex.Message}";
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

    // ═════════════════════════ Download ═════════════════════════
    private bool CanDownload()
        => HasResult
           && !IsDownloading
           && !IsBusy
           && YtDlp.Installed
           && Ffmpeg.Installed
           && (!IsPlaylist || SelectedCount > 0)
           && !string.IsNullOrWhiteSpace(Settings.Paths.LastOutputDirectory);

    private async Task DownloadAsync()
    {
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        IsIndeterminate = false;
        ProgressValue = 0;
        SpeedText = null;
        EtaText = null;

        var audio = new AudioOptions(Settings.Audio.Bitrate, Settings.Audio.EmbedThumbnail, Settings.Audio.EmbedMetadata);
        var video = new VideoOptions(Settings.Video.DownloadVideo, Settings.Video.IncludeAudio, Settings.Video.ExtractAudioSeparate, Settings.Video.Speed);
        var advanced = new AdvancedOptions(
            Settings.Advanced.Retries,
            Settings.Advanced.TimeoutSeconds,
            Settings.Advanced.MaxDurationMinutes is { } md ? TimeSpan.FromMinutes(md) : null,
            Settings.Advanced.CookiesFilePath);

        var outDir = Settings.Paths.LastOutputDirectory!;
        var template = Settings.Paths.LastTemplate;
        var progress = new Progress<DownloadProgress>(OnDownloadProgress);

        try
        {
            if (_isImportedList)
            {
                // Imported .txt: each row is a distinct URL — download the selected ones as a batch.
                var selected = PlaylistItems.Where(i => i.IsSelected).ToList();
                BatchTotal = selected.Count;
                BatchCurrent = 0;
                var urls = selected.Select(i => i.WebpageUrl).ToList();
                var dir = string.IsNullOrEmpty(_importFolder) ? outDir : Path.Combine(outDir, _importFolder);
                var req = new BatchDownloadRequest(urls, dir, "%(title)s.%(ext)s", audio, advanced, MaxParallel: 3) { Video = video };
                StatusText = $"Baixando {selected.Count} itens...";
                await _service.DownloadBatchAsync(req, new Progress<BatchProgress>(OnBatchProgress), _downloadCts.Token);
            }
            else if (IsPlaylist)
            {
                var selected = PlaylistItems.Where(i => i.IsSelected).ToList();
                BatchTotal = selected.Count;
                BatchCurrent = 0;
                var indices = string.Join(",", selected.Select(i => i.Index));
                var req = new DownloadRequest(_currentPlaylistUrl!, outDir, template, audio, advanced) { PlaylistItems = indices, Video = video };
                StatusText = $"Baixando {selected.Count} itens...";
                await _service.DownloadAsync(req, progress, _downloadCts.Token);
            }
            else if (SingleVideo is { } v)
            {
                BatchTotal = 1;
                BatchCurrent = 1;
                var req = new DownloadRequest(v.WebpageUrl, outDir, template, audio, advanced) { Video = video };
                StatusText = "Baixando...";
                var result = await _service.DownloadAsync(req, progress, _downloadCts.Token);
                StatusText = result.Success ? "Concluído" : $"Falha: {result.ErrorMessage}";
            }

            if (IsPlaylist)
                StatusText = "Concluído";
            ProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelado";
        }
        catch (Exception ex)
        {
            StatusText = $"Erro: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            SpeedText = null;
            EtaText = null;
            ProgressValue = 0;
        }
    }

    private bool CanCancel() => IsDownloading;

    private void Cancel()
    {
        _downloadCts?.Cancel();
        StatusText = "Cancelando...";
    }

    private void OnDownloadProgress(DownloadProgress p)
    {
        ProgressValue = p.PercentDone;
        SpeedText = p.SpeedText;
        EtaText = p.EtaText;
        var stage = p.Stage switch
        {
            DownloadStage.Converting => "Convertendo MP3",
            DownloadStage.EmbeddingMetadata => "Aplicando metadados",
            DownloadStage.Done => "Concluído",
            _ => "Baixando"
        };
        StatusText = IsBatch
            ? $"{stage} {BatchCurrent} de {BatchTotal}"
            : $"{stage} — {p.PercentDone:0.0}%";
    }

    // ═════════════════════════ Import .txt queue ═════════════════════════
    private bool CanImportQueue()
        => !IsDownloading
           && !IsBusy
           && YtDlp.Installed
           && Ffmpeg.Installed
           && !string.IsNullOrWhiteSpace(Settings.Paths.LastOutputDirectory);

    /// <summary>
    /// Imports a .txt (1 URL per line) and populates the same checkbox list as a playlist would, so the
    /// user can review/select and then press Download (which batches the selected distinct URLs). The first
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
            _importFolder = folder;
            IsPlaylist = true;
            PlaylistTitle = folder;
            PlaylistUploader = null;

            int idx = 1;
            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                StatusText = $"Analisando {idx} de {urls.Count}...";
                var info = await ResolveVideoInfoAsync(url, ct);
                var vm = new PlaylistItemViewModel(info, idx++);
                vm.PropertyChanged += OnPlaylistItemChanged;
                PlaylistItems.Add(vm);
            }

            SelectedPlaylistItem = PlaylistItems.FirstOrDefault();
            HasResult = true;
            _ = LoadAllThumbnailsAsync();
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectedCountText));
            UpdateTemplatePreview();
            DownloadCommand.NotifyCanExecuteChanged();
            StatusText = $"Lista '{folder}' pronta — {PlaylistItems.Count} itens";
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
            return await _service.AnalyzeUrlAsync(url, treatAsPlaylist: false, ct) switch
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

    private void OnBatchProgress(BatchProgress b)
    {
        BatchTotal = b.TotalItems;
        BatchCurrent = b.CurrentIndex;
        ProgressValue = b.TotalItems > 0 ? (b.SuccessCount + b.FailureCount) * 100.0 / b.TotalItems : 0;
        StatusText = $"Baixando {b.CurrentIndex} de {b.TotalItems}  ({b.SuccessCount} ok, {b.FailureCount} falhas)";
    }

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

    private async Task InstallFfmpegAsync()
    {
        var kind = ChooseFfmpegKind is null ? FfmpegInstallKind.Full : await ChooseFfmpegKind();
        if (kind is null)
            return;

        Ffmpeg.IsWorking = true;
        IsIndeterminate = false;
        try
        {
            var progress = new Progress<double>(v => { ProgressValue = v; StatusText = $"Baixando ffmpeg... {v:0}%"; });
            await _service.DownloadFfmpegAsync(kind.Value, progress, CancellationToken.None);
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
            OnPropertyChanged(nameof(ParallelWarning));
        if (e.PropertyName == nameof(AdvancedSettings.MaxDurationMinutes))
            OnPropertyChanged(nameof(MaxDurationText));
        if (e.PropertyName == nameof(AdvancedSettings.CookiesFilePath))
            OnPropertyChanged(nameof(Settings));
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
            ["ext"] = DownloadVideo ? "mp4" : "mp3",
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

    private void OnLogEmitted(object? sender, LogEntry e)
    {
        var counter = YtDlpOutputParser.TryParsePlaylistCounter(e.Message);
        _dispatcher.BeginInvoke(() =>
        {
            if (counter is { } c)
            {
                BatchCurrent = c.Current;
                BatchTotal = c.Total;
            }
            Logs.Add(e);
            if (Logs.Count > 5000)
                Logs.RemoveAt(0);
        });
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
