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
    string WebpageUrl);

public record PlaylistInfo(
    string Id,
    string Title,
    string? Uploader,
    IReadOnlyList<VideoInfo> Items);
