using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Pixie.TrackTracer.Detection;
using YtDlpCore;

namespace Pixie.TrackTracer.Tests;

/// <summary>
/// One fixture per real-world shape of tracklist, in <c>Fixtures/Tracklists/*.txt</c>. A fixture is the
/// text around a video (description, comments, chapters) plus what the extractor must make of it:
///
/// <code>
/// # title: …
/// # duration: 1:23:45
/// # expect: none | source=Description shape=Timestamped count=6 [header=…] [first=…] [author=…] [strays=N] [startN=m:ss|none] [endN=m:ss] [linkN=url]
/// # candidates: N          (optional: exact number of candidates, chapters included)
/// # evidence: substring    (optional, repeatable: must appear in the chosen list's evidence)
/// # note: substring        (optional, repeatable: must appear in the report's notes)
/// --- description ---
/// --- comment [pinned] [uploader] author=@x likes=N ---
/// --- chapters ---        (lines "m:ss Title")
/// </code>
///
/// To add a case, paste the text and write the expectation; nothing else changes.
/// </summary>
public class TracklistExtractorTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tracklists");
        foreach (var path in Directory.GetFiles(dir, "*.txt").OrderBy(p => p, StringComparer.Ordinal))
            yield return [Path.GetFileNameWithoutExtension(path)];
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_is_read_as_expected(string name)
    {
        var fixture = TracklistFixture.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tracklists", name + ".txt"));
        var report = TracklistExtractor.Analyze(fixture.Video);
        var rendered = TracklistReportFormatter.Format(fixture.Video, report);   // must never throw, whatever was found

        if (fixture.Candidates is { } candidates)
            Assert.True(report.Candidates.Count == candidates, $"expected {candidates} candidate(s), got {report.Candidates.Count}:\n{rendered}");
        foreach (var note in fixture.Notes)
            Assert.True(report.Notes.Any(n => n.Contains(note, StringComparison.Ordinal)), $"note \"{note}\" missing:\n{rendered}");

        if (fixture.Expect.Count == 0)
        {
            Assert.True(report.Best is null, $"expected no tracklist, got:\n{rendered}");
            return;
        }

        Assert.True(report.Best is not null, $"expected a tracklist, got none:\n{rendered}");
        var best = report.Best!;
        foreach (var (key, value) in fixture.Expect)
        {
            switch (key)
            {
                case "source": Assert.Equal(Enum.Parse<TracklistSource>(value), best.Source); break;
                case "shape": Assert.Equal(Enum.Parse<TracklistShape>(value), best.Shape); break;
                case "count": Assert.True(best.Entries.Count == int.Parse(value, CultureInfo.InvariantCulture), $"expected {value} entries:\n{rendered}"); break;
                case "header": Assert.Equal(value, best.Header); break;
                case "first": Assert.Equal(value, best.Entries[0].Text); break;
                case "author": Assert.Equal(value, best.Author); break;
                case "strays": Assert.Contains($"{value} linha(s) estranha(s) no meio", best.Evidence); break;
                default:
                    {
                        var m = Regex.Match(key, @"^(start|end|link)(\d+)$");
                        Assert.True(m.Success, $"unknown expectation '{key}'");
                        var entry = best.Entries[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1];
                        switch (m.Groups[1].Value)
                        {
                            case "start": Assert.Equal(value == "none" ? null : TracklistFixture.ParseTime(value), entry.Start); break;
                            case "end": Assert.Equal(value == "none" ? null : TracklistFixture.ParseTime(value), entry.End); break;
                            case "link": Assert.Equal(value, entry.Link); break;
                        }
                        break;
                    }
            }
        }
        foreach (var evidence in fixture.Evidence)
            Assert.True(best.Evidence.Any(e => e.Contains(evidence, StringComparison.Ordinal)), $"evidence \"{evidence}\" missing:\n{rendered}");
    }

    [Fact]
    public void Formatter_lays_out_url_title_and_findings()
    {
        var video = new VideoInfo("abc", "Um mix", null, TimeSpan.FromMinutes(10), null, "https://youtu.be/abc")
        {
            Metadata = new Dictionary<string, string> { ["description"] = "Tracklist:\n0:00 A - B\n3:00 C - D\n6:00 E - F" },
        };
        var text = TracklistReportFormatter.Format(video, TracklistExtractor.Analyze(video));
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToList();

        Assert.Equal("https://youtu.be/abc", lines[0]);
        Assert.Equal("==========", lines[1]);
        Assert.Equal("Um mix", lines[2]);
        Assert.Equal("==========", lines[3]);
        Assert.StartsWith("Tracklist: descrição · 3 faixas · com tempos · pontuação", lines[4]);
        Assert.StartsWith("  evidências: 3 linhas; tempos crescentes", lines[5]);
        Assert.Equal("  01     0:00  A - B", lines[6]);
        Assert.Equal("  03     6:00  E - F", lines[8]);
        Assert.Contains("Capítulos (yt-dlp): 0", lines);
        Assert.Contains("Comentários lidos: 0", lines);
    }

    [Fact]
    public void Chapters_are_the_fallback_when_nothing_is_detected()
    {
        var video = new VideoInfo("abc", "Um mix", null, TimeSpan.FromMinutes(10), null, "https://youtu.be/abc")
        {
            Chapters = [new("Intro", TimeSpan.Zero, TimeSpan.FromMinutes(1)), new("Drop", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)), new("Outro", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))],
        };
        var report = TracklistExtractor.Analyze(video);
        Assert.NotNull(report.Best);
        Assert.Equal(TracklistSource.Chapters, report.Best!.Source);
        Assert.Equal(["Intro", "Drop", "Outro"], report.Best.Entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=CzC6Apf0FFQ", LinkKind.Track)]
    [InlineData("https://youtu.be/ZSgzkDJU-8Q?si=x", LinkKind.Track)]
    [InlineData("https://www.youtube.com/@resonance.musics", LinkKind.Other)]
    [InlineData("https://www.youtube.com/playlist?list=PL5eQH3KE89E", LinkKind.Other)]
    [InlineData("https://soundcloud.com/santpoortnoord", LinkKind.Other)]
    [InlineData("https://soundcloud.com/frailtyxd/your-favourite-dress", LinkKind.Track)]
    [InlineData("https://soundcloud.com/eowide-hehexd/sets/bangers-only", LinkKind.Other)]
    [InlineData("https://open.spotify.com/track/7zT9Jn2QBLzQq9KocpoiNW", LinkKind.Track)]
    [InlineData("https://open.spotify.com/intl-pt/album/6LNPfW36OXP2FhT38GkYJQ", LinkKind.Track)]
    [InlineData("https://open.spotify.com/playlist/070ALJLIIxyf0z9tBjpFzs", LinkKind.Other)]
    [InlineData("https://open.spotify.com/artist/0x6v7KDiu5YRgHIx7d2Qkm", LinkKind.Other)]
    [InlineData("https://fanlink.to/soundwavesfoundmyway", LinkKind.Track)]
    [InlineData("https://www.1001tracklists.com/tracklist/abc/solomun.html", LinkKind.TracklistSite)]
    [InlineData("https://www.instagram.com/likainmusic/", LinkKind.Other)]
    [InlineData("spoti.fi/3dE1kCy", LinkKind.Other)]
    internal void Links_are_told_apart_by_host_and_path(string url, LinkKind expected)
    {
        Assert.Equal(expected, TracklistLine.ClassifyLink(url));
    }

    [Theory]
    [InlineData("00:00 Artist - Title", LineKind.Timestamped, "Artist - Title")]
    [InlineData("[01:02:33] artist — title", LineKind.Timestamped, "artist — title")]
    [InlineData("01. 00:00 Kelly & Lee - Only You (1998)", LineKind.Timestamped, "Kelly & Lee - Only You (1998)")]
    [InlineData("00:00:00 | 1. DJ Sava - Magical place", LineKind.Timestamped, "DJ Sava - Magical place")]
    [InlineData("0:00 • Intro", LineKind.Timestamped, "Intro")]
    [InlineData("00:00 - 05:32 - 2 Unlimited - No Limit", LineKind.Timestamped, "2 Unlimited - No Limit")]
    [InlineData("1\t00:00:46 – BANGER", LineKind.Timestamped, "BANGER")]
    [InlineData("００：００　アーティスト － 曲名", LineKind.Timestamped, "アーティスト - 曲名")]
    [InlineData("Artist - Title (Lofi Remix) 2:13", LineKind.TrailingTime, "Artist - Title (Lofi Remix)")]
    [InlineData("Marcel Dashwood 00:00:00 - 47:44 https://www.youtube.com/watch?v=_IlaCMib6Eg", LineKind.TrailingTime, "Marcel Dashwood")]
    [InlineData("1)Ezra Hazard - Don't Stop The Music", LineKind.Numbered, "Ezra Hazard - Don't Stop The Music")]
    [InlineData("#7 Some Artist - Song", LineKind.Numbered, "Some Artist - Song")]
    [InlineData("2 Unlimited - No Limit", LineKind.Text, "2 Unlimited - No Limit")]
    [InlineData("Biggie Smalls - Dead Wrong https://www.youtube.com/watch?v=CzC6Apf0FFQ", LineKind.TitleWithLink, "Biggie Smalls - Dead Wrong")]
    [InlineData("https://open.spotify.com/track/7zT9Jn2QBLzQq9KocpoiNW", LineKind.TrackLink, "")]
    [InlineData("santpoort → https://soundcloud.com/santpoortnoord", LineKind.OtherLink, "santpoort")]
    [InlineData("Instagram: /glowrecords", LineKind.OtherLink, "")]
    [InlineData("🎵 Tracklist:", LineKind.Header, "")]
    [InlineData("Треклист:", LineKind.Header, "")]
    [InlineData("✅ Traklist:", LineKind.Header, "")]
    [InlineData("💖Follow me on social media:", LineKind.ForeignHeader, "")]
    [InlineData("▬▬▬▬▬▬▬▬▬▬▬▬", LineKind.Divider, "")]
    [InlineData("#techno #mix #2025", LineKind.Noise, "")]
    [InlineData("", LineKind.Blank, "")]
    internal void Lines_are_classified_by_their_marks(string raw, LineKind kind, string text)
    {
        var line = TracklistLine.Classify(raw).Single();
        Assert.Equal(kind, line.Kind);
        if (kind is LineKind.Timestamped or LineKind.TrailingTime or LineKind.Numbered or LineKind.Text or LineKind.TitleWithLink or LineKind.TrackLink)
            Assert.Equal(text, line.Text);
    }

    [Fact]
    public void Header_hints_say_where_the_list_really_is()
    {
        Assert.Equal(HeaderHint.InComments, TracklistLine.Classify("🎶 | Tracklist (pinned in the comment section)").Single().Hint);
        Assert.Equal(HeaderHint.External, TracklistLine.Classify("Get the full tracklist at http://blrrm.tv/solomun").Single().Hint);
        Assert.Equal(HeaderHint.Missing, TracklistLine.Classify("Tracklist coming soon").Single().Hint);
    }
}

/// <summary>Reader for the fixture format described on <see cref="TracklistExtractorTests"/>.</summary>
public sealed class TracklistFixture
{
    public required VideoInfo Video { get; init; }
    public Dictionary<string, string> Expect { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Evidence { get; } = [];
    public int? Candidates { get; init; }

    public static TracklistFixture Load(string path)
    {
        var text = File.ReadAllText(path);
        var headerEnd = text.IndexOf("--- ", StringComparison.Ordinal);
        var header = headerEnd < 0 ? text : text[..headerEnd];
        var body = headerEnd < 0 ? "" : text[headerEnd..];

        string? title = null, description = null;
        TimeSpan? duration = null;
        int? candidates = null;
        var expect = new Dictionary<string, string>();
        var notes = new List<string>();
        var evidence = new List<string>();

        foreach (var raw in header.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("# ", StringComparison.Ordinal))
                continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var key = line[2..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "title": title = value; break;
                case "duration": duration = ParseTime(value); break;
                case "candidates": candidates = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "note": notes.Add(value); break;
                case "evidence": evidence.Add(value); break;
                case "expect":
                    if (value != "none")
                        foreach (Match m in Regex.Matches(value, @"(\w+)=(.*?)(?=\s+\w+=|$)"))
                            expect[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                    break;
            }
        }

        var comments = new List<CommentInfo>();
        var chapters = new List<ChapterInfo>();
        foreach (Match section in Regex.Matches(body, @"^--- (?<head>[^\n]*?) ---\r?\n(?<text>.*?)(?=^--- |\z)", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            var head = section.Groups["head"].Value.Trim();
            var content = section.Groups["text"].Value;
            if (head == "description")
                description = content.TrimEnd('\r', '\n');
            else if (head.StartsWith("comment", StringComparison.Ordinal))
            {
                var author = Regex.Match(head, @"author=(\S+)").Groups[1].Value;
                var likes = Regex.Match(head, @"likes=(\d+)") is { Success: true } l ? long.Parse(l.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                comments.Add(new CommentInfo(author.Length > 0 ? author : null, head.Contains(" uploader"), head.Contains(" pinned"), likes, content.TrimEnd('\r', '\n')));
            }
            else if (head == "chapters")
            {
                var starts = new List<(TimeSpan Start, string Title)>();
                foreach (var raw in content.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var space = line.IndexOf(' ', StringComparison.Ordinal);
                    starts.Add((ParseTime(line[..space]), line[(space + 1)..]));
                }
                for (int i = 0; i < starts.Count; i++)
                    chapters.Add(new ChapterInfo(starts[i].Title, starts[i].Start, i + 1 < starts.Count ? starts[i + 1].Start : duration ?? starts[i].Start));
            }
        }

        var video = new VideoInfo("fixture", title ?? Path.GetFileNameWithoutExtension(path), null, duration, null, "https://www.youtube.com/watch?v=fixture")
        {
            Metadata = description is null ? MetadataFields.Empty : new Dictionary<string, string> { ["description"] = description },
            Chapters = chapters,
            Comments = comments,
        };
        var fixture = new TracklistFixture { Video = video, Candidates = candidates };
        foreach (var (k, v) in expect) fixture.Expect[k] = v;
        fixture.Notes.AddRange(notes);
        fixture.Evidence.AddRange(evidence);
        return fixture;
    }

    /// <summary>"m:ss" or "h:mm:ss".</summary>
    public static TimeSpan ParseTime(string s)
    {
        var parts = s.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 3 ? new TimeSpan(parts[0], parts[1], parts[2]) : new TimeSpan(0, parts[0], parts[1]);
    }
}
