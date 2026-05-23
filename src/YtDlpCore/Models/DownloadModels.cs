namespace YtDlpCore;

public record DownloadRequest(
    string Url,
    string OutputDirectory,
    string OutputTemplate,           // ex: "%(playlist_title)s/%(title)s.%(ext)s"
    AudioOptions Audio,
    AdvancedOptions Advanced)
{
    /// <summary>
    /// When set (e.g. "1,3,5-7"), the URL is treated as a playlist and only these 1-based items
    /// are downloaded (<c>--playlist-items</c>), preserving playlist_index/playlist_title tokens.
    /// When null, a single-video download is forced (<c>--no-playlist</c>).
    /// </summary>
    public string? PlaylistItems { get; init; }
}

/// <summary>Batch download of several URLs sharing the same options.</summary>
public record BatchDownloadRequest(
    IReadOnlyList<string> Urls,
    string OutputDirectory,
    string OutputTemplate,
    AudioOptions Audio,
    AdvancedOptions Advanced,
    int MaxParallel = 3);

public record AudioOptions(
    string Bitrate = "192k",          // 128k | 192k | 320k
    bool EmbedThumbnail = true,
    bool EmbedMetadata = true);

public record AdvancedOptions(
    int Retries = 10,
    int TimeoutSeconds = 60,
    TimeSpan? MaxDuration = null,     // skip videos longer than this
    string? CookiesFile = null);

public enum DownloadStage
{
    Queued,
    Analyzing,
    Downloading,
    Converting,
    EmbeddingMetadata,
    Done,
    Failed,
    Cancelled
}

public record DownloadProgress(
    string CurrentItemTitle,
    double PercentDone,               // 0..100
    string? SpeedText,                // "1.2MiB/s"
    string? EtaText,                  // "00:42"
    DownloadStage Stage);

public record BatchProgress(
    int CurrentIndex,
    int TotalItems,
    DownloadProgress? CurrentItem,
    int SuccessCount,
    int FailureCount);

public record DownloadResult(
    string Url,
    bool Success,
    string? OutputFilePath,
    string? ErrorMessage,
    TimeSpan Duration);
