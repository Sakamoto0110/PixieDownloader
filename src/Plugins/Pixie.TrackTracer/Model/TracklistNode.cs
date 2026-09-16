using System.Text.Json;
using System.Text.Json.Serialization;
using Pixie.TrackTracer.Detection;
using YtDlpCore;

namespace Pixie.TrackTracer.Model;

public enum NodeKind
{
    Track,
    Playlist,
}

/// <summary>
/// Whether a node's <see cref="TracklistNode.Children"/> were ever looked for. Kept in a field of its own
/// (never as a sentinel child) so <c>children</c> is always a list and iterating it is always right: on an
/// unresolved node the loop simply runs zero times.
/// </summary>
public enum NodeState
{
    /// <summary>Never expanded. A linked track starts here; expanding it (1.1) is what fills its children.</summary>
    Unresolved,
    /// <summary>Expanded and it has tracks.</summary>
    Resolved,
    /// <summary>Expanded, nothing found — the common case for a plain track; unlike <see cref="Failed"/> it is not worth a retry.</summary>
    Empty,
    /// <summary>Expansion failed; <see cref="TracklistNode.Error"/> says why (a rate limit is worth a retry).</summary>
    Failed,
}

/// <summary>
/// One node of the tracklist tree. A playlist and a track are the <em>same</em> type — a playlist is a node
/// whose children are filled — so rendering and serialisation work at any depth without a special case for
/// the root (docs/ROADMAP.md, "Modelo de dados"). Field types never change: <c>children</c> is always an
/// array, <c>state</c> always a string.
/// </summary>
public sealed class TracklistNode
{
    public required string Title { get; set; }
    public required NodeKind Kind { get; set; }
    public long? TimestampMs { get; set; }
    public string? Url { get; set; }
    public required NodeState State { get; set; }
    public string? Error { get; set; }
    public List<TracklistNode> Children { get; set; } = [];

    /// <summary>One child per entry of a detected list — the same mapping at the root and at any depth.</summary>
    public static List<TracklistNode> ChildrenFrom(Tracklist list) => list.Entries.Select(e => new TracklistNode
    {
        Title = string.IsNullOrWhiteSpace(e.Text) ? e.Raw.Trim() : e.Text,
        Kind = NodeKind.Track,
        TimestampMs = e.Start is { } s ? (long)s.TotalMilliseconds : null,
        Url = e.Link,
        State = e.Link is null ? NodeState.Empty : NodeState.Unresolved,
    }).ToList();

    /// <summary>One child per video of a linked playlist: no timestamps (they are separate files), each expandable in turn.</summary>
    public static List<TracklistNode> ChildrenFrom(PlaylistInfo playlist) => playlist.Items.Select(v => new TracklistNode
    {
        Title = v.Title,
        Kind = NodeKind.Track,
        Url = string.IsNullOrWhiteSpace(v.WebpageUrl) ? null : v.WebpageUrl,
        State = string.IsNullOrWhiteSpace(v.WebpageUrl) ? NodeState.Empty : NodeState.Unresolved,
    }).ToList();

    /// <summary>
    /// What makes two nodes "the same video" for cycle detection: the YouTube id when the URL has one
    /// (<c>watch?v=</c>, <c>youtu.be/</c>, <c>/shorts/</c>, <c>/live/</c>), else the URL itself, trimmed and
    /// case-folded. Null for a node without a link.
    /// </summary>
    public static string? KeyOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var shortHost = uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
            if (shortHost || uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (query["v"] is { Length: > 0 } v)
                    return "yt:" + v;
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (shortHost && segments.Length >= 1)
                    return "yt:" + segments[0];
                if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v")
                    return "yt:" + segments[1];
                if (query["list"] is { Length: > 0 } list)
                    return "ytlist:" + list;
            }
        }
        return trimmed.TrimEnd('/').ToLowerInvariant();
    }
}

/// <summary>
/// What gets written into the file (<c>TXXX:PIXIE_TRACKLIST</c>) and what a later version reads back.
/// <see cref="PayloadVersion"/> is the tracklist format's own number, not the app's: it is what lets an MP3
/// tagged six months ago still be understood.
/// </summary>
public sealed class TracklistPayload
{
    public const int CurrentVersion = 1;

    public int PayloadVersion { get; set; } = CurrentVersion;

    /// <summary>Where the list came from: description, pinnedComment, uploaderComment, topComment or chapters.</summary>
    public required string Source { get; set; }

    public required TracklistNode Root { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TracklistPayload? FromJson(string json) => JsonSerializer.Deserialize<TracklistPayload>(json, Json);

    /// <summary>
    /// The tree as the analysis leaves it: the analysed video as the root playlist, one child per detected
    /// track. A track that carries a link is left <see cref="NodeState.Unresolved"/> — it may itself be a mix,
    /// and expanding it (<c>TracklistExpander</c>) is what fills its children — and a plain one is
    /// <see cref="NodeState.Empty"/>.
    /// </summary>
    public static TracklistPayload From(VideoInfo video, Tracklist list)
    {
        var root = new TracklistNode
        {
            Title = video.Title,
            Kind = NodeKind.Playlist,
            Url = video.WebpageUrl,
            State = NodeState.Resolved,
            Children = TracklistNode.ChildrenFrom(list),
        };
        return new TracklistPayload { Source = SourceName(list.Source), Root = root };
    }

    public static string SourceName(TracklistSource source) => source switch
    {
        TracklistSource.Description => "description",
        TracklistSource.PinnedComment => "pinnedComment",
        TracklistSource.UploaderComment => "uploaderComment",
        TracklistSource.TopComment => "topComment",
        TracklistSource.Chapters => "chapters",
        _ => source.ToString(),
    };
}
