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

    /// <summary>Video vs. audio-only mode and speed. Defaults to audio-only (MP3) when omitted.</summary>
    public VideoOptions Video { get; init; } = new();
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

/// <summary>
/// Video-download options. When <see cref="DownloadVideo"/> is false the request is a plain
/// audio (MP3) extraction. When true, the best video is downloaded to MP4:
/// <see cref="IncludeAudio"/> controls whether the MP4 keeps its audio track,
/// <see cref="ExtractAudioSeparate"/> additionally produces a standalone MP3, and
/// <see cref="Speed"/> (1.0 = normal) re-encodes the video faster/slower.
/// </summary>
public record VideoOptions(
    bool DownloadVideo = false,
    bool IncludeAudio = true,
    bool ExtractAudioSeparate = false,
    double Speed = 1.0);

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
