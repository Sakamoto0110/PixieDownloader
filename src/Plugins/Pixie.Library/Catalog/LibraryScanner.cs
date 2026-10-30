using System.IO;

namespace Pixie.Library.Catalog;

/// <summary>The NTFS identity of a listed file, as the scanner's identity delegate answers it (null when the handle could not be opened).</summary>
internal sealed record FileIdentityInfo(string? VolumeSerial, string? FileId)
{
    public static readonly FileIdentityInfo None = new(null, null);
}

/// <summary>What one scan is asked to do.</summary>
internal sealed record ScanRequest(
    ScanPlan Plan,
    IndexData Previous,
    bool Full,
    IReadOnlyList<string> PriorityFiles,
    Func<string, TagInfo?> ReadTags,
    Func<string, FileIdentityInfo> Identity);

/// <summary>
/// Progress of a scan. <see cref="Ready"/> carries files the UI may show before the scan ends — the
/// just-downloaded ones, read first.
/// </summary>
internal sealed record ScanProgress(string Phase, int Done, int Total, IReadOnlyList<FileNode>? Ready = null);

/// <summary>The new index plus what changed to get there.</summary>
internal sealed class ScanResult
{
    public required IndexData Index { get; init; }
    public required bool Full { get; init; }
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Removed { get; init; }
    public int TagsRead { get; init; }
    public int Unreadable { get; init; }

    /// <summary>Sources that were not there to observe (volume off, folder gone): their trees were kept.</summary>
    public IReadOnlyList<string> UnavailableSources { get; init; } = [];

    /// <summary>Folders that could not be stamped or listed: what the index knew of them was kept (inaccessible is not empty).</summary>
    public IReadOnlyList<string> InaccessibleDirectories { get; init; } = [];
}

/// <summary>
/// One pass over the sources of a <see cref="ScanPlan"/>, producing the next index tree from the previous one.
/// Pure in the sense that matters: it touches the file system read-only, reads tags and identities through the
/// delegates it is given, and returns a new <see cref="IndexData"/> instead of mutating anything.
/// <para>
/// Incremental by directory mtime: a folder whose mtime matches its node in the previous tree has the same
/// direct children it had (NTFS bumps it on create/delete/rename inside), so its files are kept without listing
/// it and its known subfolders are visited from the tree. A folder that changed, or is new, is listed. Tags are
/// read only for files whose identity — (size, mtime, and the file id when both sides know it) — is not what
/// the tree has. A full scan ignores the mtimes and lists everything; tags still go by identity.
/// </para>
/// <para>
/// Only a valid observation proves absence. A source that is not there (drive unplugged, folder gone) keeps its
/// tree and is marked <see cref="NodeStates.Unavailable"/>; a folder that cannot be stamped or listed keeps what
/// the tree knew of it and its subtree and is marked <see cref="NodeStates.Inaccessible"/> — inaccessible is not
/// empty. A file source whose folder exists but which is gone is a valid observation: it goes.
/// </para>
/// </summary>
internal static class LibraryScanner
{
    /// <summary>Bumped when what a scan observes changes rule; a tree made under other rules is scanned in full. 3 = the tree.</summary>
    public const int RulesVersion = 3;

    public static ScanResult Scan(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var previous = request.Previous;
        var index = new IndexData
        {
            Extensions = request.Plan.Extensions.Select(e => e.ToLowerInvariant()).OrderBy(e => e, StringComparer.Ordinal).ToList(),
            RulesVersion = RulesVersion,
        };
        var pending = new List<PendingFile>();
        var unavailable = new List<string>();
        var inaccessible = new List<string>();
        var listed = 0;

        foreach (var source in request.Plan.FolderSources)
        {
            ct.ThrowIfCancellationRequested();
            var old = FindSource(previous, source.Path);
            var tree = new SourceTree { RootPath = source.Path, Kind = SourceKinds.Folder, VolumeSerial = old?.VolumeSerial };
            if (!SafeDirectoryExists(source.Path))
            {
                tree.State = NodeStates.Unavailable;
                tree.Root = old?.Root is { } kept ? CloneDeep(kept) : new FolderNode { Name = NameOf(source.Path) };
                unavailable.Add(source.Path);
            }
            else
            {
                var walker = new Walker(request, tree, pending, inaccessible, ct);
                tree.Root = walker.Walk(source.Path, NameOf(source.Path), old?.Root);
                listed += walker.Listed;
            }
            index.Sources.Add(tree);
            progress?.Report(new ScanProgress("listando", listed, 0));
        }

        foreach (var source in request.Plan.FileSources)
        {
            ct.ThrowIfCancellationRequested();
            var old = FindSource(previous, source.Path);
            var tree = new SourceTree { RootPath = source.Path, Kind = SourceKinds.File, VolumeSerial = old?.VolumeSerial };
            try
            {
                var file = new FileInfo(source.Path);
                if (file.Exists)
                {
                    var identity = SafeIdentity(request.Identity, file.FullName);
                    tree.VolumeSerial = identity.VolumeSerial ?? tree.VolumeSerial;
                    tree.File = Observe(file, identity, old?.File, pending);
                }
                else if (file.Directory?.Exists != true)
                {
                    tree.State = NodeStates.Unavailable;   // the folder around it is not there to say: keep what we knew
                    tree.File = old?.File?.Clone();
                    unavailable.Add(source.Path);
                }
                else
                {
                    continue;   // the folder is there and the file is not — a valid observation, it goes
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tree.State = NodeStates.Unavailable;
                tree.File = old?.File?.Clone();
                inaccessible.Add(source.Path);
            }
            if (tree.File is null)
                continue;   // nothing known of an unavailable file: nothing to keep
            index.Sources.Add(tree);
        }

        index.Attach();   // paths exist from here on: the pending files are read by them, and Ready carries nodes the tab can show

        // The inbox first: what was just downloaded is what the user is waiting to see.
        var priority = new HashSet<string>(request.PriorityFiles.Select(PathUtil.Canonical), StringComparer.Ordinal);
        var ordered = pending.Where(p => priority.Contains(PathUtil.Canonical(p.Path))).Concat(pending.Where(p => !priority.Contains(PathUtil.Canonical(p.Path)))).ToList();
        var priorityCount = ordered.Count(p => priority.Contains(PathUtil.Canonical(p.Path)));

        int added = 0, changed = 0, unreadable = 0, done = 0;
        var ready = new List<FileNode>();
        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var tags = SafeRead(request.ReadTags, item.Path);
            if (tags is null)
                unreadable++;
            var node = item.Node;
            node.Title = tags?.Title;
            node.Artist = tags?.Artist;
            node.Album = tags?.Album;
            node.Duration = tags?.DurationSeconds;
            node.Bitrate = tags?.Bitrate;
            node.Chapters = tags?.Chapters ?? 0;
            if (item.Known)
                changed++;
            else
                added++;
            done++;
            if (priorityCount > 0 && done <= priorityCount)
            {
                ready.Add(node);
                progress?.Report(new ScanProgress("lendo tags", done, ordered.Count, done == priorityCount ? ready.ToArray() : null));
            }
            else if (done % 10 == 0 || done == ordered.Count)
            {
                progress?.Report(new ScanProgress("lendo tags", done, ordered.Count));
            }
        }

        var before = new HashSet<string>(previous.Entries.Select(e => PathUtil.Canonical(e.FullPath)), StringComparer.Ordinal);
        var now = new HashSet<string>(index.Entries.Select(e => PathUtil.Canonical(e.FullPath)), StringComparer.Ordinal);
        index.ScannedAt = DateTime.UtcNow;
        return new ScanResult
        {
            Index = index,
            Full = request.Full,
            Added = added,
            Changed = changed,
            Removed = before.Count(p => !now.Contains(p)),
            TagsRead = ordered.Count,
            Unreadable = unreadable,
            UnavailableSources = unavailable,
            InaccessibleDirectories = inaccessible.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>
    /// The cheap question the tab asks when it opens: does any known folder look different from the tree? One
    /// <c>stat</c> per folder. A folder source the tree has never seen (or saw unavailable) counts as changed if
    /// it exists now; a folder that is missing or cannot be read does not — nothing can be observed there, and
    /// asking again every few seconds would only spin. A file source counts as changed when its size or mtime
    /// differ from its node, or when it is new and exists.
    /// </summary>
    public static bool DirectoriesChanged(IndexData index, ScanPlan plan)
    {
        foreach (var source in plan.FolderSources)
        {
            var tree = FindSource(index, source.Path);
            if (tree?.Root is null || tree.IsUnavailable)
            {
                if (SafeDirectoryExists(source.Path))
                    return true;   // a source the tree has never seen, with a folder to scan
                continue;
            }
            foreach (var folder in tree.Folders)
            {
                if (folder.IsInaccessible)
                    continue;
                try
                {
                    var path = folder.FullPath;
                    if (Directory.Exists(path) && Directory.GetLastWriteTimeUtc(path) != folder.Modified)
                        return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // cannot be observed right now: not a change
                }
            }
        }
        foreach (var source in plan.FileSources)
        {
            try
            {
                var file = new FileInfo(source.Path);
                if (!file.Exists)
                    continue;
                var node = FindSource(index, source.Path)?.File;
                if (node is null || node.Size != file.Length || node.Modified != file.LastWriteTimeUtc)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // idem
            }
        }
        return false;
    }

    /// <summary>The tree was made with other extensions or other scanner rules: only a full scan brings it up to date. A tree that never came from a scan has nothing to compare.</summary>
    public static bool RulesChanged(IndexData index, ScanPlan plan)
    {
        if (index.ScannedAt is null)
            return false;
        if (index.RulesVersion != RulesVersion)
            return true;
        var stored = new HashSet<string>(index.Extensions, StringComparer.OrdinalIgnoreCase);
        return !stored.SetEquals(plan.Extensions);
    }

    /// <summary>The source tree of this path in the index, by identity of the path.</summary>
    public static SourceTree? FindSource(IndexData index, string path)
        => index.Sources.FirstOrDefault(s => PathUtil.IsSamePath(s.RootPath, path));

    /// <summary>The name a source's root node shows: the folder's own, or the whole path for a volume root ("C:\").</summary>
    internal static string NameOf(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static FileNode Observe(FileInfo file, FileIdentityInfo identity, FileNode? old, List<PendingFile> pending)
    {
        if (old is not null && old.SameFileAs(file.Length, file.LastWriteTimeUtc, identity.FileId))
        {
            var kept = old.Clone();
            kept.Name = file.Name;
            kept.FileId = identity.FileId ?? old.FileId;
            return kept;
        }
        var fresh = new FileNode { Name = file.Name, Size = file.Length, Modified = file.LastWriteTimeUtc, Created = file.CreationTimeUtc, FileId = identity.FileId };
        pending.Add(new PendingFile(fresh, file.FullName, old is not null));
        return fresh;
    }

    private static FolderNode CloneDeep(FolderNode node)
    {
        var copy = node.CloneShallow();
        foreach (var folder in node.Folders)
            copy.Folders.Add(CloneDeep(folder));
        foreach (var file in node.Files)
            copy.Files.Add(file.Clone());
        return copy;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static TagInfo? SafeRead(Func<string, TagInfo?> readTags, string path)
    {
        try
        {
            return readTags(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static FileIdentityInfo SafeIdentity(Func<string, FileIdentityInfo> identity, string path)
    {
        try
        {
            return identity(path) ?? FileIdentityInfo.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileIdentityInfo.None;
        }
    }

    /// <summary>A file whose tags are read in the second phase: the node already in the tree, where it is, and whether the previous tree had it.</summary>
    private sealed record PendingFile(FileNode Node, string Path, bool Known);

    /// <summary>The recursive walk of one folder source, with the per-scan state it needs in one place.</summary>
    private sealed class Walker(ScanRequest request, SourceTree tree, List<PendingFile> pending, List<string> inaccessible, CancellationToken ct)
    {
        public int Listed { get; private set; }

        public FolderNode Walk(string dir, string name, FolderNode? old)
        {
            ct.ThrowIfCancellationRequested();

            DateTime mtime;
            try
            {
                mtime = Directory.GetLastWriteTimeUtc(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Preserve(dir, name, old);
            }

            var node = new FolderNode { Name = name, Modified = mtime };
            if (!request.Full && old is not null && old.Modified == mtime)
            {
                // Same direct children as last time: keep the files, descend into the subfolders we knew.
                foreach (var file in old.Files)
                    node.Files.Add(file.Clone());
                foreach (var child in old.Folders)
                    node.Folders.Add(Walk(Path.Combine(dir, child.Name), child.Name, child));
                return node;
            }

            List<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Preserve(dir, name, old);
            }

            Listed++;
            var oldFolders = old?.Folders.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var oldFiles = old?.Files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var info in children)
            {
                if (info is DirectoryInfo sub)
                {
                    if ((sub.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0)
                        continue;   // junctions and symlinks can loop; system folders are not music
                    if (sub.Name.StartsWith(".~", StringComparison.Ordinal))
                        continue;   // the app's own temporaries (an import being copied in)
                    node.Folders.Add(Walk(sub.FullName, sub.Name, oldFolders?.GetValueOrDefault(sub.Name)));
                }
                else if (info is FileInfo file && request.Plan.Extensions.Contains(file.Extension))
                {
                    // The identity of every listed file is asked here (one handle each; nothing for what was not listed).
                    var identity = SafeIdentity(request.Identity, file.FullName);
                    tree.VolumeSerial = identity.VolumeSerial ?? tree.VolumeSerial;
                    node.Files.Add(Observe(file, identity, oldFiles?.GetValueOrDefault(file.Name), pending));
                }
            }
            return node;
        }

        /// <summary>A folder that refused to be stamped or listed: reported, and what the tree knew of it and of its subtree is kept.</summary>
        private FolderNode Preserve(string dir, string name, FolderNode? old)
        {
            inaccessible.Add(dir);
            var node = old is null ? new FolderNode { Name = name } : CloneDeep(old);
            node.Name = name;
            node.State = NodeStates.Inaccessible;
            return node;
        }
    }
}
