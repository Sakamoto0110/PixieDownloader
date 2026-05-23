using System.Globalization;
using System.Text.RegularExpressions;

namespace YtDlpCore;

/// <summary>A parsed yt-dlp <c>[download]</c> progress line.</summary>
public readonly record struct DownloadProgressLine(double Percent, string? Speed, string? Eta);

/// <summary>
/// Stateless parser for yt-dlp stdout/stderr. This is the most fragile part of the pipeline,
/// so it is fully covered by unit tests. All methods are pure and side-effect free.
/// </summary>
public static partial class YtDlpOutputParser
{
    // [download]  42.3% of 5.20MiB at  1.20MiB/s ETA 00:03
    [GeneratedRegex(@"\[download\]\s+(?<pct>\d{1,3}(?:\.\d+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\bat\s+(?<speed>(?:[\d.]+\s?(?:[KMGT]i?B|B)/s)|Unknown(?:\s?B/s)?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SpeedRegex();

    [GeneratedRegex(@"\bETA\s+(?<eta>[\d:]+|Unknown|--:--)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EtaRegex();

    // [download] Downloading item 3 of 12  (older yt-dlp: "Downloading video 3 of 12")
    [GeneratedRegex(@"Downloading\s+(?:item|video)\s+(?<n>\d+)\s+of\s+(?<m>\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlaylistCounterRegex();

    /// <summary>Parses a download progress line, or returns null if the line carries no percentage.</summary>
    public static DownloadProgressLine? TryParseProgress(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        var pm = PercentRegex().Match(line);
        if (!pm.Success)
            return null;

        if (!double.TryParse(pm.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return null;

        pct = Math.Clamp(pct, 0, 100);

        string? speed = SpeedRegex().Match(line) is { Success: true } sm ? sm.Groups["speed"].Value.Trim() : null;
        string? eta = EtaRegex().Match(line) is { Success: true } em ? em.Groups["eta"].Value.Trim() : null;

        return new DownloadProgressLine(pct, speed, eta);
    }

    /// <summary>Parses a "Downloading item N of M" playlist progress line.</summary>
    public static (int Current, int Total)? TryParsePlaylistCounter(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;
        var m = PlaylistCounterRegex().Match(line);
        if (!m.Success)
            return null;
        return (int.Parse(m.Groups["n"].Value), int.Parse(m.Groups["m"].Value));
    }

    /// <summary>Infers the current pipeline stage from yt-dlp/postprocessor prefixes.</summary>
    public static DownloadStage? DetectStage(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        if (line.Contains("[ExtractAudio]", StringComparison.Ordinal) ||
            line.Contains("[ffmpeg]", StringComparison.Ordinal))
            return DownloadStage.Converting;

        if (line.Contains("[Metadata]", StringComparison.Ordinal) ||
            line.Contains("[EmbedThumbnail]", StringComparison.Ordinal) ||
            line.Contains("[ThumbnailsConvertor]", StringComparison.Ordinal))
            return DownloadStage.EmbeddingMetadata;

        if (PercentRegex().IsMatch(line) ||
            line.Contains("[download] Destination:", StringComparison.Ordinal))
            return DownloadStage.Downloading;

        return null;
    }

    /// <summary>Classifies a stderr line: <c>WARNING:</c> → Warning, <c>ERROR:</c> → Error, else Info.</summary>
    public static (LogLevel Level, string Message) ClassifyStderr(string line)
    {
        var trimmed = (line ?? string.Empty).TrimStart();

        if (trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            return (LogLevel.Error, trimmed["ERROR:".Length..].Trim());

        if (trimmed.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            return (LogLevel.Warning, trimmed["WARNING:".Length..].Trim());

        return (LogLevel.Info, line ?? string.Empty);
    }

    /// <summary>Extracts a destination path from a "... Destination: &lt;path&gt;" line.</summary>
    public static string? TryParseDestination(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        const string marker = "Destination:";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        var path = line[(idx + marker.Length)..].Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
