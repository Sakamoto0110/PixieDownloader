using Pixie.TrackTracer.Detection;
using Pixie.TrackTracer.Model;
using YtDlpCore;

namespace Pixie.TrackTracer.Expansion;

/// <summary>What one expansion did to a node — for the log line and the row.</summary>
public enum ExpandOutcome
{
    /// <summary>The node has tracks now.</summary>
    Resolved,
    /// <summary>Analysed, nothing detected (or an empty playlist).</summary>
    Empty,
    /// <summary>The analysis threw; <see cref="TracklistNode.Error"/> says why and a retry may work.</summary>
    Failed,
    /// <summary>The node is a video already on the path above it: not analysed, marked failed with the reason.</summary>
    Cycle,
    /// <summary>Beyond the depth limit, or nothing to expand (no link, already expanded): untouched.</summary>
    Skipped,
}

/// <summary>
/// Fills the children of a linked node, one node at a time, on demand — the roadmap's lazy expansion. The
/// link is analysed like the user's own URL (the analysis is a delegate, so the tests hand in a table):
/// a video goes through the same detector and its best block becomes the children; a playlist becomes one
/// child per item; a throw becomes <see cref="NodeState.Failed"/> with the message, worth a retry. Two brakes,
/// because a mega mix linking a mega mix that links it back happens: a node whose video is already on the
/// path above it is a cycle and is never analysed, and nothing below <see cref="MaxDepth"/> is expanded.
/// Never touches the tree except the node it was asked about.
/// </summary>
public sealed class TracklistExpander
{
    /// <summary>The analysis to run for a link — the host's <c>AnalyzeUrlAsync(url, treatAsPlaylist: false, fetchComments: true)</c> in the app.</summary>
    public delegate Task<UrlInfo> Analyzer(string url, CancellationToken ct);

    public const string CycleMessage = "ciclo: esse vídeo já está na árvore acima";

    private readonly Analyzer _analyze;

    public TracklistExpander(Analyzer analyze, int maxDepth = 3)
    {
        _analyze = analyze;
        MaxDepth = maxDepth;
    }

    /// <summary>
    /// How deep the tree may go, the root being depth 0: a node at this depth is shown but not expanded.
    /// The tab's selector sets it; the plugin persists it.
    /// </summary>
    public int MaxDepth { get; set; }

    /// <summary>A node the user may ask to expand: it has a link, was never expanded (or failed), and is above the limit.</summary>
    public bool CanExpand(TracklistNode node, int depth)
        => node.Url is not null && depth < MaxDepth && node.State is NodeState.Unresolved or NodeState.Failed;

    /// <summary>
    /// Expands <paramref name="node"/> in place. <paramref name="depth"/> is the node's depth (its parent's plus
    /// one) and <paramref name="ancestors"/> the nodes on the path from the root down to its parent, for the
    /// cycle check. Cancellation leaves the node as it was.
    /// </summary>
    public async Task<ExpandOutcome> ExpandAsync(TracklistNode node, int depth, IReadOnlyList<TracklistNode> ancestors, CancellationToken ct)
    {
        if (!CanExpand(node, depth))
            return ExpandOutcome.Skipped;

        var key = TracklistNode.KeyOf(node.Url);
        if (key is not null && ancestors.Any(a => TracklistNode.KeyOf(a.Url) == key))
        {
            node.State = NodeState.Failed;
            node.Error = CycleMessage;
            node.Children = [];
            return ExpandOutcome.Cycle;
        }

        UrlInfo info;
        try
        {
            info = await _analyze(node.Url!, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            node.State = NodeState.Failed;
            node.Error = Short(ex.Message);
            node.Children = [];
            return ExpandOutcome.Failed;
        }

        List<TracklistNode> children = info switch
        {
            VideoUrlInfo v => TracklistExtractor.Analyze(v.Video).Best is { } best ? TracklistNode.ChildrenFrom(best) : [],
            PlaylistUrlInfo p => TracklistNode.ChildrenFrom(p.Playlist),
            _ => [],
        };
        node.Error = null;
        node.Children = children;
        if (children.Count == 0)
        {
            node.State = NodeState.Empty;
            return ExpandOutcome.Empty;
        }
        node.State = NodeState.Resolved;
        node.Kind = NodeKind.Playlist;   // it has tracks: it is a playlist now, whatever the parent's list called it
        return ExpandOutcome.Resolved;
    }

    /// <summary>
    /// Everything expandable under <paramref name="root"/>, breadth first, one analysis at a time, until
    /// nothing is left above the limit or <paramref name="ct"/> says stop. Failed nodes are not retried here
    /// (that is the row's own button). <paramref name="starting"/> gets each node as its analysis begins,
    /// <paramref name="progress"/> as it finishes.
    /// </summary>
    public async Task<int> ExpandAllAsync(TracklistNode root, Action<TracklistNode>? starting, Action<TracklistNode, ExpandOutcome>? progress, CancellationToken ct)
    {
        var expanded = 0;
        var queue = new Queue<(TracklistNode Node, int Depth, List<TracklistNode> Path)>();
        queue.Enqueue((root, 0, []));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (node, depth, path) = queue.Dequeue();
            if (node.State == NodeState.Unresolved && CanExpand(node, depth))
            {
                starting?.Invoke(node);
                var outcome = await ExpandAsync(node, depth, path, ct);
                expanded++;
                progress?.Invoke(node, outcome);
            }
            if (node.State != NodeState.Resolved)
                continue;
            var childPath = new List<TracklistNode>(path) { node };
            foreach (var child in node.Children)
                queue.Enqueue((child, depth + 1, childPath));
        }
        return expanded;
    }

    /// <summary>How many nodes "expand all" would analyse right now.</summary>
    public int CountExpandable(TracklistNode root)
    {
        var count = 0;
        Walk(root, 0);
        return count;

        void Walk(TracklistNode node, int depth)
        {
            if (node.State == NodeState.Unresolved && CanExpand(node, depth))
                count++;
            if (node.State == NodeState.Resolved)
                foreach (var child in node.Children)
                    Walk(child, depth + 1);
        }
    }

    private static string Short(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
