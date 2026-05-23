namespace YtDlpCore;

/// <summary>Status of an external binary (yt-dlp or ffmpeg).</summary>
public record ToolStatus(bool Installed, string? Path, string? Version, string? Error)
{
    public static ToolStatus Missing(string? error = null) => new(false, null, null, error);
}

/// <summary>Result of comparing the locally installed yt-dlp against the latest GitHub release.</summary>
public record UpdateInfo(string CurrentVersion, string LatestVersion, bool UpdateAvailable, string ReleaseUrl);

/// <summary>Which ffmpeg distribution to install.</summary>
public enum FfmpegInstallKind
{
    /// <summary>gyan.dev release-essentials (~80MB), full codec coverage.</summary>
    Full,
    /// <summary>BtbN win64-lgpl-shared (~25MB), basic MP3 conversion only.</summary>
    Minimal
}
