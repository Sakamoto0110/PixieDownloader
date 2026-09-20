using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pixie.Library.Catalog;

/// <summary>
/// <c>data\library\manifest.v2.json</c> — the logical state of the library, the part the disk does not know:
/// the options, the storage root, the sources the scanner walks, the items that carry persistent state (a
/// source, a group membership), the groups and their memberships, and what the 2.x has seen of the 1.x side.
/// Never a copy of the physical tree: that is the index. Written as a whole by <see cref="LibraryManifest"/>;
/// unknown properties round-trip untouched so a newer plugin's fields survive an older one.
/// </summary>
internal sealed class ManifestData
{
    public const int CurrentSchema = 3;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>What the 2.x has already seen of the 1.x files (<c>manifest.json</c>, <c>inbox\</c>) — null before any migration.</summary>
    public LegacyV1Data? LegacyV1 { get; set; }

    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public DateTime? LastSync { get; set; }

    /// <summary>The library's own folder: where Importar copies to and Criar pasta creates; always scanned. Null = the Music folder of the system.</summary>
    public string? StorageRoot { get; set; }

    /// <summary>The program that opens a file, or <see langword="null"/> for whatever Windows associates with it.</summary>
    public string? Player { get; set; }

    /// <summary>A download landing outside every source adds its output folder as a source.</summary>
    public bool AutoAddRoots { get; set; } = true;

    /// <summary>Opening the tab checks the folders and syncs what changed. Off, only the button syncs.</summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>Extensions beyond the built-in audio and video ones, without the dot ("gif").</summary>
    public List<string> ExtraExtensions { get; set; } = [];

    /// <summary>The tab lists the files under collapsible folder headers (off: one flat list in the chosen order).</summary>
    public bool GroupByFolder { get; set; } = true;

    /// <summary>The items with persistent state: every source, and every file or folder that belongs to a group.</summary>
    public List<ItemData> Items { get; set; } = [];

    public List<GroupData> Groups { get; set; } = [];
    public List<MembershipData> Memberships { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The 2.x's memory of the 1.x side, so nothing there is ever imported twice or resurrected.</summary>
internal sealed class LegacyV1Data
{
    public DateTime? ObservedAt { get; set; }

    /// <summary>SHA-256 of the v1 roots, canonical and sorted, as last seen.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>The v1 roots as last seen (canonical). Only roots not in here are ever imported.</summary>
    public List<string> ObservedRoots { get; set; } = [];

    /// <summary>File names of the <c>inbox\</c> blocks already translated into <c>inbox.v2\</c>; pruned to the ones still there.</summary>
    public List<string> ObservedInbox { get; set; } = [];
}

/// <summary>
/// One thing on disk the library holds persistent state about: a file or a folder, found again by its locator.
/// It is a scan source when <see cref="Source"/> is set (what the 1.x called a root); it is a member of the
/// groups that name its id. An item nobody references would be pure weight: it is removed with its last
/// membership unless it is a source.
/// </summary>
internal sealed class ItemData
{
    public string Id { get; set; } = "";

    /// <summary><see cref="ItemKinds"/>: file | folder.</summary>
    public string Kind { get; set; } = "";

    public LocatorData Locator { get; set; } = new();

    /// <summary>Present when the scanner observes this item on its own: a folder walked, or a lone file stat'ed.</summary>
    public SourceData? Source { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsFolder => Kind == ItemKinds.Folder;
    [JsonIgnore] public bool IsFile => Kind == ItemKinds.File;
    [JsonIgnore] public bool IsSource => Source is not null;

    public ItemData Clone()
    {
        var copy = (ItemData)MemberwiseClone();
        copy.Locator = Locator.Clone();
        copy.Source = Source?.Clone();
        copy.Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra);
        return copy;
    }
}

/// <summary>What a source carries beyond being an item: the name the tab shows (a chip, the header of its files).</summary>
internal sealed class SourceData
{
    public string Name { get; set; } = "";

    public SourceData Clone() => (SourceData)MemberwiseClone();
}

/// <summary>A group: a name the person gave to a set of items, inside another group or at the top. Never a folder on disk.</summary>
internal sealed class GroupData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The group this one sits in; null = top level. One parent at most; a chain that comes back to itself is a cycle the reconciler reports.</summary>
    public string? ParentGroupId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public GroupData Clone()
    {
        var copy = (GroupData)MemberwiseClone();
        copy.Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra);
        return copy;
    }
}

/// <summary>An item belongs to a group. Many-to-many; never implied by the group's parent, never by a folder's contents.</summary>
internal sealed class MembershipData
{
    public string GroupId { get; set; } = "";
    public string ItemId { get; set; } = "";

    public MembershipData Clone() => (MembershipData)MemberwiseClone();
}

/// <summary>Where an item was last seen, and what identifies it beyond the path: the volume and the file id, and for a file its size and mtime.</summary>
internal sealed class LocatorData
{
    public string LastKnownPath { get; set; } = "";
    public string? VolumeSerial { get; set; }
    public string? FileId { get; set; }
    public long? Size { get; set; }
    public DateTime? Mtime { get; set; }

    public LocatorData Clone() => (LocatorData)MemberwiseClone();
}

internal static class ItemKinds
{
    public const string File = "file";
    public const string Folder = "folder";
    public static bool IsKnown(string kind) => kind is File or Folder;
}

/// <summary>The 1.x <c>manifest.json</c>, read only to migrate and to watch for roots added during a downgrade. Never written by the 2.x.</summary>
internal sealed class LegacyManifestV1
{
    public int SchemaVersion { get; set; } = 1;
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public List<LibraryRoot> Roots { get; set; } = [];
    public string? Player { get; set; }
    public bool AutoAddRoots { get; set; } = true;
    public bool AutoSync { get; set; } = true;
    public List<string> ExtraExtensions { get; set; } = [];
    public bool GroupByFolder { get; set; } = true;
    public DateTime? LastSync { get; set; }
}

/// <summary>A named folder of the 1.x catalogue: one name pointing at one real directory (the shape of <c>roots[]</c> in <c>manifest.json</c>).</summary>
internal sealed record LibraryRoot(string Name, string Path);
