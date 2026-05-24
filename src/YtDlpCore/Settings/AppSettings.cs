using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace YtDlpCore;

/// <summary>Root settings object persisted to <c>./settings.json</c>. All nodes are observable.</summary>
public sealed partial class AppSettings : ObservableObject
{
    [ObservableProperty] private int _version = 1;

    public UiSettings Ui { get; init; } = new();
    public PathSettings Paths { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public AdvancedSettings Advanced { get; init; } = new();
    public ToolSettings Tools { get; init; } = new();
    public ObservableCollection<RecentUrl> RecentUrls { get; init; } = [];
}

public sealed partial class UiSettings : ObservableObject
{
    public WindowSize LastWindowSize { get; init; } = new();
    [ObservableProperty] private string _lastWindowState = "Normal";   // Normal | Maximized
    [ObservableProperty] private bool _advancedPanelExpanded;
    [ObservableProperty] private bool _debugPanelEnabled;
    [ObservableProperty] private bool _treatAsPlaylist = true;   // analyse list URLs as playlists by default
    [ObservableProperty] private bool _previewCollapsed;         // collapsed state of the preview sidebar
}

public sealed partial class WindowSize : ObservableObject
{
    [ObservableProperty][JsonPropertyName("w")] private double _w = 1280;
    [ObservableProperty][JsonPropertyName("h")] private double _h = 800;
}

public sealed partial class PathSettings : ObservableObject
{
    [ObservableProperty] private string? _lastOutputDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    [ObservableProperty] private string _lastTemplate = "%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s";
}

public sealed partial class AudioSettings : ObservableObject
{
    [ObservableProperty] private string _bitrate = "192k";   // 128k | 192k | 320k
    [ObservableProperty] private bool _embedThumbnail = true;
    [ObservableProperty] private bool _embedMetadata = true;
}

public sealed partial class AdvancedSettings : ObservableObject
{
    [ObservableProperty] private int _parallelDownloads = 3;   // 1..10
    [ObservableProperty] private int _retries = 10;
    [ObservableProperty] private int _timeoutSeconds = 60;
    [ObservableProperty] private int? _maxDurationMinutes;     // null = no limit
    [ObservableProperty] private string? _cookiesFilePath;
}

public sealed partial class ToolSettings : ObservableObject
{
    [ObservableProperty] private string _ytDlpPath = "./tools/yt-dlp.exe";
    [ObservableProperty] private string _ffmpegPath = "./tools/ffmpeg.exe";
    [ObservableProperty] private DateTimeOffset? _lastUpdateCheck;
    [ObservableProperty] private bool _autoCheckUpdatesOnStartup = true;
}

public sealed partial class RecentUrl : ObservableObject
{
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string? _title;
    [ObservableProperty] private DateTimeOffset _lastUsed = DateTimeOffset.Now;
}
