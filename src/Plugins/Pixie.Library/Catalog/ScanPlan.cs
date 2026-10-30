using System.IO;

namespace Pixie.Library.Catalog;

/// <summary>A resolved scan source: the item it came from (null for the storage root), its path, and whether it is walked or stat'ed.</summary>
internal sealed record ScanSource(string? ItemId, string Path, bool IsFolder);

/// <summary>
/// What the scanner observes — a function of the manifest and the environment, never persisted: the storage
/// root plus the folder sources, as the covering set (a folder inside another is walked by the outer one), the
/// file sources no folder covers, and the extensions. Deriving it is the whole of the "plan": a source that
/// came or went shows up as a tree to list or to drop; only the extensions and the scanner's rules, which the
/// index records, decide whether the next scan has to be a full one.
/// </summary>
internal sealed class ScanPlan
{
    private ScanPlan(string storageRoot, IReadOnlyList<ScanSource> folderSources, IReadOnlyList<ScanSource> fileSources, IReadOnlySet<string> extensions)
    {
        StorageRoot = storageRoot;
        FolderSources = folderSources;
        FileSources = fileSources;
        Extensions = extensions;
    }

    public string StorageRoot { get; }
    public IReadOnlyList<ScanSource> FolderSources { get; }
    public IReadOnlyList<ScanSource> FileSources { get; }
    public IReadOnlySet<string> Extensions { get; }

    public IEnumerable<ScanSource> Sources => FolderSources.Concat(FileSources);

    /// <summary>The storage root a manifest resolves to: its own, or the system's Music folder. Normalized.</summary>
    public static string StorageRootOf(string? storageRoot, string musicFolder)
        => PathUtil.Normalize(string.IsNullOrWhiteSpace(storageRoot) ? musicFolder : storageRoot);

    /// <summary>
    /// Derives the plan. Items that cannot be observed are left out: conflicted ids, unknown kinds, unusable
    /// locator paths — the reconciler reports those; the scanner must not walk them.
    /// </summary>
    public static ScanPlan Build(ManifestSnapshot manifest, string musicFolder)
    {
        var storageRoot = StorageRootOf(manifest.StorageRoot, musicFolder);
        var folders = new List<ScanSource> { new(null, storageRoot, true) };
        var files = new List<ScanSource>();
        foreach (var item in manifest.Sources)
        {
            if (!ItemKinds.IsKnown(item.Kind))
                continue;
            if (ResolvePath(item) is not { } path)
                continue;
            (item.IsFolder ? folders : files).Add(new ScanSource(item.Id, path, item.IsFolder));
        }

        // The covering set of folders: drop a folder another folder contains, and a second source on the same path.
        var coveringFolders = new List<ScanSource>();
        foreach (var folder in folders)
        {
            var covered = folders.Any(other => !ReferenceEquals(other, folder) && PathUtil.IsProperDescendant(folder.Path, other.Path));
            if (covered || coveringFolders.Any(kept => PathUtil.IsSamePath(kept.Path, folder.Path)))
                continue;
            coveringFolders.Add(folder);
        }
        var uncoveredFiles = new List<ScanSource>();
        foreach (var file in files)
        {
            if (folders.Any(folder => PathUtil.IsDescendantOrSame(file.Path, folder.Path)))
                continue;
            if (uncoveredFiles.Any(kept => PathUtil.IsSamePath(kept.Path, file.Path)))
                continue;
            uncoveredFiles.Add(file);
        }

        return new ScanPlan(storageRoot, coveringFolders, uncoveredFiles, FileKinds.All(manifest.ExtraExtensions));
    }

    /// <summary>Where an item's locator points, normalized, or null when the path is not usable (the reconciler names the reason).</summary>
    public static string? ResolvePath(ItemData item)
        => PathUtil.TryNormalizeAbsolute(item.Locator.LastKnownPath, out var path, out _) ? path : null;

    /// <summary>The source of the plan that observes this path — the deepest folder containing it, or the file source that is it — or null.</summary>
    public ScanSource? SourceOf(string path)
    {
        ScanSource? best = null;
        foreach (var folder in FolderSources)
        {
            if (PathUtil.IsDescendantOrSame(path, folder.Path) && (best is null || folder.Path.Length > best.Path.Length))
                best = folder;
        }
        return best ?? FileSources.FirstOrDefault(f => PathUtil.IsSamePath(f.Path, path));
    }

    /// <summary>Inside the storage root, where Importar copies to and Criar pasta creates.</summary>
    public bool IsInStorageRoot(string path) => PathUtil.IsDescendantOrSame(path, StorageRoot);
}
