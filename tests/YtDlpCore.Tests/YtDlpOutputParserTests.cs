using YtDlpCore;

namespace YtDlpCore.Tests;

public class YtDlpOutputParserTests
{
    [Theory]
    [InlineData("[download]  42.3% of 5.20MiB at  1.20MiB/s ETA 00:03", 42.3, "1.20MiB/s", "00:03")]
    [InlineData("[download] 100.0% of 5.20MiB at 4.00MiB/s ETA 00:00", 100.0, "4.00MiB/s", "00:00")]
    [InlineData("[download]   0.0% of ~5.20MiB at Unknown B/s ETA Unknown", 0.0, "Unknown B/s", "Unknown")]
    public void TryParseProgress_parses_percent_speed_eta(string line, double pct, string speed, string eta)
    {
        var p = YtDlpOutputParser.TryParseProgress(line);
        Assert.NotNull(p);
        Assert.Equal(pct, p!.Value.Percent, 1);
        Assert.Equal(speed, p.Value.Speed);
        Assert.Equal(eta, p.Value.Eta);
    }

    [Fact]
    public void TryParseProgress_final_line_without_at_or_eta()
    {
        var p = YtDlpOutputParser.TryParseProgress("[download] 100% of 5.20MiB in 00:05");
        Assert.NotNull(p);
        Assert.Equal(100, p!.Value.Percent, 1);
        Assert.Null(p.Value.Speed);
        Assert.Null(p.Value.Eta);
    }

    [Theory]
    [InlineData("[download] Destination: C:\\Music\\song.webm")]
    [InlineData("not a progress line")]
    [InlineData("")]
    public void TryParseProgress_returns_null_when_no_percent(string line)
    {
        Assert.Null(YtDlpOutputParser.TryParseProgress(line));
    }

    [Theory]
    [InlineData("WARNING: deprecated option", LogLevel.Warning, "deprecated option")]
    [InlineData("ERROR: unable to download", LogLevel.Error, "unable to download")]
    [InlineData("  WARNING: indented warning", LogLevel.Warning, "indented warning")]
    [InlineData("[youtube] extracting info", LogLevel.Info, "[youtube] extracting info")]
    public void ClassifyStderr_distinguishes_levels(string line, LogLevel level, string message)
    {
        var (lvl, msg) = YtDlpOutputParser.ClassifyStderr(line);
        Assert.Equal(level, lvl);
        Assert.Equal(message, msg);
    }

    [Theory]
    [InlineData("[ExtractAudio] Destination: out.mp3", DownloadStage.Converting)]
    [InlineData("[ffmpeg] post-processing", DownloadStage.Converting)]
    [InlineData("[EmbedThumbnail] embedding", DownloadStage.EmbeddingMetadata)]
    [InlineData("[Metadata] writing", DownloadStage.EmbeddingMetadata)]
    [InlineData("[download]  10.0% of 1MiB", DownloadStage.Downloading)]
    [InlineData("[download] Destination: file.webm", DownloadStage.Downloading)]
    public void DetectStage_maps_prefixes(string line, DownloadStage expected)
    {
        Assert.Equal(expected, YtDlpOutputParser.DetectStage(line));
    }

    [Fact]
    public void DetectStage_returns_null_for_unrelated_lines()
    {
        Assert.Null(YtDlpOutputParser.DetectStage("[youtube] just info"));
    }

    [Theory]
    [InlineData("[ExtractAudio] Destination: C:\\Music\\Song.mp3", "C:\\Music\\Song.mp3")]
    [InlineData("[download] Destination: C:\\Music\\Song.webm", "C:\\Music\\Song.webm")]
    public void TryParseDestination_extracts_path(string line, string expected)
    {
        Assert.Equal(expected, YtDlpOutputParser.TryParseDestination(line));
    }

    [Fact]
    public void TryParseDestination_returns_null_without_marker()
    {
        Assert.Null(YtDlpOutputParser.TryParseDestination("[download] 42% of 5MiB"));
    }

    [Theory]
    [InlineData("[download] Downloading item 3 of 12", 3, 12)]
    [InlineData("[download] Downloading video 1 of 5", 1, 5)]
    public void TryParsePlaylistCounter_parses_counts(string line, int n, int m)
    {
        var c = YtDlpOutputParser.TryParsePlaylistCounter(line);
        Assert.NotNull(c);
        Assert.Equal(n, c!.Value.Current);
        Assert.Equal(m, c.Value.Total);
    }

    [Fact]
    public void TryParsePlaylistCounter_returns_null_for_other_lines()
    {
        Assert.Null(YtDlpOutputParser.TryParsePlaylistCounter("[download] 50% of 1MiB"));
    }
}
