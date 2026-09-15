using System.IO;

namespace Pixie.Library.Catalog;

/// <summary>
/// What TagLib# can tell about a file: title, artist, album, duration, bitrate, and how many ID3v2 <c>CHAP</c>
/// frames it carries (a mix with its tracklist written in). A file it cannot read — a format it does not
/// know, a truncated download, a GIF — gives <see langword="null"/>, and the entry keeps its file name only.
/// </summary>
internal static class TagReader
{
    public static TagInfo? Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var tag = file.Tag;
            var props = file.Properties;
            var chapters = file.GetTag(TagLib.TagTypes.Id3v2, create: false) is TagLib.Id3v2.Tag id3
                ? id3.GetFrames("CHAP").Count()
                : 0;
            var duration = props is { Duration.TotalSeconds: > 0 } ? props.Duration.TotalSeconds : (double?)null;
            var bitrate = props is { AudioBitrate: > 0 } ? props.AudioBitrate : (int?)null;
            return new TagInfo(Clean(tag?.Title), Clean(tag?.JoinedPerformers) ?? Clean(tag?.JoinedAlbumArtists), Clean(tag?.Album), duration, bitrate, chapters);
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException
                                       or UnauthorizedAccessException or ArgumentException or NullReferenceException)
        {
            // NullReferenceException: TagLib# dereferences a missing header in some truncated files
            return null;
        }
    }

    private static string? Clean(string? value)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim().Replace('\0', ' ').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
