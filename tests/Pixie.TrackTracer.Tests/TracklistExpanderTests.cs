using System.IO;
using System.Net.Http;
using Pixie.TrackTracer.Detection;
using Pixie.TrackTracer.Expansion;
using Pixie.TrackTracer.Model;
using YtDlpCore;

namespace Pixie.TrackTracer.Tests;

/// <summary>
/// The lazy expansion (1.1) against a table of fake analyses: a link becomes the tracklist of the video it
/// points at, a playlist becomes its videos, a throw becomes a failed node worth a retry, a video already on
/// the path above is a cycle and is never fetched, and nothing goes past the depth limit.
/// </summary>
public sealed class TracklistExpanderTests
{
    private const string A = "https://www.youtube.com/watch?v=aaa";
    private const string B = "https://www.youtube.com/watch?v=bbb";
    private const string C = "https://www.youtube.com/watch?v=ccc";
    private const string D = "https://youtu.be/ddd";
    private const string List = "https://www.youtube.com/playlist?list=PLxyz";

    private readonly Dictionary<string, Func<UrlInfo>> _table = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _calls = [];

    private Task<UrlInfo> Analyze(string url, CancellationToken ct)
    {
        _calls.Add(url);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_table.TryGetValue(url, out var make) ? make() : throw new InvalidOperationException($"sem análise falsa pra {url}"));
    }

    private TracklistExpander Expander(int maxDepth = 3) => new(Analyze, maxDepth);

    /// <summary>A video whose description is a timestamped tracklist; each entry may carry a link.</summary>
    private static UrlInfo Video(string url, string title, params (string Title, string? Link)[] tracks)
    {
        var id = url[(url.LastIndexOf('=') + 1)..];
        var lines = tracks.Select((t, i) => $"{i * 10:00}:00 {t.Title}" + (t.Link is null ? "" : " " + t.Link));
        var description = "Tracklist:\n" + string.Join("\n", lines);
        var video = new VideoInfo(id, title, "dj", TimeSpan.FromMinutes(10 * tracks.Length), null, url)
        {
            Metadata = new Dictionary<string, string> { ["description"] = description },
        };
        return new VideoUrlInfo { OriginalUrl = url, Video = video };
    }

    private static UrlInfo Plain(string url, string title)
        => new VideoUrlInfo { OriginalUrl = url, Video = new VideoInfo("x", title, "dj", TimeSpan.FromMinutes(3), null, url) };

    /// <summary>
    /// The root as the analysis leaves it: the video at <see cref="A"/> with its detected list. The detector
    /// wants more than one entry, so every list here opens with a plain "Intro" and the interesting track is index 1.
    /// </summary>
    private TracklistNode Root(params (string Title, string? Link)[] tracks)
    {
        var info = (VideoUrlInfo)Video(A, "Mega mix A", [("Intro", null), .. tracks]);
        var best = TracklistExtractor.Analyze(info.Video).Best!;
        return TracklistPayload.From(info.Video, best).Root;
    }

    [Fact]
    public void The_root_built_from_a_list_has_linked_tracks_unresolved_and_plain_ones_empty()
    {
        var root = Root(("Outro mix", B));

        Assert.Equal(NodeState.Empty, root.Children[0].State);   // "Intro"
        Assert.Equal(NodeState.Unresolved, root.Children[1].State);
        Assert.Equal(B, root.Children[1].Url);
        Assert.Equal(NodeKind.Track, root.Children[1].Kind);
    }

    [Fact]
    public async Task A_linked_track_expands_into_the_tracklist_of_the_video_it_points_at()
    {
        _table[B] = () => Video(B, "Mix B", ("B um", null), ("B dois", C), ("B três", null));
        var root = Root(("Outro mix", B));
        var node = root.Children[1];

        var outcome = await Expander().ExpandAsync(node, 1, [root], CancellationToken.None);

        Assert.Equal(ExpandOutcome.Resolved, outcome);
        Assert.Equal(NodeState.Resolved, node.State);
        Assert.Equal(NodeKind.Playlist, node.Kind);   // it has tracks now
        Assert.Equal(["B um", "B dois", "B três"], node.Children.Select(c => c.Title));
        Assert.Equal(NodeState.Unresolved, node.Children[1].State);   // "B dois" links C: expandable in turn
        Assert.Equal([B], _calls);
        Assert.Equal(NodeState.Empty, root.Children[0].State);   // the sibling was not touched
    }

    [Fact]
    public async Task A_link_without_a_tracklist_is_empty_and_a_playlist_link_lists_its_videos()
    {
        _table[B] = () => Plain(B, "Só uma música");
        _table[List] = () => new PlaylistUrlInfo
        {
            OriginalUrl = List,
            Playlist = new PlaylistInfo("PLxyz", "Sets", "dj", [new VideoInfo("c", "Set C", "dj", null, null, C), new VideoInfo("d", "Set D", "dj", null, null, D)]),
        };
        var root = Root(("Uma", B), ("Os sets", List));
        var expander = Expander();

        Assert.Equal(ExpandOutcome.Empty, await expander.ExpandAsync(root.Children[1], 1, [root], CancellationToken.None));
        Assert.Equal(NodeState.Empty, root.Children[1].State);
        Assert.Empty(root.Children[1].Children);
        Assert.Null(root.Children[1].Error);

        Assert.Equal(ExpandOutcome.Resolved, await expander.ExpandAsync(root.Children[2], 1, [root], CancellationToken.None));
        var items = root.Children[2].Children;
        Assert.Equal(["Set C", "Set D"], items.Select(c => c.Title));
        Assert.All(items, c => Assert.Null(c.TimestampMs));   // separate files: no position in this one
        Assert.All(items, c => Assert.Equal(NodeState.Unresolved, c.State));
        Assert.Equal([C, D], items.Select(c => c.Url));
    }

    [Fact]
    public async Task A_video_already_on_the_path_above_is_a_cycle_and_is_never_analysed()
    {
        _table[B] = () => Video(B, "Mix B", ("De volta pro A", "https://youtu.be/aaa"), ("Outra", C));
        var root = Root(("Mix B", B));
        var expander = Expander();
        await expander.ExpandAsync(root.Children[1], 1, [root], CancellationToken.None);
        var backToA = root.Children[1].Children[0];
        _calls.Clear();

        var outcome = await expander.ExpandAsync(backToA, 2, [root, root.Children[1]], CancellationToken.None);

        Assert.Equal(ExpandOutcome.Cycle, outcome);
        Assert.Equal(NodeState.Failed, backToA.State);
        Assert.Equal(TracklistExpander.CycleMessage, backToA.Error);
        Assert.Empty(_calls);   // youtu.be/aaa and watch?v=aaa are the same video: no fetch
    }

    [Fact]
    public async Task Nothing_expands_at_or_past_the_depth_limit()
    {
        _table[B] = () => Video(B, "Mix B", ("B um", null), ("B dois", C));
        var root = Root(("Mix B", B));
        var expander = Expander(maxDepth: 1);
        var node = root.Children[1];

        Assert.False(expander.CanExpand(node, 1));
        Assert.Equal(ExpandOutcome.Skipped, await expander.ExpandAsync(node, 1, [root], CancellationToken.None));
        Assert.Equal(NodeState.Unresolved, node.State);
        Assert.Empty(_calls);

        expander.MaxDepth = 2;   // the selector moved: the same node is now within reach
        Assert.True(expander.CanExpand(node, 1));
        Assert.Equal(ExpandOutcome.Resolved, await expander.ExpandAsync(node, 1, [root], CancellationToken.None));
        Assert.False(expander.CanExpand(node.Children[1], 2));   // but its linked child sits at the limit
    }

    [Fact]
    public async Task A_throwing_analysis_fails_the_node_with_the_message_and_a_retry_can_succeed()
    {
        var attempts = 0;
        _table[B] = () => ++attempts == 1 ? throw new HttpRequestException("HTTP 429 Too Many Requests") : Video(B, "Mix B", ("B um", null), ("B dois", null));
        var root = Root(("Mix B", B));
        var expander = Expander();
        var node = root.Children[1];

        Assert.Equal(ExpandOutcome.Failed, await expander.ExpandAsync(node, 1, [root], CancellationToken.None));
        Assert.Equal(NodeState.Failed, node.State);
        Assert.Equal("HTTP 429 Too Many Requests", node.Error);
        Assert.Empty(node.Children);
        Assert.True(expander.CanExpand(node, 1));   // failed is worth a retry

        Assert.Equal(ExpandOutcome.Resolved, await expander.ExpandAsync(node, 1, [root], CancellationToken.None));
        Assert.Null(node.Error);
        Assert.Equal(2, node.Children.Count);
    }

    [Fact]
    public async Task A_cancelled_analysis_leaves_the_node_as_it_was()
    {
        _table[B] = () => Video(B, "Mix B", ("B um", null), ("B dois", null));
        var root = Root(("Mix B", B));
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Expander().ExpandAsync(root.Children[1], 1, [root], cancelled));

        Assert.Equal(NodeState.Unresolved, root.Children[1].State);
        Assert.Null(root.Children[1].Error);
    }

    [Fact]
    public async Task Expand_all_walks_breadth_first_one_analysis_at_a_time_until_the_limit()
    {
        _table[B] = () => Video(B, "Mix B", ("B um", C), ("B dois", null));
        _table[C] = () => Video(C, "Mix C", ("C um", D), ("C dois", null));
        _table[D] = () => Video(D, "Mix D", ("D um", null), ("D dois", null));
        var root = Root(("Mix B", B));
        var expander = Expander(maxDepth: 3);
        var seen = new List<(string Title, ExpandOutcome Outcome)>();

        var count = await expander.ExpandAllAsync(root, null, (n, o) => seen.Add((n.Title, o)), CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal([B, C], _calls);   // depth 1 then depth 2; D sits at depth 3 = the limit, untouched
        Assert.Equal([("Mix B", ExpandOutcome.Resolved), ("B um", ExpandOutcome.Resolved)], seen);
        var d = root.Children[1].Children[0].Children[0];
        Assert.Equal("C um", d.Title);
        Assert.Equal(NodeState.Unresolved, d.State);
        Assert.Equal(0, expander.CountExpandable(root));

        expander.MaxDepth = 4;
        Assert.Equal(1, expander.CountExpandable(root));
        Assert.Equal(1, await expander.ExpandAllAsync(root, null, null, CancellationToken.None));
        Assert.Equal(["D um", "D dois"], d.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Expand_all_stops_where_it_is_cancelled_and_skips_failed_nodes()
    {
        _table[B] = () => throw new HttpRequestException("HTTP 403");
        _table[C] = () => Video(C, "Mix C", ("C um", null), ("C dois", null));
        var root = Root(("Mix B", B), ("Mix C", C));
        var expander = Expander();
        var first = await expander.ExpandAsync(root.Children[1], 1, [root], CancellationToken.None);
        Assert.Equal(ExpandOutcome.Failed, first);
        _calls.Clear();

        Assert.Equal(1, await expander.ExpandAllAsync(root, null, null, CancellationToken.None));
        Assert.Equal([C], _calls);   // the failed one is the row's own retry, not expand-all's business

        using var cts = new CancellationTokenSource();
        _table[D] = () => Video(D, "Mix D", ("D um", null), ("D dois", null));
        var late = Root(("Mix D", D));
        var started = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expander.ExpandAllAsync(late, _ => { started++; cts.Cancel(); }, null, cts.Token));
        Assert.Equal(1, started);
        Assert.Equal(NodeState.Unresolved, late.Children[1].State);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123&list=PL9&t=10", "yt:abc123")]
    [InlineData("https://youtu.be/abc123?si=xyz", "yt:abc123")]
    [InlineData("https://m.youtube.com/shorts/abc123", "yt:abc123")]
    [InlineData("https://www.youtube.com/playlist?list=PL9", "ytlist:PL9")]
    [InlineData("https://soundcloud.com/dj/set-one/", "https://soundcloud.com/dj/set-one")]
    [InlineData("  https://Example.com/Mix ", "https://example.com/mix")]
    public void KeyOf_treats_the_same_video_behind_different_urls_as_one(string url, string key)
        => Assert.Equal(key, TracklistNode.KeyOf(url));

    [Fact]
    public void KeyOf_is_null_without_a_link() => Assert.Null(TracklistNode.KeyOf(null));

    [Fact]
    public void Settings_persist_the_depth_and_clamp_nonsense()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixie-tracktracer-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var fresh = TrackTracerSettings.Load(dir);
            Assert.Equal(TrackTracerSettings.DefaultMaxDepth, fresh.MaxDepth);

            fresh.MaxDepth = 5;
            fresh.Save();
            Assert.Equal(5, TrackTracerSettings.Load(dir).MaxDepth);
            Assert.False(File.Exists(Path.Combine(dir, "settings.json.tmp")));

            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "maxDepth": 99 }""");
            Assert.Equal(TrackTracerSettings.MaxDepthLimit, TrackTracerSettings.Load(dir).MaxDepth);

            File.WriteAllText(Path.Combine(dir, "settings.json"), "not json");
            Assert.Equal(TrackTracerSettings.DefaultMaxDepth, TrackTracerSettings.Load(dir).MaxDepth);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
