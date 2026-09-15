using System.IO;
using Pixie.TrackTracer.Model;
using TagLib.Id3v2;

namespace Pixie.TrackTracer.Tagging;

/// <summary>
/// Writes a tracklist into an MP3 the way podcasts do it — ID3v2 <c>CHAP</c> frames (start/end in ms plus a
/// title) tied together by one top-level <c>CTOC</c> — so VLC, foobar2000 and mpv show chapters without
/// knowing Pixie exists. The whole tree goes next to them, as JSON, in <c>TXXX:PIXIE_TRACKLIST</c>: that one
/// is for Pixie itself. Nothing bespoke, nothing binary. Writing again replaces what a previous run left.
/// </summary>
public static class Id3ChapterWriter
{
    public const string PayloadDescription = "PIXIE_TRACKLIST";
    private const string TocId = "toc";
    private const uint NoByteOffset = 0xFFFFFFFF;   // the spec's "not used" for the byte offsets

    /// <summary>The copy being tagged, next to the file; never left behind by a run that finished, well or badly.</summary>
    public const string TempSuffix = ".pixie-tmp";

    /// <summary>
    /// Returns how many <c>CHAP</c> frames were written — zero when the entries carry no start times, in
    /// which case only the payload goes in. The last chapter ends at <paramref name="totalDuration"/>.
    /// The file is never touched in place: the tag goes into a copy next to it and the copy takes the
    /// original's place in one rename, so a process that dies halfway (the app closing right after a
    /// download) leaves the old file, not a broken one — and a cancellation before the swap leaves it too.
    /// </summary>
    public static int Write(string mp3Path, TracklistPayload payload, TimeSpan? totalDuration, CancellationToken cancellationToken = default)
    {
        var temp = mp3Path + TempSuffix;
        try
        {
            File.Copy(mp3Path, temp, overwrite: true);
            int chapters;
            // TagLib# saves in place, rewriting the whole file whenever the tag grows (it always grows: the
            // ffmpeg that made the file left no padding) — that is why it works on the copy. The mime type is
            // given because the copy's name doesn't end in .mp3.
            using (var file = TagLib.File.Create(temp, "audio/mpeg", TagLib.ReadStyle.Average))
            {
                chapters = WriteTag(file, payload, totalDuration);
                cancellationToken.ThrowIfCancellationRequested();
                file.Save();
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, mp3Path, overwrite: true);
            return chapters;
        }
        finally
        {
            // A no-op after the rename; the leftover after a throw. Never lets a locked temp mask that throw.
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static int WriteTag(TagLib.File file, TracklistPayload payload, TimeSpan? totalDuration)
    {
        var tag = (Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);

        tag.RemoveFrames("CHAP");
        tag.RemoveFrames("CTOC");
        if (UserTextInformationFrame.Get(tag, PayloadDescription, false) is { } old)
            tag.RemoveFrame(old);

        var timed = payload.Root.Children
            .Where(c => c.TimestampMs is not null)
            .OrderBy(c => c.TimestampMs)
            .ToList();

        var ids = new List<string>();
        for (int i = 0; i < timed.Count; i++)
        {
            var start = (uint)Math.Max(0, timed[i].TimestampMs!.Value);
            var end = i + 1 < timed.Count
                ? (uint)Math.Max(0, timed[i + 1].TimestampMs!.Value)
                : (uint)Math.Max(start, totalDuration?.TotalMilliseconds ?? start);
            if (end < start)
                end = start;

            var id = $"chp{i}";
            tag.AddFrame(new ChapterFrame(id, timed[i].Title)
            {
                StartMilliseconds = start,
                EndMilliseconds = end,
                StartByteOffset = NoByteOffset,
                EndByteOffset = NoByteOffset,
            });
            ids.Add(id);
        }
        if (ids.Count > 0)
            tag.AddFrame(new TableOfContentsFrame(TocId, payload.Root.Title) { IsTopLevel = true, IsOrdered = true, ChapterIds = ids });

        tag.AddFrame(new UserTextInformationFrame(PayloadDescription) { Text = [payload.ToJson()] });
        return ids.Count;
    }

    /// <summary>The tree a previous run stored in the file, or null when there is none (or it is unreadable).</summary>
    public static TracklistPayload? Read(string mp3Path)
    {
        using var file = TagLib.File.Create(mp3Path);
        if (file.GetTag(TagLib.TagTypes.Id3v2, create: false) is not Tag tag)
            return null;
        var frame = UserTextInformationFrame.Get(tag, PayloadDescription, false);
        var json = frame?.Text.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return TracklistPayload.FromJson(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
