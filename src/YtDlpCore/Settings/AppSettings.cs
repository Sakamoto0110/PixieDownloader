using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace YtDlpCore;

/// <summary>Root settings object persisted to <c>./settings.json</c>. All nodes are observable.</summary>
public sealed class AppSettings : ObservableObject
{
    private int _version = 1;
    public int Version { get => _version; set => SetProperty(ref _version, value); }

    public UiSettings Ui { get; init; } = new();
    public PathSettings Paths { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public VideoSettings Video { get; init; } = new();
    public AdvancedSettings Advanced { get; init; } = new();
    public ToolSettings Tools { get; init; } = new();
    public ObservableCollection<RecentUrl> RecentUrls { get; init; } = [];
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

public sealed class PathSettings : ObservableObject
{
    private string? _lastOutputDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public string? LastOutputDirectory { get => _lastOutputDirectory; set => SetProperty(ref _lastOutputDirectory, value); }

    private string _lastTemplate = "%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s";
    public string LastTemplate { get => _lastTemplate; set => SetProperty(ref _lastTemplate, value); }
}

public sealed class AudioSettings : ObservableObject
{
    private string _bitrate = "192k";   // 128k | 192k | 320k
    public string Bitrate { get => _bitrate; set => SetProperty(ref _bitrate, value); }

    private bool _embedThumbnail = true;
    public bool EmbedThumbnail { get => _embedThumbnail; set => SetProperty(ref _embedThumbnail, value); }

    private bool _embedMetadata = true;
    public bool EmbedMetadata { get => _embedMetadata; set => SetProperty(ref _embedMetadata, value); }
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
