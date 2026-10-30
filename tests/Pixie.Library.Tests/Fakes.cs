using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>Items, groups and memberships as the manifest carries them, built in one line each.</summary>
internal static class Items
{
    public static ItemData SourceFolder(string path, string? name = null, string? id = null, string? fileId = null, string? serial = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Kind = ItemKinds.Folder,
        Locator = new LocatorData { LastKnownPath = path, FileId = fileId, VolumeSerial = serial },
        Source = new SourceData { Name = name ?? LibraryScanner.NameOf(path.TrimEnd(Path.DirectorySeparatorChar)) },
    };

    public static ItemData SourceFile(string path, string? name = null, string? id = null, string? fileId = null, string? serial = null, long? size = null, DateTime? mtime = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Kind = ItemKinds.File,
        Locator = new LocatorData { LastKnownPath = path, FileId = fileId, VolumeSerial = serial, Size = size, Mtime = mtime },
        Source = new SourceData { Name = name ?? Path.GetFileName(path) },
    };

    /// <summary>An item without the source role: it exists because a group names it.</summary>
    public static ItemData File(string path, string? id = null, string? fileId = null, string? serial = null, long? size = null, DateTime? mtime = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Kind = ItemKinds.File,
        Locator = new LocatorData { LastKnownPath = path, FileId = fileId, VolumeSerial = serial, Size = size, Mtime = mtime },
    };

    public static ItemData Folder(string path, string? id = null, string? fileId = null, string? serial = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Kind = ItemKinds.Folder,
        Locator = new LocatorData { LastKnownPath = path, FileId = fileId, VolumeSerial = serial },
    };

    public static GroupData Group(string name, string? id = null, string? parent = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Name = name,
        ParentGroupId = parent,
    };

    public static MembershipData Member(string groupId, string itemId) => new() { GroupId = groupId, ItemId = itemId };

    /// <summary>A snapshot straight from data, the way <see cref="LibraryManifest.Snapshot"/> would build it — conflicts computed here too.</summary>
    public static ManifestSnapshot Snapshot(ManifestData data, bool readOnly = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in data.Items.Select(i => i.Id).Concat(data.Groups.Select(g => g.Id)))
        {
            if (!string.IsNullOrEmpty(id) && !seen.Add(id))
                conflicted.Add(id);
        }
        return new ManifestSnapshot(data, conflicted, readOnly);
    }

    /// <summary>A manifest with these parts (items, groups, memberships, in any order).</summary>
    public static ManifestData Data(params object[] parts)
    {
        var data = new ManifestData();
        foreach (var part in parts)
        {
            switch (part)
            {
                case ItemData item: data.Items.Add(item); break;
                case GroupData group: data.Groups.Add(group); break;
                case MembershipData membership: data.Memberships.Add(membership); break;
                default: throw new ArgumentException($"não é parte de manifesto: {part}");
            }
        }
        return data;
    }
}

/// <summary>
/// A file system that answers from a table: what is at a path, which volumes are mounted, where a file id
/// lives now. Every question is remembered, so a test can say what the reconciler did not ask.
/// </summary>
internal sealed class FakeProbe : IFileProbe
{
    private readonly Dictionary<string, ProbeResult> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byId = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Stats { get; } = [];
    public List<string> IdLookups { get; } = [];

    public FakeProbe Folder(string path, string? serial = "VOL1", string? fileId = null)
    {
        _entries[Key(path)] = new ProbeResult(ProbeOutcome.Found, true, 0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), serial, fileId);
        Root(path);
        if (serial is not null) _volumes.Add(serial);
        if (serial is not null && fileId is not null) _byId[serial + "/" + fileId] = PathUtil.Normalize(path);
        return this;
    }

    public FakeProbe File(string path, long size = 100, DateTime? modified = null, string? serial = "VOL1", string? fileId = null)
    {
        _entries[Key(path)] = new ProbeResult(ProbeOutcome.Found, false, size, modified ?? new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), serial, fileId);
        Root(path);
        if (serial is not null) _volumes.Add(serial);
        if (serial is not null && fileId is not null) _byId[serial + "/" + fileId] = PathUtil.Normalize(path);
        return this;
    }

    public FakeProbe Denied(string path)
    {
        _entries[Key(path)] = ProbeResult.Denied;
        Root(path);
        return this;
    }

    /// <summary>The drive exists (the path's root answers) even though nothing is at the path.</summary>
    public FakeProbe Root(string path)
    {
        if (PathUtil.VolumeRootOf(path) is { } root)
            _roots.Add(root);
        return this;
    }

    public FakeProbe Volume(string serial)
    {
        _volumes.Add(serial);
        return this;
    }

    public FakeProbe Remove(string path)
    {
        _entries.Remove(Key(path));
        return this;
    }

    public ProbeResult Stat(string path)
    {
        Stats.Add(path);
        return _entries.TryGetValue(Key(path), out var result) ? result : ProbeResult.Missing;
    }

    public bool IsVolumeReady(string volumeSerial, string pathHint) => _volumes.Contains(volumeSerial);

    public bool PathRootExists(string path) => PathUtil.VolumeRootOf(path) is { } root && _roots.Contains(root);

    public string? FindByFileId(string volumeSerial, string fileId, string pathHint)
    {
        IdLookups.Add(fileId);
        return _byId.TryGetValue(volumeSerial + "/" + fileId, out var path) ? path : null;
    }

    private static string Key(string path) => PathUtil.Canonical(path);
}

/// <summary>An identity delegate for the scanner: a stable id per path unless a test says a file was replaced.</summary>
internal sealed class FakeIdentity
{
    public Dictionary<string, string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    public string Serial { get; set; } = "FAKEVOL";

    public FileIdentityInfo Get(string path)
    {
        Calls.Add(path);
        if (!Ids.TryGetValue(path, out var id))
            Ids[path] = id = "id-" + Guid.NewGuid().ToString("N")[..8];
        return new FileIdentityInfo(Serial, id);
    }
}

/// <summary>
/// An index tree built from absolute paths, for tests without a disk: each path lands under the deepest source
/// of the plan that contains it (a source of its own when none does — what an index keeps of a source that
/// left the plan), folders created along the way.
/// </summary>
internal sealed class TestIndex
{
    private static readonly DateTime Stamp = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private readonly IndexData _index = new() { RulesVersion = LibraryScanner.RulesVersion };

    public TestIndex(ScanPlan? plan = null)
    {
        if (plan is null)
            return;
        _index.Extensions = plan.Extensions.Select(e => e.ToLowerInvariant()).OrderBy(e => e, StringComparer.Ordinal).ToList();
        foreach (var source in plan.FolderSources)
            _index.Sources.Add(new SourceTree { RootPath = source.Path, Kind = SourceKinds.Folder, Root = new FolderNode { Name = LibraryScanner.NameOf(source.Path), Modified = Stamp } });
        foreach (var source in plan.FileSources)
            _index.Sources.Add(new SourceTree { RootPath = source.Path, Kind = SourceKinds.File });
    }

    public TestIndex File(string path, long size = 100, DateTime? modified = null, string? fileId = null, string? title = null, string? serial = null)
    {
        path = PathUtil.Normalize(path);
        var node = new FileNode { Name = Path.GetFileName(path), Size = size, Modified = modified ?? Stamp, Created = Stamp, FileId = fileId, Title = title };
        var source = SourceFor(path);
        if (serial is not null)
            source.VolumeSerial = serial;
        if (!source.IsFolder)
        {
            source.File = node;
            return this;
        }
        FolderAt(source, Path.GetDirectoryName(path)!).Files.Add(node);
        return this;
    }

    /// <summary>A folder that exists in the tree, files or not.</summary>
    public TestIndex Folder(string path)
    {
        path = PathUtil.Normalize(path);
        FolderAt(SourceFor(path), path);
        return this;
    }

    public TestIndex Inaccessible(string path)
    {
        path = PathUtil.Normalize(path);
        FolderAt(SourceFor(path), path).State = NodeStates.Inaccessible;
        return this;
    }

    public TestIndex Unavailable(string sourcePath)
    {
        SourceFor(PathUtil.Normalize(sourcePath)).State = NodeStates.Unavailable;
        return this;
    }

    public IndexData Build()
    {
        _index.Sources.RemoveAll(s => s.Root is null && s.File is null);
        _index.ScannedAt = Stamp;
        _index.Attach();
        return _index;
    }

    private SourceTree SourceFor(string path)
    {
        SourceTree? best = null;
        foreach (var source in _index.Sources)
        {
            if (PathUtil.IsDescendantOrSame(path, source.RootPath) && (best is null || source.RootPath.Length > best.RootPath.Length))
                best = source;
        }
        if (best is not null)
            return best;
        var root = Path.GetDirectoryName(path) ?? path;
        best = new SourceTree { RootPath = root, Kind = SourceKinds.Folder, Root = new FolderNode { Name = LibraryScanner.NameOf(root), Modified = Stamp } };
        _index.Sources.Add(best);
        return best;
    }

    private static FolderNode FolderAt(SourceTree source, string directory)
    {
        var node = source.Root!;
        var relative = PathUtil.Relative(directory, source.RootPath);
        if (relative.Length == 0)
            return node;
        foreach (var segment in relative.Split('\\'))
        {
            var child = node.Folders.FirstOrDefault(f => f.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                child = new FolderNode { Name = segment, Modified = Stamp };
                node.Folders.Add(child);
            }
            node = child;
        }
        return node;
    }
}
