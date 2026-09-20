using Pixie.Library.Catalog;

namespace Pixie.Library;

/// <summary>What the projection is asked to show: the filters of the tab, the sort inside a container, which containers are open.</summary>
internal sealed record ProjectionOptions(
    /// <summary>The chip: only files under this root (null = every root).</summary>
    string? RootPath,
    FileKind? Kind,
    string Search,
    IComparer<LibraryRow> Sort,
    /// <summary>"Pastas": containers on; off = one flat list of files in the sort order.</summary>
    bool Grouped,
    /// <summary>Which container keys are open; while searching every container is.</summary>
    IReadOnlySet<string> Expanded,
    /// <summary>Files a scan just read (the inbox), shown before the model that has them arrives.</summary>
    IReadOnlyList<FileNode> Early)
{
    public bool Searching => !string.IsNullOrWhiteSpace(Search);
    public bool Filtering => Searching || RootPath is not null || Kind is not null;

    /// <summary>A group with nothing to show is still listed under a chip (the chip filters the physical side; the group has to be there for a drop), not under a search or a type.</summary>
    public bool HidesEmptyGroups => Searching || Kind is not null;
}

/// <summary>
/// Turns the model into the rows of the single list: "Tudo" — the storage root's tree with the other sources
/// as containers beside its folders — then the top-level groups, each opening on its child groups and its
/// members, a member folder opening on the same subtree of the index that "Tudo" shows. Only what is open is
/// materialised; the filters decide which files appear and a container appears when something below it does
/// (outside a filter every container is listed: a folder just created has to show up empty, a group has to be
/// there to receive a drop). Pure: the same model and options give the same rows.
/// </summary>
internal static class LibraryProjection
{
    public const string TopKey = "tudo";

    public static Projection Build(LibraryModel model, IReadOnlyList<SourceRef> sources, ProjectionOptions options)
    {
        var builder = new Builder(model, sources, options);
        var rows = options.Grouped ? builder.Tree() : builder.Flat();
        return new Projection(rows, builder.Files, builder.Seconds, builder.Bytes);
    }

    private sealed class Builder(LibraryModel model, IReadOnlyList<SourceRef> sources, ProjectionOptions options)
    {
        private readonly List<LibraryRow> _rows = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        /// <summary>The files the filters let through, open or not, each once: what the footer counts.</summary>
        public int Files { get; private set; }
        public double Seconds { get; private set; }
        public long Bytes { get; private set; }

        // ───── the tree ─────

        public List<LibraryRow> Tree()
        {
            var storage = model.StorageRootTree?.Root;
            var storageRef = sources.FirstOrDefault(s => s.IsStorageRoot);
            var count = 0;
            var children = new List<LibraryRow>();

            // The storage root's own folders, the other sources as containers beside them, then the files
            if (storage is not null)
                count += FolderRows(storage, TopKey, 1, storageRef, null, children);
            foreach (var item in model.Sources.Where(i => i.IsFolder))
            {
                if (item.Tree?.Root is not { } root)
                    continue;   // covered by another source (walked as part of its tree) or never scanned yet
                var source = sources.FirstOrDefault(s => s.ItemId == item.Id);
                var (row, below) = FolderRow(root, TopKey + "|s:" + item.Id, 1, source, null, item, isSourceRoot: true);
                if (row is not null)
                {
                    children.Add(row);
                    children.AddRange(below);
                    count += row.Count;
                }
            }
            var files = new List<LibraryRow>();
            if (storage is not null)
                FileRows(storage, TopKey, 1, storageRef, null, files);
            foreach (var item in model.Sources.Where(i => i.IsFile))
            {
                if (item.File is { } file && Take(file, RootOf(file.FullPath)))
                    files.Add(LibraryRow.ForFile(TopKey + "|" + PathUtil.Canonical(file.FullPath), 1, file, RootOf(file.FullPath), null, item));
            }
            files.Sort(options.Sort);
            children.AddRange(files);
            count += files.Count;
            foreach (var early in EarlyRows(TopKey, 1))
            {
                children.Add(early);
                count++;
            }

            if (!options.Filtering || count > 0)
            {
                var expanded = IsOpen(TopKey);
                _rows.Add(LibraryRow.ForTop(TopKey, model.StorageRoot, expanded, count));
                if (expanded)
                    _rows.AddRange(children);
            }

            foreach (var group in model.Groups)
                GroupInto(group, "g:" + group.Id, 0, _rows);
            return _rows;
        }

        /// <summary>The rows of a folder's children (folders as containers, then files), appended to <paramref name="into"/>; returns how many files the subtree shows.</summary>
        private int Children(FolderNode folder, string parentKey, int depth, SourceRef? source, GroupView? inGroup, List<LibraryRow> into)
            => FolderRows(folder, parentKey, depth, source, inGroup, into) + FileRows(folder, parentKey, depth, source, inGroup, into);

        private int FolderRows(FolderNode folder, string parentKey, int depth, SourceRef? source, GroupView? inGroup, List<LibraryRow> into)
        {
            var count = 0;
            foreach (var sub in folder.Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var (row, below) = FolderRow(sub, parentKey + "|" + PathUtil.Canonical(sub.FullPath), depth, source, inGroup, model.ItemAt(sub.FullPath), isSourceRoot: false);
                if (row is null)
                    continue;
                into.Add(row);
                into.AddRange(below);
                count += row.Count;
            }
            return count;
        }

        private int FileRows(FolderNode folder, string parentKey, int depth, SourceRef? source, GroupView? inGroup, List<LibraryRow> into)
        {
            var files = new List<LibraryRow>();
            foreach (var file in folder.Files)
            {
                if (Take(file, source))
                    files.Add(LibraryRow.ForFile(parentKey + "|" + PathUtil.Canonical(file.FullPath), depth, file, source, inGroup, model.ItemAt(file.FullPath)));
            }
            files.Sort(options.Sort);
            into.AddRange(files);
            return files.Count;
        }

        /// <summary>A folder as a container row plus, when open, the rows below it. Null when a filter leaves nothing under it.</summary>
        private (LibraryRow? Row, List<LibraryRow> Below) FolderRow(FolderNode folder, string key, int depth, SourceRef? source, GroupView? inGroup, ItemView? item, bool isSourceRoot)
        {
            var below = new List<LibraryRow>();
            var count = Children(folder, key, depth + 1, source, inGroup, below);
            if (options.Filtering && count == 0)
                return (null, below);
            var expanded = IsOpen(key);
            var row = LibraryRow.ForFolder(key, depth, folder, source, inGroup, item, expanded, count, isSourceRoot);
            return (row, expanded ? below : []);
        }

        /// <summary>A group's rows appended to <paramref name="into"/> (its own row first, then, when open, the child groups and the members); returns the files it shows.</summary>
        private int GroupInto(GroupView group, string key, int depth, List<LibraryRow> into)
        {
            var below = new List<LibraryRow>();
            var count = 0;
            foreach (var child in group.Children)
                count += GroupInto(child, key + "|g:" + child.Id, depth + 1, below);
            count += Members(group, key, depth + 1, below);
            if (options.HidesEmptyGroups && count == 0)
                return 0;
            var expanded = IsOpen(key);
            into.Add(LibraryRow.ForGroup(key, depth, group, expanded, count));
            if (expanded)
                into.AddRange(below);
            return count;
        }

        /// <summary>The members of a group: folders as containers over the index subtree, files, and what could not be found; returns the files shown.</summary>
        private int Members(GroupView group, string key, int depth, List<LibraryRow> into)
        {
            var count = 0;
            var folders = group.Members.Where(m => m.IsFolder).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var member in folders)
            {
                var memberKey = key + "|" + (member.Folder is { } f ? PathUtil.Canonical(f.FullPath) : "i:" + member.Id);
                if (member.Folder is { } folder)
                {
                    var (row, below) = FolderRow(folder, memberKey, depth, RootOf(folder.FullPath), group, member, isSourceRoot: member.IsSource);
                    if (row is null)
                        continue;
                    into.Add(row);
                    into.AddRange(below);
                    count += row.Count;
                }
                else if (!options.Filtering)
                {
                    into.Add(LibraryRow.ForMissing(memberKey, depth, member, group));
                }
            }
            var files = new List<LibraryRow>();
            var missing = new List<LibraryRow>();
            foreach (var member in group.Members.Where(m => m.IsFile))
            {
                var memberKey = key + "|" + (member.File is { } f ? PathUtil.Canonical(f.FullPath) : "i:" + member.Id);
                if (member.File is { } file)
                {
                    if (Take(file, RootOf(file.FullPath)))
                        files.Add(LibraryRow.ForFile(memberKey, depth, file, RootOf(file.FullPath), group, member));
                }
                else if (member.Presence == Presence.Present && member.ResolvedPath is not null)
                {
                    var row = LibraryRow.ForOutsideFile(memberKey, depth, member, group);
                    if (Passes(row))
                        files.Add(row);
                }
                else if (!options.Filtering)
                {
                    missing.Add(LibraryRow.ForMissing(memberKey, depth, member, group));
                }
            }
            files.Sort(options.Sort);
            into.AddRange(files);
            into.AddRange(missing);
            return count + files.Count;
        }

        // ───── the flat list ─────

        public List<LibraryRow> Flat()
        {
            var files = new List<LibraryRow>();
            foreach (var file in model.Index.Entries)
            {
                if (!_seen.Add(PathUtil.Canonical(file.FullPath)))
                    continue;
                var source = RootOf(file.FullPath);
                if (Take(file, source))
                    files.Add(LibraryRow.ForFile("f|" + PathUtil.Canonical(file.FullPath), 0, file, source, null, model.ItemAt(file.FullPath)));
            }
            foreach (var early in EarlyRows("f", 0))
                files.Add(early);
            files.Sort(options.Sort);
            return files;
        }

        // ───── helpers ─────

        /// <summary>Files a scan just read, not yet in the model: shown once, under the top, until the model catches up.</summary>
        private IEnumerable<LibraryRow> EarlyRows(string parentKey, int depth)
        {
            foreach (var file in options.Early)
            {
                var canonical = PathUtil.Canonical(file.FullPath);
                if (model.Index.FindFile(file.FullPath) is not null || !_seen.Add("early|" + canonical))
                    continue;
                var source = RootOf(file.FullPath);
                if (Take(file, source))
                    yield return LibraryRow.ForFile(parentKey + "|early|" + canonical, depth, file, source, null, null);
            }
        }

        private bool IsOpen(string key) => options.Searching || options.Expanded.Contains(key);

        /// <summary><see cref="Passes(FileNode, SourceRef?)"/>, and the file counted for the footer — once, wherever it shows.</summary>
        private bool Take(FileNode file, SourceRef? source)
        {
            if (!Passes(file, source))
                return false;
            if (_taken.Add(PathUtil.Canonical(file.FullPath)))
            {
                Files++;
                Seconds += file.Duration ?? 0;
                Bytes += file.Size;
            }
            return true;
        }

        private SourceRef? RootOf(string path)
            => sources.Where(r => PathUtil.IsDescendantOrSame(path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();

        private bool Passes(FileNode file, SourceRef? source)
        {
            if (options.RootPath is { } root && (source is null || !PathUtil.IsSamePath(source.Path, root)))
                return false;
            if (options.Kind is { } kind && FileKinds.KindOf(file.FullPath) != kind)
                return false;
            if (!options.Searching)
                return true;
            var key = SearchText.Normalize(string.Join(" ", new[] { file.Title, file.Artist, file.Album, file.Name, PathUtil.Relative(file.Parent?.FullPath ?? file.FullPath, source?.Path ?? file.Source.RootPath), source?.Name }.Where(s => !string.IsNullOrEmpty(s))!));
            return SearchText.Matches(key, options.Search);
        }

        private bool Passes(LibraryRow row)
        {
            if (options.RootPath is not null)
                return false;   // outside every source: no chip claims it
            if (options.Kind is { } kind && row.FileKind != kind)
                return false;
            return !options.Searching || SearchText.Matches(row.SearchKey, options.Search);
        }
    }
}

/// <summary>The rows of the list plus what the footer says: the files the filters let through (open or not), once each, with their totals.</summary>
internal sealed record Projection(List<LibraryRow> Rows, int Files, double Seconds, long Bytes);

/// <summary>The sort inside a container, as the "Ordem" combo names it.</summary>
public sealed record SortOption(string Label, IComparer<LibraryRow> Comparer)
{
    public override string ToString() => Label;

    public static IReadOnlyList<SortOption> All { get; } =
    [
        new("Recentes", Comparer<LibraryRow>.Create((a, b) => b.Added.CompareTo(a.Added) is var c && c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase))),
        new("Título", Comparer<LibraryRow>.Create((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase))),
        new("Artista", Comparer<LibraryRow>.Create((a, b) => string.Compare(a.Subtitle, b.Subtitle, StringComparison.CurrentCultureIgnoreCase) is var c && c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase))),
        new("Duração", Comparer<LibraryRow>.Create((a, b) => b.Duration.CompareTo(a.Duration))),
        new("Tamanho", Comparer<LibraryRow>.Create((a, b) => b.Size.CompareTo(a.Size))),
        new("Pasta", Comparer<LibraryRow>.Create((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase))),
    ];
}

/// <summary>One entry of the type combo.</summary>
public sealed record KindOption(string Label, FileKind? Kind)
{
    public override string ToString() => Label;

    public static IReadOnlyList<KindOption> All { get; } = [new("Tudo", null), new("Áudio", FileKind.Audio), new("Vídeo", FileKind.Video), new("Outros", FileKind.Other)];
}
