using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtDlpCore;

namespace PixieDownloader.ViewModels;

public sealed partial class MainViewModel : ObservableObject
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

    public MainViewModel(IYtDlpService service, SettingsService settings, SessionLogger logger)
    {
        _service = service;
        _settings = settings;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

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

    // ───────────────────────── View interaction hooks (set by MainWindow) ─────────────────────────
    public Func<string?, string?>? PickFolder { get; set; }
    public Func<string?>? PickCookiesFile { get; set; }
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

    [RelayCommand]
    private void AddToken(TokenOption? token)
    {
        if (token is null) return;
        TemplateTokens.Add(token.Value);
        RebuildTemplateFromTokens();
    }

    [RelayCommand(CanExecute = nameof(HasTokens))]
    private void RemoveLastToken()
    {
        if (TemplateTokens.Count == 0) return;
        TemplateTokens.RemoveAt(TemplateTokens.Count - 1);
        RebuildTemplateFromTokens();
    }

    [RelayCommand(CanExecute = nameof(HasTokens))]
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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _url = "";

    partial void OnUrlChanged(string value) => ScheduleAutoAnalyze(value);

    /// <summary>When true, a URL with a list is analysed as a playlist; when false, as a single video.</summary>
    [ObservableProperty] private bool _treatAsPlaylist = true;

    partial void OnTreatAsPlaylistChanged(bool value)
    {
        Settings.Ui.TreatAsPlaylist = value;   // persisted (debounced) by SettingsService
        // Re-analyse the current URL under the new mode.
        if (!string.IsNullOrWhiteSpace(Url) && AnalyzeCommand.CanExecute(null))
            AnalyzeCommand.Execute(null);
    }

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _hasResult;

    [ObservableProperty] private bool _isPlaylist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrl))]
    private VideoInfo? _singleVideo;

    [ObservableProperty] private string? _playlistTitle;
    [ObservableProperty] private string? _playlistUploader;

    public ObservableCollection<PlaylistItemViewModel> PlaylistItems { get; } = [];
    public ICollectionView PlaylistView { get; }

    [ObservableProperty] private string _filterText = "";
    partial void OnFilterTextChanged(string value) => PlaylistView.Refresh();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewUrl))]
    private PlaylistItemViewModel? _selectedPlaylistItem;
    partial void OnSelectedPlaylistItemChanged(PlaylistItemViewModel? value)
    {
        if (value is not null)
            _ = LoadPreviewThumbnailAsync(value.Id, value.ThumbnailUrl);
    }

    public int SelectedCount => PlaylistItems.Count(i => i.IsSelected);
    public string SelectedCountText => $"{SelectedCount} de {PlaylistItems.Count} selecionados";

    // Collapsible list: ~5 rows when collapsed, full (scrollable) when expanded.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaylistMaxHeight))]
    [NotifyPropertyChangedFor(nameof(PlaylistToggleLabel))]
    private bool _isPlaylistCollapsed;
    public double PlaylistMaxHeight => IsPlaylistCollapsed ? 268 : 520;
    public string PlaylistToggleLabel => IsPlaylistCollapsed ? "Expandir" : "Recolher";

    [RelayCommand] private void TogglePlaylistCollapsed() => IsPlaylistCollapsed = !IsPlaylistCollapsed;

    // ───────────────────────── Preview ─────────────────────────
    [ObservableProperty] private string? _previewThumbnailPath;

    /// <summary>Webpage URL of the currently previewed item (opened when the preview is clicked).</summary>
    public string? PreviewUrl => SelectedPlaylistItem?.WebpageUrl ?? SingleVideo?.WebpageUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewColumnWidth))]
    private bool _isPreviewCollapsed;
    public GridLength PreviewColumnWidth => IsPreviewCollapsed ? new GridLength(40) : new GridLength(370);

    partial void OnIsPreviewCollapsedChanged(bool value) => Settings.Ui.PreviewCollapsed = value;

    [RelayCommand] private void TogglePreviewCollapsed() => IsPreviewCollapsed = !IsPreviewCollapsed;

    [RelayCommand]
    private void OpenPreview()
    {
        if (!string.IsNullOrWhiteSpace(PreviewUrl))
            OpenUrl?.Invoke(PreviewUrl);
    }

    // ───────────────────────── Output template ─────────────────────────
    [ObservableProperty] private string _templatePreview = "";

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
    [ObservableProperty] private string _statusText = "Pronto";
    [ObservableProperty] private double _progressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    private bool _isIndeterminate;

    /// <summary>The progress bar is only shown while something is actually running (idle = hidden).</summary>
    public bool ShowProgress => IsBusy || IsDownloading || IsIndeterminate || YtDlp.IsWorking || Ffmpeg.IsWorking;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    private bool _isDownloading;

    [ObservableProperty] private string? _speedText;
    [ObservableProperty] private string? _etaText;
    [ObservableProperty] private int _batchCurrent;
    [ObservableProperty] private int _batchTotal;
    public bool IsBatch => BatchTotal > 1;
    partial void OnBatchTotalChanged(int value) => OnPropertyChanged(nameof(IsBatch));

    // ───────────────────────── Tools / updates ─────────────────────────
    public ToolStatusViewModel YtDlp { get; } = new("yt-dlp");
    public ToolStatusViewModel Ffmpeg { get; } = new("ffmpeg");

    [ObservableProperty] private UpdateInfo? _updateInfo;
    [ObservableProperty] private bool _updateAvailable;

    // ───────────────────────── Debug tab ─────────────────────────
    [ObservableProperty] private string _debugUrl = "";
    [ObservableProperty] private string _debugOutput = "";
    [ObservableProperty] private string _rawArgs = "";
    [ObservableProperty] private bool _debugVerbose;
    [ObservableProperty] private bool _debugSkipDownload;

    // ───────────────────────── Logs tab ─────────────────────────
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ICollectionView LogsView { get; }

    [ObservableProperty] private LogLevel _minLogLevel = LogLevel.Debug;
    partial void OnMinLogLevelChanged(LogLevel value) => LogsView.Refresh();

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

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
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

    [RelayCommand]
    private void UseRecent(RecentUrl? recent)
    {
        if (recent is not null)
            Url = recent.Url;
    }

    // ═════════════════════════ Selection ═════════════════════════
    [RelayCommand] private void SelectAll() => SetAllSelected(true);
    [RelayCommand] private void ClearSelection() => SetAllSelected(false);

    [RelayCommand]
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
        if (obj is not PlaylistItemViewModel vm || string.IsNullOrWhiteSpace(FilterText))
            return true;
        var f = FilterText.Trim();
        return vm.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
               || (vm.Uploader?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
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

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        IsIndeterminate = false;
        ProgressValue = 0;
        SpeedText = null;
        EtaText = null;

        var audio = new AudioOptions(Settings.Audio.Bitrate, Settings.Audio.EmbedThumbnail, Settings.Audio.EmbedMetadata);
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
            if (IsPlaylist)
            {
                var selected = PlaylistItems.Where(i => i.IsSelected).ToList();
                BatchTotal = selected.Count;
                BatchCurrent = 0;
                var indices = string.Join(",", selected.Select(i => i.Index));
                var req = new DownloadRequest(_currentPlaylistUrl!, outDir, template, audio, advanced) { PlaylistItems = indices };
                StatusText = $"Baixando {selected.Count} itens...";
                await _service.DownloadAsync(req, progress, _downloadCts.Token);
            }
            else if (SingleVideo is { } v)
            {
                BatchTotal = 1;
                BatchCurrent = 1;
                var req = new DownloadRequest(v.WebpageUrl, outDir, template, audio, advanced);
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
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

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
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
    [RelayCommand]
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

    [RelayCommand]
    private void BrowseCookiesFile()
    {
        var chosen = PickCookiesFile?.Invoke();
        if (!string.IsNullOrEmpty(chosen))
            Settings.Advanced.CookiesFilePath = chosen;
    }

    [RelayCommand] private void ClearCookies() => Settings.Advanced.CookiesFilePath = null;

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolderPath?.Invoke(_logger.LogDirectory);

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(Settings.Paths.LastOutputDirectory))
            OpenFolderPath?.Invoke(Settings.Paths.LastOutputDirectory);
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (UpdateInfo is { ReleaseUrl: { Length: > 0 } url })
            OpenUrl?.Invoke(url);
    }

    // ═════════════════════════ Debug commands ═════════════════════════
    [RelayCommand] private Task Simulate() => RunDebugAsync(["--simulate", DebugUrl]);
    [RelayCommand] private Task GetFilename() => RunDebugAsync(["--get-filename", "-o", Settings.Paths.LastTemplate, DebugUrl]);
    [RelayCommand] private Task GetTitle() => RunDebugAsync(["--get-title", DebugUrl]);
    [RelayCommand] private Task GetDuration() => RunDebugAsync(["--get-duration", DebugUrl]);
    [RelayCommand] private Task GetThumbnail() => RunDebugAsync(["--get-thumbnail", DebugUrl]);
    [RelayCommand] private Task GetDirectUrl() => RunDebugAsync(["--get-url", DebugUrl]);
    [RelayCommand] private Task GetId() => RunDebugAsync(["--get-id", DebugUrl]);
    [RelayCommand] private Task ListFormats() => RunDebugAsync(["-F", DebugUrl]);
    [RelayCommand] private Task DumpJson() => RunDebugAsync(["--dump-json", DebugUrl]);

    [RelayCommand]
    private Task RunRaw()
    {
        var parsed = SplitArguments(RawArgs);
        return parsed.Length == 0 ? Task.CompletedTask : RunDebugAsync(parsed, applyToggles: false);
    }

    [RelayCommand] private void ClearOutput() => DebugOutput = "";
    [RelayCommand] private void CopyOutput() => CopyToClipboard?.Invoke(DebugOutput);

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
            ["ext"] = "mp3",
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
