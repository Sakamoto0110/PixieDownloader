namespace YtDlpCore;

/// <summary>
/// The only surface the WPF UI is allowed to know about. Everything is async and cancellable.
/// No <see cref="System.Diagnostics.Process"/> calls happen outside this library.
/// </summary>
public interface IYtDlpService
{
    // ───── Binary management ─────
    Task<ToolStatus> CheckYtDlpAsync(CancellationToken ct = default);
    Task<ToolStatus> CheckFfmpegAsync(CancellationToken ct = default);

    /// <summary>Downloads yt-dlp.exe into ./tools/ relative to the executable.</summary>
    Task DownloadYtDlpAsync(IProgress<double>? progress, CancellationToken ct);

    /// <summary>Downloads a portable ffmpeg build into ./tools/ relative to the executable.</summary>
    Task DownloadFfmpegAsync(FfmpegInstallKind kind, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Queries the latest yt-dlp release (yt-dlp/yt-dlp GitHub releases).</summary>
    Task<UpdateInfo> CheckYtDlpUpdateAsync(CancellationToken ct = default);

    /// <summary>Updates yt-dlp at runtime without restarting the app.</summary>
    Task UpdateYtDlpAsync(IProgress<double>? progress, CancellationToken ct);

    // ───── URL analysis ─────
    /// <summary>
    /// Analyses a URL and returns metadata + items, without downloading.
    /// When <paramref name="treatAsPlaylist"/> is true, a URL carrying a list is expanded into a
    /// playlist (<c>--flat-playlist</c>); when false, only the single video is read (<c>--no-playlist</c>).
    /// </summary>
    Task<UrlInfo> AnalyzeUrlAsync(string url, bool treatAsPlaylist, CancellationToken ct);

    /// <summary>Downloads a thumbnail and stores it in the local cache. Returns the local path.</summary>
    Task<string> GetThumbnailAsync(string videoId, string thumbnailUrl, CancellationToken ct);

    // ───── Download ─────
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);

    Task<IReadOnlyList<DownloadResult>> DownloadBatchAsync(
        BatchDownloadRequest request,
        IProgress<BatchProgress>? progress,
        CancellationToken ct);

    // ───── Debug ─────
    /// <summary>Runs yt-dlp with arbitrary args and returns the full stdout/stderr.</summary>
    Task<RawCommandResult> RunRawAsync(string[] args, CancellationToken ct);

    // ───── Events ─────
    event EventHandler<LogEntry>? LogEmitted;
}
