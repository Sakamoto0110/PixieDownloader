using YtDlpCore;

namespace YtDlpCore.Tests;

public class OutputTemplateTests
{
    [Fact]
    public void SubstituteLiteral_replaces_string_token_and_keeps_others()
    {
        var t = OutputTemplate.SubstituteLiteral("%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s", "playlist_title", "Mix 2026");
        Assert.Equal("Mix 2026/%(playlist_index)02d - %(title)s.%(ext)s", t);
    }

    [Fact]
    public void SubstituteLiteral_honors_zero_padding_on_integer_tokens()
    {
        var t = OutputTemplate.SubstituteLiteral("%(playlist_index)02d - %(title)s.%(ext)s", "playlist_index", "7");
        Assert.Equal("07 - %(title)s.%(ext)s", t);
    }

    [Fact]
    public void SubstituteLiteral_escapes_percent_and_sanitizes_path_characters()
    {
        var t = OutputTemplate.SubstituteLiteral("%(playlist_title)s/%(title)s.%(ext)s", "playlist_title", "100% Hits: A/B?");
        Assert.Equal("100%% Hits： A⧸B？/%(title)s.%(ext)s", t);
    }

    [Theory]
    [InlineData("%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s", "%(title)s.%(ext)s")]
    [InlineData("%(uploader)s/%(title)s.%(ext)s", "%(uploader)s/%(title)s.%(ext)s")]
    [InlineData("%(upload_date)s - %(title)s.%(ext)s", "%(upload_date)s - %(title)s.%(ext)s")]
    [InlineData("%(playlist_index)s_%(title)s.%(ext)s", "%(title)s.%(ext)s")]
    [InlineData("%(playlist_title)s/%(playlist_index)s", "%(title)s.%(ext)s")]
    public void WithoutPlaylistTokens_drops_tokens_and_dangling_separators(string template, string expected)
    {
        Assert.Equal(expected, OutputTemplate.WithoutPlaylistTokens(template));
    }

    [Fact]
    public void PrefixFileName_prefixes_only_the_file_name_part()
    {
        Assert.Equal("Pasta/1 - %(title)s.%(ext)s", OutputTemplate.PrefixFileName("Pasta/%(title)s.%(ext)s", "1 - "));
        Assert.Equal("1 - %(title)s.%(ext)s", OutputTemplate.PrefixFileName("%(title)s.%(ext)s", "1 - "));
    }
}

public class PendingQueueFileTests
{
    [Fact]
    public void Format_then_Parse_round_trips_every_option()
    {
        var entry = new PendingDownload(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Never Gonna Give You Up",
            @"C:\Users\me\Music", "Mix/%(title)s.%(ext)s",
            new AudioOptions("320k", EmbedThumbnail: false, EmbedMetadata: true, ExcludedMetadataFields: ["description"]),
            new VideoOptions(DownloadVideo: true, IncludeAudio: false, Speed: 1.5, StartTime: TimeSpan.FromSeconds(10), EndTime: TimeSpan.FromSeconds(20)),
            212.5);

        var text = PendingQueueFile.Format([entry]);
        var back = PendingQueueFile.Parse(text.Split('\n'));

        var e = Assert.Single(back);
        Assert.Equal(entry.Url, e.Url);
        Assert.Equal(entry.Title, e.Title);
        Assert.Equal(entry.OutputDirectory, e.OutputDirectory);
        Assert.Equal(entry.OutputTemplate, e.OutputTemplate);
        Assert.Equal("320k", e.Audio!.Bitrate);
        Assert.False(e.Audio.EmbedThumbnail);
        Assert.Equal(["description"], e.Audio.ExcludedMetadataFields);
        Assert.True(e.Video!.DownloadVideo);
        Assert.False(e.Video.IncludeAudio);
        Assert.Equal(1.5, e.Video.Speed);
        Assert.Equal(TimeSpan.FromSeconds(10), e.Video.StartTime);
        Assert.Equal(TimeSpan.FromSeconds(20), e.Video.EndTime);
        Assert.Equal(212.5, e.SourceDurationSeconds);
    }

    [Fact]
    public void Format_then_Parse_keeps_the_resumable_work_directory()
    {
        var entry = new PendingDownload("https://example.com/v", "v", null, null, null,
            new VideoOptions(DownloadVideo: true, Speed: 2.0), null, WorkDirectory: @"C:\app\.~downloads\job-abc");
        var back = PendingQueueFile.Parse(PendingQueueFile.Format([entry]).Split('\n'));
        Assert.Equal(@"C:\app\.~downloads\job-abc", Assert.Single(back).WorkDirectory);
    }

    [Fact]
    public void Parse_accepts_bare_urls_and_skips_comments_blanks_and_garbage()
    {
        string[] lines =
        [
            "# header",
            "",
            "https://example.com/video/1",
            "not a url at all",
            "{ this is not json",
            "   https://example.com/video/2   ",
        ];
        var entries = PendingQueueFile.Parse(lines);
        Assert.Equal(2, entries.Count);
        Assert.Equal("https://example.com/video/1", entries[0].Url);
        Assert.Null(entries[0].OutputTemplate);
        Assert.Equal("https://example.com/video/2", entries[1].Url);
    }

    [Fact]
    public void Format_starts_with_comment_header()
    {
        var text = PendingQueueFile.Format([]);
        Assert.StartsWith("#", text);
        Assert.Empty(PendingQueueFile.Parse(text.Split('\n')));
    }
}

public class MetadataFieldsTests
{
    [Fact]
    public void FfmpegKeysFor_expands_multi_tag_fields_and_ignores_unknown_keys()
    {
        var keys = MetadataFields.FfmpegKeysFor(["description", "purl", "bogus", "title"]).ToList();
        Assert.Equal(["description", "synopsis", "purl", "comment", "title"], keys);
    }

    [Fact]
    public void Extract_reads_values_in_priority_order_and_joins_arrays()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"title":"Song","track":"Track name","uploader":"Chan","artists":["A","B"],"upload_date":"20260101","track_number":3,"description":""}""");
        var meta = MetadataFields.Extract(doc.RootElement);
        Assert.Equal("Track name", meta["title"]);     // track beats title, like yt-dlp
        Assert.Equal("A, B", meta["artist"]);          // artists beats uploader
        Assert.Equal("20260101", meta["date"]);
        Assert.Equal("3", meta["track"]);
        Assert.False(meta.ContainsKey("description")); // empty values are not "found"
        Assert.False(meta.ContainsKey("album"));
    }
}

public class FfmpegProgressParserTests
{
    [Theory]
    [InlineData("out_time_us=1500000", 1.5)]
    [InlineData("out_time_ms=2500000", 2.5)]
    [InlineData("out_time=00:00:03.250000", 3.25)]
    public void TryParseFfmpegOutTime_reads_every_out_time_flavour(string line, double seconds)
    {
        var ts = YtDlpOutputParser.TryParseFfmpegOutTime(line);
        Assert.NotNull(ts);
        Assert.Equal(seconds, ts!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time_us=-9223372036854775808")]
    [InlineData("frame=12")]
    [InlineData("progress=continue")]
    [InlineData("")]
    public void TryParseFfmpegOutTime_returns_null_for_other_lines(string line)
    {
        Assert.Null(YtDlpOutputParser.TryParseFfmpegOutTime(line));
    }
}

public class VideoOptionsTests
{
    [Fact]
    public void NeedsFfmpegPass_only_for_gif_speed_or_silent_video_with_separate_mp3()
    {
        Assert.False(new VideoOptions().NeedsFfmpegPass);                                         // plain mp3
        Assert.False(new VideoOptions(DownloadVideo: true).NeedsFfmpegPass);                      // plain mp4
        Assert.False(new VideoOptions(DownloadVideo: true, ExtractAudioSeparate: true).NeedsFfmpegPass); // yt-dlp does -x -k itself
        Assert.True(new VideoOptions(DownloadVideo: true, Speed: 2.0).NeedsFfmpegPass);
        Assert.True(new VideoOptions(DownloadVideo: true, ExtractGif: true).NeedsFfmpegPass);
        Assert.True(new VideoOptions(DownloadVideo: true, IncludeAudio: false, ExtractAudioSeparate: true).NeedsFfmpegPass);
        Assert.False(new VideoOptions(DownloadVideo: false, Speed: 2.0).NeedsFfmpegPass);         // speed is video-only
    }
}
