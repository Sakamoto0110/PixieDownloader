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
