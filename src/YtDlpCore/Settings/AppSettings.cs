using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace YtDlpCore;

/// <summary>Root settings object persisted to <c>./settings.json</c>. All nodes are observable.</summary>
public sealed class AppSettings : ObservableObject
{
    /// <summary>2 = the output layout is <see cref="PathSettings.Folder"/>/<see cref="PathSettings.Name"/> (1 = a single template string).</summary>
    public const int CurrentVersion = 2;

    private int _version = CurrentVersion;
    public int Version { get => _version; set => SetProperty(ref _version, value); }

    /// <summary>Brings settings written by an older build up to <see cref="CurrentVersion"/>. Idempotent.</summary>
    public void Upgrade()
    {
        if (Version < 2)
            Paths.AdoptLegacyTemplate(Paths.LastTemplate);
        if (Version < CurrentVersion)
            Version = CurrentVersion;
    }

    public UiSettings Ui { get; init; } = new();
    public PathSettings Paths { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public VideoSettings Video { get; init; } = new();
    public AdvancedSettings Advanced { get; init; } = new();
    public ToolSettings Tools { get; init; } = new();
    public PluginSettings Plugins { get; init; } = new();
    public ObservableCollection<RecentUrl> RecentUrls { get; init; } = [];
}

/// <summary>
/// The only plugin state the user owns; everything else about a plugin is read from its folder. Ids are
/// the folder names under <c>plugins/</c>.
/// </summary>
public sealed class PluginSettings : ObservableObject
{
    /// <summary>Installed plugins the user switched off. Absent = enabled; an id that is no longer installed is harmless.</summary>
    public ObservableCollection<string> DisabledIds { get; init; } = [];

    private string? _catalogUrl;
    /// <summary>
    /// Where the Plugins tab fetches the catalog of official plugins (<c>plugins.json</c>, the zips resolved
    /// relative to it). Null or blank = the repository's `plugins` GitHub Release. A private catalog works the same way.
    /// </summary>
    public string? CatalogUrl { get => _catalogUrl; set => SetProperty(ref _catalogUrl, value); }
}

public sealed class UiSettings : ObservableObject
{
    public WindowSize LastWindowSize { get; init; } = new();

    private string _lastWindowState = "Normal";   // Normal | Maximized
    public string LastWindowState { get => _lastWindowState; set => SetProperty(ref _lastWindowState, value); }

    private bool _advancedPanelExpanded;
    public bool AdvancedPanelExpanded { get => _advancedPanelExpanded; set => SetProperty(ref _advancedPanelExpanded, value); }

    private bool _debugPanelEnabled;
    public bool DebugPanelEnabled { get => _debugPanelEnabled; set => SetProperty(ref _debugPanelEnabled, value); }

    private bool _treatAsPlaylist = true;   // analyse list URLs as playlists by default
    public bool TreatAsPlaylist { get => _treatAsPlaylist; set => SetProperty(ref _treatAsPlaylist, value); }

    private bool _previewCollapsed;          // collapsed state of the preview sidebar
    public bool PreviewCollapsed { get => _previewCollapsed; set => SetProperty(ref _previewCollapsed, value); }

    private bool _videoOptionsExpanded;      // "Opções avançadas de vídeo" expander (GIF, áudio, velocidade, recorte)
    public bool VideoOptionsExpanded { get => _videoOptionsExpanded; set => SetProperty(ref _videoOptionsExpanded, value); }

    private bool _queuePersistent = true;    // keep unfinished downloads in pending-downloads.txt across sessions
    public bool QueuePersistent { get => _queuePersistent; set => SetProperty(ref _queuePersistent, value); }
}

public sealed class WindowSize : ObservableObject
{
    private double _w = 1280;
    [JsonPropertyName("w")]
    public double W { get => _w; set => SetProperty(ref _w, value); }

    private double _h = 800;
    [JsonPropertyName("h")]
    public double H { get => _h; set => SetProperty(ref _h, value); }
}

/// <summary>
/// Where downloads go and how they're laid out under it: two selectors (<see cref="Folder"/>, <see cref="Name"/>)
/// or a hand-written yt-dlp template (<see cref="UseCustomTemplate"/> + <see cref="CustomTemplate"/>). The custom
/// text is kept while it's off, so switching back brings it back. <see cref="Template"/> is the result.
/// </summary>
public sealed class PathSettings : ObservableObject
{
    private string? _lastOutputDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public string? LastOutputDirectory { get => _lastOutputDirectory; set => SetProperty(ref _lastOutputDirectory, value); }

    private OutputFolder _folder = OutputFolder.Playlist;
    public OutputFolder Folder
    {
        get => _folder;
        set { if (SetProperty(ref _folder, value)) OnLayoutChanged(); }
    }

    private OutputName _name = OutputName.IndexTitle;
    public OutputName Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnLayoutChanged(); }
    }

    private bool _useCustomTemplate;
    /// <summary>Turning it on with nothing written starts the custom text from the selectors' template.</summary>
    public bool UseCustomTemplate
    {
        get => _useCustomTemplate;
        set
        {
            if (value && string.IsNullOrWhiteSpace(_customTemplate))
                CustomTemplate = OutputLayout.WithoutExtension(OutputLayout.Build(_folder, _name));
            if (SetProperty(ref _useCustomTemplate, value)) OnLayoutChanged();
        }
    }

    private string _customTemplate = "";
    /// <summary>The hand-written template, without the extension (<see cref="OutputLayout.FromCustom"/> appends it).</summary>
    public string CustomTemplate
    {
        get => _customTemplate;
        set { if (SetProperty(ref _customTemplate, value ?? "")) OnLayoutChanged(); }
    }

    /// <summary>The yt-dlp template every download starts from, before the per-download substitutions.</summary>
    [JsonIgnore]
    public string Template => OutputLayout.Compose(_folder, _name, _useCustomTemplate, _customTemplate);

    private string _lastTemplate = OutputLayout.Build(OutputFolder.Playlist, OutputName.IndexTitle);
    /// <summary>
    /// Not an input: a copy of <see cref="Template"/>, written so a build from before the selectors (which
    /// only knows this field) still finds the layout after a downgrade, and read once by
    /// <see cref="AdoptLegacyTemplate"/> when settings from such a build are loaded.
    /// </summary>
    public string LastTemplate { get => _lastTemplate; set => SetProperty(ref _lastTemplate, value); }

    private void OnLayoutChanged()
    {
        OnPropertyChanged(nameof(Template));
        LastTemplate = Template;
    }

    /// <summary>Settings from before the selectors: a template some combination makes picks it; anything else becomes the custom one.</summary>
    public void AdoptLegacyTemplate(string? template)
    {
        if (OutputLayout.TryMatch(template, out var folder, out var name))
        {
            Folder = folder;
            Name = name;
            UseCustomTemplate = false;
        }
        else
        {
            CustomTemplate = OutputLayout.WithoutExtension(template ?? "");
            UseCustomTemplate = true;
        }
        LastTemplate = Template;
    }
}

public sealed class AudioSettings : ObservableObject
{
    private string _bitrate = "192k";   // 128k | 192k | 320k
    public string Bitrate { get => _bitrate; set => SetProperty(ref _bitrate, value); }

    private bool _embedThumbnail = true;
    public bool EmbedThumbnail { get => _embedThumbnail; set => SetProperty(ref _embedThumbnail, value); }

    private bool _embedMetadata = true;
    public bool EmbedMetadata { get => _embedMetadata; set => SetProperty(ref _embedMetadata, value); }

    /// <summary>
    /// Metadata fields (keys from <see cref="MetadataFields.Embeddable"/>) the user unchecked in the
    /// preview panel — left blank in every download even though <see cref="EmbedMetadata"/> is on.
    /// </summary>
    public ObservableCollection<string> ExcludedMetadataFields { get; init; } = [];
}

public sealed class VideoSettings : ObservableObject
{
    private bool _downloadVideo;            // false = extrai MP3 (padrão); true = baixa o vídeo
    public bool DownloadVideo { get => _downloadVideo; set => SetProperty(ref _downloadVideo, value); }

    private bool _includeAudio = true;      // true = o MP4 tem áudio; false = MP4 mudo
    public bool IncludeAudio { get => _includeAudio; set => SetProperty(ref _includeAudio, value); }

    private bool _extractAudioSeparate;     // true = também gera um MP3 só de áudio
    public bool ExtractAudioSeparate { get => _extractAudioSeparate; set => SetProperty(ref _extractAudioSeparate, value); }

    private double _speed = 1.0;            // 0.1x .. 8x; aplicado ao vídeo via ffmpeg (setpts + atempo)
    public double Speed { get => _speed; set => SetProperty(ref _speed, value); }

    private bool _extractGif;               // true = extrai um trecho curto como .gif animado (sem áudio)
    public bool ExtractGif { get => _extractGif; set => SetProperty(ref _extractGif, value); }

    private double _gifDurationSeconds = 3.0;   // 1..20s — duração padrão do trecho extraído como GIF
    public double GifDurationSeconds { get => _gifDurationSeconds; set => SetProperty(ref _gifDurationSeconds, Math.Clamp(value, 1, 20)); }

    private double _gifSpeed = 1.0;         // 0.25x..1.0x — câmera lenta pro GIF, separado do Speed do vídeo normal
    public double GifSpeed { get => _gifSpeed; set => SetProperty(ref _gifSpeed, value); }
}

public sealed class AdvancedSettings : ObservableObject
{
    private int _parallelDownloads = 3;   // 1..10
    public int ParallelDownloads { get => _parallelDownloads; set => SetProperty(ref _parallelDownloads, value); }

    private int _retries = 10;
    public int Retries { get => _retries; set => SetProperty(ref _retries, value); }

    private int _timeoutSeconds = 60;
    public int TimeoutSeconds { get => _timeoutSeconds; set => SetProperty(ref _timeoutSeconds, value); }

    private int? _maxDurationMinutes;     // null = no limit
    public int? MaxDurationMinutes { get => _maxDurationMinutes; set => SetProperty(ref _maxDurationMinutes, value); }

    private string? _cookiesFilePath;
    public string? CookiesFilePath { get => _cookiesFilePath; set => SetProperty(ref _cookiesFilePath, value); }
}

public sealed class ToolSettings : ObservableObject
{
    private string _ytDlpPath = "./tools/yt-dlp.exe";
    public string YtDlpPath { get => _ytDlpPath; set => SetProperty(ref _ytDlpPath, value); }

    private string _ffmpegPath = "./tools/ffmpeg.exe";
    public string FfmpegPath { get => _ffmpegPath; set => SetProperty(ref _ffmpegPath, value); }

    private DateTimeOffset? _lastUpdateCheck;
    public DateTimeOffset? LastUpdateCheck { get => _lastUpdateCheck; set => SetProperty(ref _lastUpdateCheck, value); }

    private bool _autoCheckUpdatesOnStartup = true;
    public bool AutoCheckUpdatesOnStartup { get => _autoCheckUpdatesOnStartup; set => SetProperty(ref _autoCheckUpdatesOnStartup, value); }
}

public sealed class RecentUrl : ObservableObject
{
    private string _url = "";
    public string Url { get => _url; set => SetProperty(ref _url, value); }

    private string? _title;
    public string? Title { get => _title; set => SetProperty(ref _title, value); }

    private DateTimeOffset _lastUsed = DateTimeOffset.Now;
    public DateTimeOffset LastUsed { get => _lastUsed; set => SetProperty(ref _lastUsed, value); }
}
