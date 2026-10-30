namespace Pixie.Library.Catalog;

// The resolved state of the library: what the reconciler builds from the manifest, the index tree and a few
// probes, and what the tab shows. Immutable once built; a change means a new model.

/// <summary>"Does it exist on disk, as expected, right now?"</summary>
internal enum Presence
{
    Present,
    Missing,
    Inaccessible,
    TypeMismatch,
    /// <summary>Nothing could be observed: the volume is off, or the tree does not reach it and nothing was probed.</summary>
    Unknown,
}

/// <summary>"Does the item's locator still point at it?"</summary>
internal enum Resolution
{
    Resolved,
    Relocated,
    Replaced,
    Broken,
    Unverifiable,
    StructuralConflict,
}

internal enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>One thing the reconciler has to say about an item or a group. Several may hold at once.</summary>
internal sealed record Diagnostic(string Code, Severity Severity, string Message);

/// <summary>A persistent item resolved: where it is now, how sure, and what the tree knows of it.</summary>
internal sealed class ItemView
{
    internal ItemView(ItemData data)
    {
        Data = data;
        Id = data.Id;
    }

    public ItemData Data { get; }
    public string Id { get; }
    public bool IsFolder => Data.IsFolder;
    public bool IsFile => Data.IsFile;
    public bool IsSource => Data.IsSource;

    /// <summary>The source's name, else the file or folder name of where it points.</summary>
    public string Name => Data.Source?.Name ?? System.IO.Path.GetFileName(ResolvedPath ?? Data.Locator.LastKnownPath);

    /// <summary>Where the locator resolves on disk (relocated when it was found elsewhere), when the path was usable.</summary>
    public string? ResolvedPath { get; internal set; }
    public Presence Presence { get; internal set; } = Presence.Unknown;
    public Resolution Resolution { get; internal set; } = Resolution.Resolved;
    public ProbeResult? Probe { get; internal set; }

    /// <summary>The tree's node for it, when the last scan reached it.</summary>
    public FileNode? File { get; internal set; }
    public FolderNode? Folder { get; internal set; }

    /// <summary>A source: its own tree in the index (a folder walked on its own; null when covered by another source or never scanned).</summary>
    public SourceTree? Tree { get; internal set; }

    /// <summary>The groups it belongs to (attached ones), in manifest order.</summary>
    public IReadOnlyList<GroupView> Groups => _groups;
    internal List<GroupView> _groups = [];

    public IReadOnlyList<Diagnostic> Self => _self;
    internal List<Diagnostic> _self = [];
    public Severity Severity => _self.Select(d => d.Severity).DefaultIfEmpty(Severity.Info).Max();

    /// <summary>The tree has it where the locator says: what the tab lists comes from the tree.</summary>
    public bool Indexed => File is not null || Folder is not null;

    public override string ToString() => $"{Data.Kind} {Name} ({Id})";
}

/// <summary>A group resolved: its place in the group tree and its members.</summary>
internal sealed class GroupView
{
    internal GroupView(GroupData data)
    {
        Data = data;
        Id = data.Id;
    }

    public GroupData Data { get; }
    public string Id { get; }
    public string Name => Data.Name;
    public string? ParentGroupId => Data.ParentGroupId;

    public GroupView? Parent { get; internal set; }

    /// <summary>The groups inside this one, in manifest order.</summary>
    public IReadOnlyList<GroupView> Children => _children;
    internal List<GroupView> _children = [];

    /// <summary>The items with a membership in this group, in manifest order — never the children's members, never a folder's contents.</summary>
    public IReadOnlyList<ItemView> Members => _members;
    internal List<ItemView> _members = [];

    /// <summary>Reachable from the top through existing parents, without a cycle.</summary>
    public bool Attached { get; internal set; }

    /// <summary>Why it is out of the tree, when it is. Detached groups are listed at the top with this said.</summary>
    public string? DetachedReason { get; internal set; }

    public IReadOnlyList<Diagnostic> Self => _self;
    internal List<Diagnostic> _self = [];
    public Severity Severity => _self.Select(d => d.Severity).DefaultIfEmpty(Severity.Info).Max();

    /// <summary>This group and every group below it, depth-first.</summary>
    public IEnumerable<GroupView> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var deeper in child.SelfAndDescendants())
                yield return deeper;
        }
    }

    public override string ToString() => $"group {Name} ({Id})";
}

/// <summary>Something global for the warning line: a source that is off, a folder that refused, ids in conflict, a membership pointing nowhere.</summary>
internal sealed record Problem(string Code, Severity Severity, string Message, string? Id = null, string? Path = null);

internal sealed record ModelStatus(bool ScanPlanChanged, bool ReadOnly, Presence StorageRootPresence);

/// <summary>The resolved library, immutable.</summary>
internal sealed class LibraryModel
{
    internal LibraryModel(string storageRoot, SourceTree? storageRootTree, IReadOnlyList<ItemView> sources, IReadOnlyDictionary<string, ItemView> items,
        IReadOnlyList<GroupView> groups, IReadOnlyDictionary<string, GroupView> allGroups, IReadOnlyList<Problem> problems, ModelStatus status,
        ScanPlan plan, ManifestSnapshot manifest, IndexData index)
    {
        StorageRoot = storageRoot;
        StorageRootTree = storageRootTree;
        Sources = sources;
        Items = items;
        Groups = groups;
        AllGroups = allGroups;
        Problems = problems;
        Status = status;
        Plan = plan;
        Manifest = manifest;
        Index = index;
    }

    /// <summary>The library's own folder, resolved.</summary>
    public string StorageRoot { get; }

    /// <summary>Its tree in the index, when scanned.</summary>
    public SourceTree? StorageRootTree { get; }

    /// <summary>The items that are sources, in manifest order.</summary>
    public IReadOnlyList<ItemView> Sources { get; }

    /// <summary>Every item with a usable id.</summary>
    public IReadOnlyDictionary<string, ItemView> Items { get; }

    /// <summary>The top-level groups (no parent) and the detached ones, in manifest order.</summary>
    public IReadOnlyList<GroupView> Groups { get; }

    /// <summary>Every group with a usable id, attached or not.</summary>
    public IReadOnlyDictionary<string, GroupView> AllGroups { get; }

    public IReadOnlyList<Problem> Problems { get; }
    public ModelStatus Status { get; }
    public ScanPlan Plan { get; }
    public ManifestSnapshot Manifest { get; }
    public IndexData Index { get; }

    public ItemView? FindItem(string? id) => id is not null && Items.TryGetValue(id, out var item) ? item : null;
    public GroupView? FindGroup(string? id) => id is not null && AllGroups.TryGetValue(id, out var group) ? group : null;

    /// <summary>The item at this path, if one exists — by identity of the resolved path.</summary>
    public ItemView? ItemAt(string path)
        => Items.Values.FirstOrDefault(i => i.ResolvedPath is { } p && PathUtil.IsSamePath(p, path));
}

// ───── Actions: what the reconciler proposes; the application layer executes ─────

internal abstract record ReconcileAction;

/// <summary>A locator healed (Replaced: fingerprint; Relocated: path) — write it back.</summary>
internal sealed record RewriteLocator(string ItemId, LocatorData Locator) : ReconcileAction;

internal enum ScanReason
{
    ScanPlanChanged,
    Pending,
}

internal sealed record RequestScan(ScanReason Reason, bool Full) : ReconcileAction;

/// <summary>A Broken item: only a person can point it somewhere (Localizar…) or take it out of its groups.</summary>
internal sealed record RequestManualRelocation(string ItemId, string Reason) : ReconcileAction;

internal sealed record ReconcileResult(LibraryModel Model, IReadOnlyList<ReconcileAction> Actions);

/// <summary>Everything <see cref="Reconciler.Build"/> reads.</summary>
internal sealed record ReconcileInput(ManifestSnapshot Manifest, IndexData Index, ScanPlan Plan);
