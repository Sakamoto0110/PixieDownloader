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

    /// <summary>The full description — the same text that <see cref="Metadata"/> embeds under "description".</summary>
    public string? Description => Metadata.TryGetValue("description", out var d) ? d : null;

    /// <summary>yt-dlp's <c>chapters</c> (YouTube's parse of description timestamps). Empty for flat playlist entries.</summary>
    public IReadOnlyList<ChapterInfo> Chapters { get; init; } = [];

    /// <summary>
    /// The top comments, when the analysis asked for them (single video only — see
    /// <see cref="IYtDlpService.AnalyzeUrlAsync"/>): the pinned one first if there is one, most liked next.
    /// </summary>
    public IReadOnlyList<CommentInfo> Comments { get; init; } = [];
}

public record PlaylistInfo(
    string Id,
    string Title,
    string? Uploader,
    IReadOnlyList<VideoInfo> Items);
