namespace YtDlpCore;

/// <summary>Discriminated result of analysing a URL: either a single video or a playlist.</summary>
public abstract record UrlInfo
{
    public required string OriginalUrl { get; init; }
}

public record VideoUrlInfo : UrlInfo
{
    public required VideoInfo Video { get; init; }
}

public record PlaylistUrlInfo : UrlInfo
{
    public required PlaylistInfo Playlist { get; init; }
}

public record VideoInfo(
    string Id,
    string Title,
    string? Uploader,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    string WebpageUrl)
{
    /// <summary>
    /// The embeddable metadata yt-dlp found for this video, keyed by <see cref="MetadataFieldDef.Key"/>
    /// (see <see cref="MetadataFields.Embeddable"/>). Only fields that actually had a value are present;
    /// a flat playlist entry typically carries just title/uploader/url.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = MetadataFields.Empty;
}

public record PlaylistInfo(
    string Id,
    string Title,
    string? Uploader,
    IReadOnlyList<VideoInfo> Items);
