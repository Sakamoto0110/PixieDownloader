using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pixie.Library.Catalog;

/// <summary>A named folder of the catalogue: what the tab shows as a toggle, pointing at one real directory.</summary>
internal sealed record LibraryRoot(string Name, string Path);

/// <summary>
/// The catalogue's state, as the manifest carries it. <c>Syncing</c> means "the index is being rebuilt, do not
/// trust it" (the write itself is one atomic rename at the end); found on disk at startup it means the previous
/// run never finished, and the next one is a full scan. <c>Failed</c> is the same promise made explicitly.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncStatus>))]
internal enum SyncStatus
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("synced")] Synced,
    [JsonStringEnumMemberName("syncing")] Syncing,
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>
/// <c>data/library/manifest.json</c> — configuration, meant to be read by a person: the sync flag, the folders,
/// the player, the options. The index is a separate file because it is data, not configuration.
/// </summary>
internal sealed class ManifestData
{
    public int SchemaVersion { get; set; } = 1;
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public List<LibraryRoot> Roots { get; set; } = [];

    /// <summary>The program that opens a file, or <see langword="null"/> for whatever Windows associates with it.</summary>
    public string? Player { get; set; }

    /// <summary>A download landing outside every root adds its output folder as a new root.</summary>
    public bool AutoAddRoots { get; set; } = true;

    /// <summary>Opening the tab checks the folders and syncs what changed. Off, only the button syncs.</summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>Extensions beyond the built-in audio and video ones, without the dot ("gif").</summary>
    public List<string> ExtraExtensions { get; set; } = [];

    public DateTime? LastSync { get; set; }
}

/// <summary>One file of the catalogue. Identity is (path, size, mtime): while those hold, the tags are not read again.</summary>
internal sealed record LibraryEntry
{
    public required string Path { get; init; }
    public required long Size { get; init; }

    /// <summary>Last write time, UTC.</summary>
    public required DateTime Modified { get; init; }

    /// <summary>Creation time, UTC — when the file appeared on this disk, which is what "recent" means here.</summary>
    public required DateTime Created { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>Seconds, when the container said.</summary>
    public double? Duration { get; init; }

    /// <summary>kbit/s, when the container said.</summary>
    public int? Bitrate { get; init; }

    /// <summary>ID3v2 <c>CHAP</c> frames — a mix with its tracklist written in shows how many tracks it carries.</summary>
    public int Chapters { get; init; }

    public bool SameFileAs(long size, DateTime modified) => Size == size && Modified == modified;
}

/// <summary>A directory seen by the scanner and its mtime at the time: unchanged mtime = same direct children as before.</summary>
internal sealed record DirectoryStamp(string Path, DateTime Modified);

/// <summary><c>data/library/index.json</c> — written once, whole, at the end of every sync.</summary>
internal sealed class IndexData
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime? ScannedAt { get; set; }
    public List<DirectoryStamp> Directories { get; set; } = [];
    public List<LibraryEntry> Entries { get; set; } = [];
}

/// <summary>What the tag reader got out of a file.</summary>
internal sealed record TagInfo(string? Title, string? Artist, string? Album, double? DurationSeconds, int? Bitrate, int Chapters);

/// <summary>Public because the tab's type filter binds to it; the plugin's public surface is otherwise the entry class.</summary>
public enum FileKind
{
    Audio,
    Video,
    Other,
}

/// <summary>Which files the catalogue takes: the audio and video the app produces or a player reads, plus what the user adds.</summary>
internal static class FileKinds
{
    public static readonly IReadOnlySet<string> Audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma", ".aif", ".aiff" };

    public static readonly IReadOnlySet<string> Video = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v" };

    /// <summary>The full set for a scan: built-ins plus the manifest's extras, each as ".ext".</summary>
    public static IReadOnlySet<string> All(IEnumerable<string> extraExtensions)
    {
        var set = new HashSet<string>(Audio, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(Video);
        foreach (var extra in extraExtensions)
        {
            var ext = Normalize(extra);
            if (ext is not null)
                set.Add(ext);
        }
        return set;
    }

    public static FileKind KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return Audio.Contains(ext) ? FileKind.Audio : Video.Contains(ext) ? FileKind.Video : FileKind.Other;
    }

    /// <summary>"gif", " .GIF ", "*.gif" → ".gif"; nothing usable → null.</summary>
    public static string? Normalize(string extension)
    {
        var ext = extension.Trim().TrimStart('*').TrimStart('.').Trim().ToLowerInvariant();
        return ext.Length == 0 || ext.Any(c => char.IsWhiteSpace(c) || c == '.' || System.IO.Path.GetInvalidFileNameChars().Contains(c)) ? null : "." + ext;
    }
}

/// <summary>JSON the way both files are written: camelCase, enums as words, nulls left out.</summary>
internal static class LibraryJson
{
    public static readonly JsonSerializerOptions Manifest = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Index = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Write-temp-and-rename: the file on disk is always the old one or the new one, never half of either.</summary>
internal static class AtomicFile
{
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
