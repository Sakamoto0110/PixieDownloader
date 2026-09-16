using System.IO;
using System.Text.Json;
using Pixie.TrackTracer.Detection;
using Pixie.TrackTracer.Model;
using Pixie.TrackTracer.Tagging;
using TagLib.Id3v2;
using YtDlpCore;

namespace Pixie.TrackTracer.Tests;

/// <summary>The tree that goes into the file, and the ID3 frames it becomes — on a 1 s silent MP3 (Fixtures/silence.mp3).</summary>
public sealed class PayloadAndChapterTests : IDisposable
{
    private static readonly VideoInfo Video = new("abc", "Mega mix vol. 3", "DJ", TimeSpan.FromSeconds(200), null, "https://www.youtube.com/watch?v=abc");

    private static readonly Tracklist List = new(
        TracklistSource.Description, TracklistShape.Timestamped, "Tracklist",
        [
            new TracklistEntry(1, TimeSpan.Zero, null, "Faixa um", null, "0:00 Faixa um"),
            new TracklistEntry(2, TimeSpan.FromSeconds(60), null, "Outro mega mix", "https://www.youtube.com/watch?v=xyz", "1:00 Outro mega mix https://www.youtube.com/watch?v=xyz"),
            new TracklistEntry(3, TimeSpan.FromSeconds(125), null, "Faixa três", null, "2:05 Faixa três"),
        ],
        Score: 40, Evidence: ["3 tempos crescentes"]);

    private readonly string _mp3 = Path.Combine(Path.GetTempPath(), "pixie-tracklist-tests", Guid.NewGuid().ToString("N") + ".mp3");

    public PayloadAndChapterTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_mp3)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.mp3"), _mp3);
    }

    // ───── The tree ─────

    [Fact]
    public void The_video_is_the_root_playlist_and_every_track_a_child_of_the_same_type()
    {
        var payload = TracklistPayload.From(Video, List);

        Assert.Equal(1, payload.PayloadVersion);
        Assert.Equal("description", payload.Source);
        var root = payload.Root;
        Assert.Equal(NodeKind.Playlist, root.Kind);
        Assert.Equal(NodeState.Resolved, root.State);
        Assert.Equal(Video.WebpageUrl, root.Url);
        Assert.Equal(3, root.Children.Count);

        // A plain track has nothing to expand; a linked one may be a mix of its own — left unresolved until 1.7.
        Assert.Equal(NodeState.Empty, root.Children[0].State);
        Assert.Equal(NodeState.Unresolved, root.Children[1].State);
        Assert.Equal("https://www.youtube.com/watch?v=xyz", root.Children[1].Url);
        Assert.Equal(60_000, root.Children[1].TimestampMs);
        Assert.All(root.Children, c => Assert.Empty(c.Children));   // always a list, never null
    }

    [Fact]
    public void The_json_is_the_roadmap_shape_and_round_trips()
    {
        var json = TracklistPayload.From(Video, List).ToJson();

        using var doc = JsonDocument.Parse(json);
        var rootEl = doc.RootElement;
        Assert.Equal(1, rootEl.GetProperty("payloadVersion").GetInt32());
        Assert.Equal("description", rootEl.GetProperty("source").GetString());
        var node = rootEl.GetProperty("root");
        Assert.Equal("playlist", node.GetProperty("kind").GetString());
        Assert.Equal("resolved", node.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Array, node.GetProperty("children").ValueKind);
        var second = node.GetProperty("children")[1];
        Assert.Equal("track", second.GetProperty("kind").GetString());
        Assert.Equal("unresolved", second.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Array, second.GetProperty("children").ValueKind);
        Assert.False(second.TryGetProperty("error", out _));   // nulls are left out

        var back = TracklistPayload.FromJson(json)!;
        Assert.Equal("Outro mega mix", back.Root.Children[1].Title);
        Assert.Equal(NodeState.Unresolved, back.Root.Children[1].State);
    }

    // ───── The file ─────

    [Fact]
    public void Chapters_become_CHAP_frames_under_one_top_level_CTOC_and_the_tree_rides_along()
    {
        var payload = TracklistPayload.From(Video, List);

        var written = Id3ChapterWriter.Write(_mp3, payload, Video.Duration);

        Assert.Equal(3, written);
        using var file = TagLib.File.Create(_mp3);
        var tag = (Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
        var chapters = tag.GetFrames("CHAP").OfType<ChapterFrame>().OrderBy(c => c.StartMilliseconds).ToList();
        Assert.Equal([0u, 60_000u, 125_000u], chapters.Select(c => c.StartMilliseconds));
        Assert.Equal([60_000u, 125_000u, 200_000u], chapters.Select(c => c.EndMilliseconds));   // each ends where the next starts; the last at the video's end
        Assert.Equal(["Faixa um", "Outro mega mix", "Faixa três"], chapters.Select(TitleOf));

        var toc = Assert.Single(tag.GetFrames("CTOC").OfType<TableOfContentsFrame>());
        Assert.True(toc.IsTopLevel);
        Assert.True(toc.IsOrdered);
        Assert.Equal(chapters.Select(c => c.Id), toc.ChapterIds);

        var back = Id3ChapterWriter.Read(_mp3)!;
        Assert.Equal("description", back.Source);
        Assert.Equal(3, back.Root.Children.Count);
        Assert.False(File.Exists(_mp3 + Id3ChapterWriter.TempSuffix));   // the copy that was tagged became the file
    }

    [Fact]
    public async Task An_expanded_tree_goes_whole_into_the_file_while_the_chapters_stay_this_files_timeline()
    {
        var payload = TracklistPayload.From(Video, List);
        var nested = new VideoInfo("xyz", "Outro mega mix", "DJ", TimeSpan.FromMinutes(30), null, "https://www.youtube.com/watch?v=xyz")
        {
            Metadata = new Dictionary<string, string> { ["description"] = "0:00 Dentro um\n10:00 Dentro dois https://www.youtube.com/watch?v=deep\n20:00 Dentro três" },
        };
        var expander = new Pixie.TrackTracer.Expansion.TracklistExpander((url, _) => Task.FromResult<UrlInfo>(new VideoUrlInfo { OriginalUrl = url, Video = nested }));
        var linked = payload.Root.Children[1];
        Assert.Equal(Pixie.TrackTracer.Expansion.ExpandOutcome.Resolved, await expander.ExpandAsync(linked, 1, [payload.Root], CancellationToken.None));

        var written = Id3ChapterWriter.Write(_mp3, payload, Video.Duration);

        Assert.Equal(3, written);   // the nested tracks are another file's timeline: no CHAP for them
        var back = Id3ChapterWriter.Read(_mp3)!;
        Assert.Equal(1, back.PayloadVersion);   // the format did not change: children were always a list
        var inner = back.Root.Children[1];
        Assert.Equal(NodeState.Resolved, inner.State);
        Assert.Equal(NodeKind.Playlist, inner.Kind);
        Assert.Equal(["Dentro um", "Dentro dois", "Dentro três"], inner.Children.Select(c => c.Title));
        Assert.Equal(600_000, inner.Children[1].TimestampMs);
        Assert.Equal(NodeState.Unresolved, inner.Children[1].State);
        Assert.Equal("https://www.youtube.com/watch?v=deep", inner.Children[1].Url);
    }

    [Fact]
    public void A_cancellation_before_the_swap_leaves_the_original_untouched_and_no_temp_behind()
    {
        var before = File.ReadAllBytes(_mp3);
        var cancelled = new CancellationToken(canceled: true);   // noticed only after the tag is built, before the swap

        Assert.Throws<OperationCanceledException>(() => Id3ChapterWriter.Write(_mp3, TracklistPayload.From(Video, List), Video.Duration, cancelled));

        Assert.Equal(before, File.ReadAllBytes(_mp3));
        Assert.False(File.Exists(_mp3 + Id3ChapterWriter.TempSuffix));
        Assert.Null(Id3ChapterWriter.Read(_mp3));
    }

    [Fact]
    public void Writing_again_replaces_instead_of_piling_up()
    {
        var payload = TracklistPayload.From(Video, List);
        Id3ChapterWriter.Write(_mp3, payload, Video.Duration);

        var shorter = TracklistPayload.From(Video, List with { Entries = List.Entries.Take(2).ToList() });
        var written = Id3ChapterWriter.Write(_mp3, shorter, Video.Duration);

        Assert.Equal(2, written);
        using var file = TagLib.File.Create(_mp3);
        var tag = (Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
        Assert.Equal(2, tag.GetFrames("CHAP").Count());
        Assert.Single(tag.GetFrames("CTOC"));
        Assert.Single(tag.GetFrames("TXXX").OfType<UserTextInformationFrame>(), f => f.Description == Id3ChapterWriter.PayloadDescription);
        Assert.Equal(2, Id3ChapterWriter.Read(_mp3)!.Root.Children.Count);
    }

    [Fact]
    public void A_list_without_times_gets_the_tree_but_no_chapters()
    {
        var untimed = List with
        {
            Shape = TracklistShape.Numbered,
            Entries = List.Entries.Select(e => e with { Start = null }).ToList(),
        };

        var written = Id3ChapterWriter.Write(_mp3, TracklistPayload.From(Video, untimed), Video.Duration);

        Assert.Equal(0, written);
        using var file = TagLib.File.Create(_mp3);
        var tag = (Tag)file.GetTag(TagLib.TagTypes.Id3v2, false);
        Assert.Empty(tag.GetFrames("CHAP"));
        Assert.Empty(tag.GetFrames("CTOC"));
        Assert.Equal(3, Id3ChapterWriter.Read(_mp3)!.Root.Children.Count);
        Assert.All(Id3ChapterWriter.Read(_mp3)!.Root.Children, c => Assert.Null(c.TimestampMs));
    }

    [Fact]
    public void A_file_never_tagged_reads_back_as_nothing()
    {
        Assert.Null(Id3ChapterWriter.Read(_mp3));
    }

    private static string TitleOf(ChapterFrame chapter) =>
        chapter.SubFrames.OfType<TextInformationFrame>().FirstOrDefault(f => f.FrameId == "TIT2")?.Text.FirstOrDefault() ?? "";

    public void Dispose()
    {
        try { File.Delete(_mp3); } catch { }
    }
}
