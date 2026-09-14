using System.Text.Json;

namespace YtDlpCore;

/// <summary>A chapter yt-dlp derived from the description (YouTube's own rules: first at 0:00, ≥ 3, ascending).</summary>
public sealed record ChapterInfo(string Title, TimeSpan Start, TimeSpan End)
{
    /// <summary>Reads the <c>chapters</c> array of an info-json object; empty when absent.</summary>
    public static IReadOnlyList<ChapterInfo> ReadAll(JsonElement info)
    {
        var list = new List<ChapterInfo>();
        if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("chapters", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var c in arr.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
                continue;
            var title = c.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var start = c.TryGetProperty("start_time", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            var end = c.TryGetProperty("end_time", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : start;
            list.Add(new ChapterInfo(title, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
        }
        return list;
    }
}

/// <summary>
/// One of the few top comments the single-video analysis fetches — enough to catch the pinned one and
/// the uploader's own, which is where a tracklist lives when it is not in the description.
/// </summary>
public sealed record CommentInfo(string? Author, bool IsUploader, bool IsPinned, long LikeCount, string Text)
{
    /// <summary>Reads the <c>comments</c> array that <c>--write-comments</c> adds to an info-json object; empty when absent.</summary>
    public static IReadOnlyList<CommentInfo> ReadAll(JsonElement info)
    {
        var list = new List<CommentInfo>();
        if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var c in arr.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object)
                continue;
            var text = c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (text.Length == 0)
                continue;
            var author = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var uploader = c.TryGetProperty("author_is_uploader", out var u) && u.ValueKind == JsonValueKind.True;
            var pinned = c.TryGetProperty("is_pinned", out var p) && p.ValueKind == JsonValueKind.True;
            long likes = c.TryGetProperty("like_count", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt64(out var n) ? n : 0;
            list.Add(new CommentInfo(author, uploader, pinned, likes, text));
        }
        return list;
    }
}

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
