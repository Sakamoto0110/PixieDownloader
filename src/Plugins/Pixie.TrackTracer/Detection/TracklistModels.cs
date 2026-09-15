namespace Pixie.TrackTracer.Detection;

/// <summary>Where a detected tracklist came from, in the order the extractor trusts them.</summary>
public enum TracklistSource
{
    Description,
    PinnedComment,
    UploaderComment,
    TopComment,
    /// <summary>yt-dlp's <c>chapters</c> — a fallback: YouTube already parsed the description with stricter rules than ours.</summary>
    Chapters,
}

/// <summary>The dominant shape of the lines that make up a block — what the detector recognised them by.</summary>
public enum TracklistShape
{
    /// <summary><c>00:00 Artist - Title</c>, optionally with an end time (<c>00:00 - 04:30 …</c>).</summary>
    Timestamped,
    /// <summary><c>Artist - Title 3:45</c>: a trailing time per line that does not grow — track lengths, not positions.</summary>
    TitleWithDuration,
    /// <summary><c>01. Artist - Title</c></summary>
    Numbered,
    /// <summary><c>Artist - Title https://…</c>, or a title line followed by a link line.</summary>
    TitleWithLink,
    /// <summary>A run of music-store links with no titles at all.</summary>
    LinksOnly,
    /// <summary>Bare <c>Artist - Title</c> lines — only accepted under a header or with a clear artist/title split.</summary>
    PlainTitles,
}

/// <summary>
/// One track of a detected tracklist. <see cref="Raw"/> is always the original line (what a sidecar
/// file should reproduce); the parsed fields are best-effort and may be missing.
/// </summary>
public sealed record TracklistEntry(
    int Number,
    TimeSpan? Start,
    TimeSpan? End,
    string Text,
    string? Link,
    string Raw);

/// <summary>A block of consecutive lines the extractor believes is a tracklist, with the evidence it scored.</summary>
public sealed record Tracklist(
    TracklistSource Source,
    TracklistShape Shape,
    string? Header,
    IReadOnlyList<TracklistEntry> Entries,
    int Score,
    IReadOnlyList<string> Evidence)
{
    /// <summary>Comment author, when the block came from a comment.</summary>
    public string? Author { get; init; }
}

/// <summary>
/// Everything the extractor found for one video: the block it would use, every other candidate it
/// considered (best first), and free-form notes about hints it saw along the way ("tracklist in the
/// comments", an external tracklist link, an "IDs only" disclaimer…).
/// </summary>
public sealed record TracklistReport(
    Tracklist? Best,
    IReadOnlyList<Tracklist> Candidates,
    IReadOnlyList<string> Notes)
{
    public static readonly TracklistReport Empty = new(null, [], []);
}
