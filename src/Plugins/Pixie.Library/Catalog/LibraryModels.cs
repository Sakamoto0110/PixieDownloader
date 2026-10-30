using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pixie.Library.Catalog;

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

/// <summary>What the scanner could say about a source or a folder the last time it looked.</summary>
internal static class NodeStates
{
    public const string Ok = "ok";

    /// <summary>A source that was not there to observe (volume off, folder gone): its tree was kept as it was.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>A folder that exists and refused to be stamped or listed: what the index knew of it was kept (inaccessible is not empty).</summary>
    public const string Inaccessible = "inaccessible";
}

/// <summary>
/// <c>data\library\index.v2.json</c> — the observed state as a tree, one per library: a subtree per source the
/// scan plan walks (a covering folder or a lone file), folders with the mtime the incremental scan compares,
/// files with their physical identity and tags. Names only; the full path of a node is its source's root plus
/// the names above it, derived when an operation needs the disk. Written whole once per sync and disposable:
/// lost or from another schema, it is rebuilt by a full scan.
/// </summary>
internal sealed class IndexData
{
    public const int CurrentSchema = 3;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public DateTime? ScannedAt { get; set; }

    /// <summary>The extensions and the scanner rules this tree was made with; different ones mean a full scan.</summary>
    public List<string> Extensions { get; set; } = [];
    public int RulesVersion { get; set; }

    public List<SourceTree> Sources { get; set; } = [];

    private List<FileNode>? _files;
    private Dictionary<string, object>? _byPath;

    /// <summary>Every file of every source, once, in tree order.</summary>
    [JsonIgnore]
    public IReadOnlyList<FileNode> Entries => _files ??= Sources.SelectMany(s => s.Files).ToList();

    /// <summary>Folders the last scan could not stamp or list, by full path.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> InaccessibleDirectories => Sources.SelectMany(s => s.Folders).Where(f => f.State == NodeStates.Inaccessible).Select(f => f.FullPath).ToList();

    /// <summary>The file at this path, or null. The lookup is built on the first question and kept: a tree does not change after its scan.</summary>
    public FileNode? FindFile(string path) => Lookup().TryGetValue(PathUtil.Canonical(path), out var node) ? node as FileNode : null;

    public FolderNode? FindFolder(string path) => Lookup().TryGetValue(PathUtil.Canonical(path), out var node) ? node as FolderNode : null;

    /// <summary>The source whose tree holds this path — the deepest one when sources nest — or null.</summary>
    public SourceTree? SourceOf(string path)
    {
        SourceTree? best = null;
        foreach (var source in Sources)
        {
            if (PathUtil.IsDescendantOrSame(path, source.RootPath) && (best is null || source.RootPath.Length > best.RootPath.Length))
                best = source;
        }
        return best;
    }

    /// <summary>Wires the parent references after deserialization. The scanner does it as it builds.</summary>
    public void Attach()
    {
        foreach (var source in Sources)
            source.Attach();
        _files = null;
        _byPath = null;
    }

    private Dictionary<string, object> Lookup()
    {
        if (_byPath is not null)
            return _byPath;
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            foreach (var folder in source.Folders)
                map.TryAdd(PathUtil.Canonical(folder.FullPath), folder);
            foreach (var file in source.Files)
                map.TryAdd(PathUtil.Canonical(file.FullPath), file);
        }
        return _byPath = map;
    }
}

/// <summary>One source of the scan plan as the index saw it: the only absolute path of its subtree.</summary>
internal sealed class SourceTree
{
    /// <summary>The folder walked, or the lone file stat'ed — normalized, case preserved.</summary>
    public string RootPath { get; set; } = "";

    /// <summary><c>folder</c> or <c>file</c>.</summary>
    public string Kind { get; set; } = SourceKinds.Folder;

    /// <summary><see cref="NodeStates.Ok"/> or <see cref="NodeStates.Unavailable"/>.</summary>
    public string State { get; set; } = NodeStates.Ok;

    /// <summary>NTFS volume serial of the source, when a handle could be opened; the whole subtree is on it (reparse points are not followed).</summary>
    public string? VolumeSerial { get; set; }

    /// <summary>A folder source: its tree. The root node's name is the folder's own.</summary>
    public FolderNode? Root { get; set; }

    /// <summary>A file source: the file.</summary>
    public FileNode? File { get; set; }

    [JsonIgnore] public bool IsFolder => Kind == SourceKinds.Folder;
    [JsonIgnore] public bool IsUnavailable => State == NodeStates.Unavailable;

    /// <summary>Every folder of the tree, the root first, depth-first.</summary>
    [JsonIgnore]
    public IEnumerable<FolderNode> Folders => Root is null ? [] : Root.SelfAndDescendants();

    /// <summary>Every file of the source, depth-first.</summary>
    [JsonIgnore]
    public IEnumerable<FileNode> Files => File is not null ? [File] : Folders.SelectMany(f => f.Files);

    public void Attach()
    {
        if (Root is not null)
            Root.Attach(this, null);
        if (File is not null)
            File.Attach(this, null);
    }
}

internal static class SourceKinds
{
    public const string Folder = "folder";
    public const string File = "file";
}

/// <summary>A directory seen by the scanner: its name, its mtime at the time (unchanged mtime = same direct children as before), what is in it.</summary>
internal sealed class FolderNode
{
    public string Name { get; set; } = "";

    /// <summary>Last write time, UTC — the key of the incremental scan.</summary>
    public DateTime Modified { get; set; }

    /// <summary><see cref="NodeStates.Ok"/> or <see cref="NodeStates.Inaccessible"/>.</summary>
    public string State { get; set; } = NodeStates.Ok;

    public List<FolderNode> Folders { get; set; } = [];
    public List<FileNode> Files { get; set; } = [];

    private string? _fullPath;

    [JsonIgnore] public SourceTree Source { get; private set; } = null!;
    [JsonIgnore] public FolderNode? Parent { get; private set; }
    [JsonIgnore] public bool IsInaccessible => State == NodeStates.Inaccessible;

    /// <summary>The source's root path plus the names down to here.</summary>
    [JsonIgnore]
    public string FullPath => _fullPath ??= Parent is null ? Source.RootPath : Path.Combine(Parent.FullPath, Name);

    public IEnumerable<FolderNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var folder in Folders)
        {
            foreach (var deeper in folder.SelfAndDescendants())
                yield return deeper;
        }
    }

    /// <summary>Every file below here, depth-first.</summary>
    public IEnumerable<FileNode> AllFiles() => SelfAndDescendants().SelectMany(f => f.Files);

    public void Attach(SourceTree source, FolderNode? parent)
    {
        Source = source;
        Parent = parent;
        _fullPath = null;
        foreach (var folder in Folders)
            folder.Attach(source, this);
        foreach (var file in Files)
            file.Attach(source, this);
    }

    /// <summary>A copy of this node alone (no children), for a scan that keeps it.</summary>
    public FolderNode CloneShallow() => new() { Name = Name, Modified = Modified, State = State };
}

/// <summary>
/// One file as the scanner observed it — physical state and tags, nothing logical. Identity for "the tags still
/// hold" is (size, mtime, and the file id when both sides know it). Where it shows in the library is
/// projection: its place in the tree, and the groups its item belongs to.
/// </summary>
internal sealed class FileNode
{
    public string Name { get; set; } = "";
    public long Size { get; set; }

    /// <summary>Last write time, UTC.</summary>
    public DateTime Modified { get; set; }

    /// <summary>Creation time, UTC — when the file appeared on this disk, which is what "recent" means here.</summary>
    public DateTime Created { get; set; }

    /// <summary>NTFS 128-bit file id, when the scanner could open a handle; with the source's volume serial, the recovery key of a locator.</summary>
    public string? FileId { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }

    /// <summary>Seconds, when the container said.</summary>
    public double? Duration { get; set; }

    /// <summary>kbit/s, when the container said.</summary>
    public int? Bitrate { get; set; }

    /// <summary>ID3v2 <c>CHAP</c> frames — a mix with its tracklist written in shows how many tracks it carries.</summary>
    public int Chapters { get; set; }

    private string? _fullPath;

    [JsonIgnore] public SourceTree Source { get; private set; } = null!;
    [JsonIgnore] public FolderNode? Parent { get; private set; }

    /// <summary>The source's root path plus the names down to here; a file source's own path.</summary>
    [JsonIgnore]
    public string FullPath => _fullPath ??= Parent is null ? Source.RootPath : System.IO.Path.Combine(Parent.FullPath, Name);

    /// <summary>The older name of <see cref="FullPath"/>, kept for its callers.</summary>
    [JsonIgnore]
    public string Path => FullPath;

    [JsonIgnore] public string? VolumeSerial => Source?.VolumeSerial;

    public void Attach(SourceTree source, FolderNode? parent)
    {
        Source = source;
        Parent = parent;
        _fullPath = null;
    }

    /// <summary>Same size and mtime, and the same file id when this node and the observation both carry one.</summary>
    public bool SameFileAs(long size, DateTime modified, string? fileId)
        => Size == size && Modified == modified && (fileId is null || FileId is null || string.Equals(FileId, fileId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every persisted field equal: the same observation of the same file, whichever tree it sits in.</summary>
    public bool SameAs(FileNode other)
        => Name == other.Name && Size == other.Size && Modified == other.Modified && Created == other.Created && FileId == other.FileId
           && Title == other.Title && Artist == other.Artist && Album == other.Album && Duration == other.Duration && Bitrate == other.Bitrate && Chapters == other.Chapters;

    public FileNode Clone() => (FileNode)MemberwiseClone();
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
