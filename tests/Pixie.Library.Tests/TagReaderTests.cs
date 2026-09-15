using System.IO;
using Pixie.Library.Catalog;
using TagLib.Id3v2;

namespace Pixie.Library.Tests;

/// <summary>TagLib# through the reader: a tagged MP3, an MP3 with chapters, and files that are not what their extension says.</summary>
public sealed class TagReaderTests : IDisposable
{
    private readonly TempTree _tree = new("tags");

    [Fact]
    public void Reads_title_artist_album_duration_and_bitrate_from_an_mp3()
    {
        var path = _tree.Mp3("tagged.mp3");
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Strobe";
            file.Tag.Performers = ["deadmau5"];
            file.Tag.Album = "For Lack of a Better Name";
            file.Save();
        }

        var info = TagReader.Read(path);

        Assert.NotNull(info);
        Assert.Equal("Strobe", info.Title);
        Assert.Equal("deadmau5", info.Artist);
        Assert.Equal("For Lack of a Better Name", info.Album);
        Assert.InRange(info.DurationSeconds ?? 0, 0.5, 2.0);   // the fixture is one second of silence
        Assert.True(info.Bitrate > 0);
        Assert.Equal(0, info.Chapters);
    }

    [Fact]
    public void Counts_the_CHAP_frames_a_mix_carries()
    {
        var path = _tree.Mp3("mix.mp3");
        using (var file = TagLib.File.Create(path))
        {
            var tag = (Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
            for (var i = 0; i < 3; i++)
                tag.AddFrame(new ChapterFrame($"chp{i}", $"Faixa {i + 1}") { StartMilliseconds = (uint)(i * 300), EndMilliseconds = (uint)(i * 300 + 300) });
            file.Save();
        }

        var info = TagReader.Read(path);

        Assert.NotNull(info);
        Assert.Equal(3, info.Chapters);
    }

    [Fact]
    public void An_untagged_mp3_gives_no_title_but_still_a_duration()
    {
        var info = TagReader.Read(_tree.Mp3("plain.mp3"));

        Assert.NotNull(info);
        Assert.Null(info.Title);
        Assert.Null(info.Artist);
        Assert.True(info.DurationSeconds > 0);
    }

    [Fact]
    public void Junk_and_unknown_formats_give_nothing()
    {
        Assert.Null(TagReader.Read(_tree.Junk("broken.mp4")));
        Assert.Null(TagReader.Read(_tree.Junk("loop.gif")));
        Assert.Null(TagReader.Read(_tree.Full("missing.mp3")));
    }

    public void Dispose() => _tree.Dispose();
}
