using System.IO;

namespace Pixie.Library.Catalog;

/// <summary>What one scan is asked to do.</summary>
internal sealed record ScanRequest(
    IReadOnlyList<LibraryRoot> Roots,
    IndexData Previous,
    bool Full,
    IReadOnlySet<string> Extensions,
    IReadOnlyList<string> PriorityFiles,
    Func<string, TagInfo?> ReadTags);

/// <summary>
/// Progress of a scan. <see cref="Ready"/> carries entries the UI may show before the scan ends — the
/// just-downloaded files, folded in first.
/// </summary>
internal sealed record ScanProgress(string Phase, int Done, int Total, IReadOnlyList<LibraryEntry>? Ready = null);

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
    public IReadOnlyList<string> UnavailableRoots { get; init; } = [];
}

/// <summary>
/// One pass over the roots, producing the next index from the previous one. Pure in the sense that matters: it
/// touches the file system read-only, reads tags through the delegate it is given, and returns a new
/// <see cref="IndexData"/> instead of mutating anything.
/// <para>
/// Incremental by directory mtime: a directory whose mtime matches the previous index has the same direct
/// children it had (NTFS bumps it on create/delete/rename inside), so its entries are reused without listing
/// it and its known subdirectories are visited from the index. A directory that changed, or is new, is listed.
/// Tags are read only for files whose (size, mtime) is not what the index has. A full scan ignores the stamps
/// and lists everything; tags still go by identity — a file that did not change did not change its tags.
/// </para>
/// <para>
/// A root that is not there (drive unplugged, share offline) keeps its entries and stamps untouched and is
/// reported in <see cref="ScanResult.UnavailableRoots"/> — nothing is forgotten because a disk is off.
/// </para>
/// </summary>
internal static class LibraryScanner
{
    public static ScanResult Scan(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var previous = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in request.Previous.Entries)
            previous.TryAdd(e.Path, e);
        var previousStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in request.Previous.Directories)
            previousStamps.TryAdd(d.Path, d.Modified);

        var previousByDir = previous.Values
            .GroupBy(e => Path.GetDirectoryName(e.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var previousChildDirs = previousStamps.Keys
            .Select(p => (Parent: Path.GetDirectoryName(p) ?? "", Path: p))
            .GroupBy(x => x.Parent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Path).ToList(), StringComparer.OrdinalIgnoreCase);

        var current = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var found = new List<FoundFile>();
        var unavailable = new List<string>();
        var listed = 0;

        var walker = new Walker(request, previousByDir, previousChildDirs, previousStamps, current, stamps, found, ct);
        foreach (var root in request.Roots)
        {
            var rootPath = PathUtil.Normalize(root.Path);
            if (!Directory.Exists(rootPath))
            {
                unavailable.Add(root.Path);
                foreach (var e in previous.Values.Where(e => PathUtil.IsUnder(e.Path, rootPath)))
                    current.TryAdd(e.Path, e);
                foreach (var (dir, mtime) in previousStamps.Where(s => PathUtil.IsUnder(s.Key, rootPath)))
                    stamps.TryAdd(dir, mtime);
                continue;
            }
            walker.Walk(rootPath, ref listed);
            progress?.Report(new ScanProgress("listando", listed, 0));
        }

        // Decide, file by file, what needs its tags read: new files, and files whose identity moved.
        var toRead = new List<FoundFile>();
        foreach (var f in found)
        {
            if (current.ContainsKey(f.Path))
                continue;   // seen twice (nested roots): the first pass decided
            if (previous.TryGetValue(f.Path, out var old) && old.SameFileAs(f.Size, f.Modified))
                current[f.Path] = old;
            else
                toRead.Add(f);
        }

        // The inbox first: what was just downloaded is what the user is waiting to see.
        var priority = new HashSet<string>(request.PriorityFiles, StringComparer.OrdinalIgnoreCase);
        var ordered = toRead.Where(f => priority.Contains(f.Path)).Concat(toRead.Where(f => !priority.Contains(f.Path))).ToList();
        var priorityCount = ordered.Count(f => priority.Contains(f.Path));

        int added = 0, changed = 0, unreadable = 0, done = 0;
        var ready = new List<LibraryEntry>();
        foreach (var f in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var tags = SafeRead(request.ReadTags, f.Path);
            if (tags is null)
                unreadable++;
            var entry = new LibraryEntry
            {
                Path = f.Path,
                Size = f.Size,
                Modified = f.Modified,
                Created = f.Created,
                Title = tags?.Title,
                Artist = tags?.Artist,
                Album = tags?.Album,
                Duration = tags?.DurationSeconds,
                Bitrate = tags?.Bitrate,
                Chapters = tags?.Chapters ?? 0,
            };
            current[f.Path] = entry;
            if (previous.ContainsKey(f.Path))
                changed++;
            else
                added++;
            done++;
            if (priorityCount > 0 && done <= priorityCount)
            {
                ready.Add(entry);
                progress?.Report(new ScanProgress("lendo tags", done, ordered.Count, done == priorityCount ? ready.ToArray() : null));
            }
            else if (done % 10 == 0 || done == ordered.Count)
            {
                progress?.Report(new ScanProgress("lendo tags", done, ordered.Count));
            }
        }

        var removed = previous.Keys.Count(k => !current.ContainsKey(k));
        var index = new IndexData
        {
            ScannedAt = DateTime.UtcNow,
            Directories = stamps.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => new DirectoryStamp(s.Key, s.Value)).ToList(),
            Entries = current.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        return new ScanResult
        {
            Index = index,
            Full = request.Full,
            Added = added,
            Changed = changed,
            Removed = removed,
            TagsRead = ordered.Count,
            Unreadable = unreadable,
            UnavailableRoots = unavailable,
        };
    }

    /// <summary>
    /// The cheap question the tab asks when it opens: does any known directory look different from the index?
    /// One <c>stat</c> per directory. A root the index has never seen counts as changed; a missing directory
    /// counts as changed too (a drive that was there and is not).
    /// </summary>
    public static bool DirectoriesChanged(IndexData index, IReadOnlyList<LibraryRoot> roots)
    {
        var known = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in index.Directories)
            known.TryAdd(d.Path, d.Modified);
        foreach (var root in roots)
        {
            var path = PathUtil.Normalize(root.Path);
            if (known.ContainsKey(path))
                continue;
            if (Directory.Exists(path))
                return true;   // a root the index has never seen, with a folder to scan
            // a new root that is not there yet changes nothing
        }
        foreach (var (dir, mtime) in known)
        {
            if (!Directory.Exists(dir) || Directory.GetLastWriteTimeUtc(dir) != mtime)
                return true;
        }
        return false;
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

    private sealed record FoundFile(string Path, long Size, DateTime Modified, DateTime Created);

    /// <summary>The recursive walk, with the per-scan state it needs in one place.</summary>
    private sealed class Walker(
        ScanRequest request,
        Dictionary<string, List<LibraryEntry>> previousByDir,
        Dictionary<string, List<string>> previousChildDirs,
        Dictionary<string, DateTime> previousStamps,
        Dictionary<string, LibraryEntry> current,
        Dictionary<string, DateTime> stamps,
        List<FoundFile> found,
        CancellationToken ct)
    {
        public void Walk(string dir, ref int listed)
        {
            ct.ThrowIfCancellationRequested();
            if (stamps.ContainsKey(dir))
                return;   // nested roots: already walked

            DateTime mtime;
            try
            {
                mtime = Directory.GetLastWriteTimeUtc(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            stamps[dir] = mtime;

            if (!request.Full && previousStamps.TryGetValue(dir, out var old) && old == mtime)
            {
                // Same direct children as last time: reuse the entries, descend into the subdirectories we knew.
                if (previousByDir.TryGetValue(dir, out var entries))
                    foreach (var e in entries)
                        current.TryAdd(e.Path, e);
                if (previousChildDirs.TryGetValue(dir, out var knownChildren))
                    foreach (var child in knownChildren)
                        Walk(child, ref listed);
                return;
            }

            try
            {
                var children = new DirectoryInfo(dir).EnumerateFileSystemInfos();
                listed++;
                foreach (var info in children)
                {
                    if (info is DirectoryInfo sub)
                    {
                        if ((sub.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0)
                            continue;   // junctions and symlinks can loop; system folders are not music
                        Walk(sub.FullName, ref listed);
                    }
                    else if (info is FileInfo file && request.Extensions.Contains(file.Extension))
                    {
                        found.Add(new FoundFile(file.FullName, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder we cannot list is a folder with nothing in it, as far as the catalogue goes
            }
        }
    }
}
