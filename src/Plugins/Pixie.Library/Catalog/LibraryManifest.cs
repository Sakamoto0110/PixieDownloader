using System.IO;
using System.Text.Json;

namespace Pixie.Library.Catalog;

/// <summary>
/// The manifest file and the state machine around its flag. Every change goes to disk at once, by
/// write-temp-and-rename, so a crash leaves the previous manifest, never a broken one. The flag is the
/// contract between the sync and everything else: a download or a folder change flips it to <c>pending</c>
/// (<see cref="MarkPending"/>, which also bumps <see cref="Generation"/>), and the sync only writes
/// <c>synced</c> if the generation is still the one it read when it started (<see cref="TryMarkSynced"/>) —
/// otherwise something arrived mid-scan and the flag stays pending for the next run.
/// </summary>
internal sealed class LibraryManifest
{
    private readonly object _lock = new();
    private readonly string _path;
    private ManifestData _data;
    private int _generation;

    private LibraryManifest(string path, ManifestData data)
    {
        _path = path;
        _data = data;
    }

    /// <summary>
    /// Reads <paramref name="path"/>. Missing → a fresh manifest in <c>pending</c>, written right away (the first
    /// opening must not show an empty catalogue as if that were the truth). Unreadable → set aside as
    /// <c>manifest.json.bad</c> and started over. <c>syncing</c> on disk means the previous run died mid-way:
    /// it becomes <c>failed</c>, which makes the next run a full one.
    /// </summary>
    public static LibraryManifest Load(string path, Action<string>? warn = null)
    {
        ManifestData? data = null;
        if (File.Exists(path))
        {
            try
            {
                data = JsonSerializer.Deserialize<ManifestData>(File.ReadAllText(path), LibraryJson.Manifest);
                if (data is null)
                    throw new JsonException("vazio");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warn?.Invoke($"manifest.json ilegível ({ex.Message}); guardado como manifest.json.bad e recriado");
                try { File.Move(path, path + ".bad", overwrite: true); } catch { /* the rewrite below replaces it anyway */ }
                data = null;
            }
        }

        var fresh = data is null;
        data ??= new ManifestData();
        data.Roots = data.Roots.Where(r => !string.IsNullOrWhiteSpace(r.Path)).ToList();
        data.ExtraExtensions = data.ExtraExtensions.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        var interrupted = data.Status == SyncStatus.Syncing;
        if (interrupted)
            data.Status = SyncStatus.Failed;

        var manifest = new LibraryManifest(path, data);
        if (fresh || interrupted)
            manifest.Save();
        return manifest;
    }

    public string Path => _path;

    /// <summary>Bumped by every <see cref="MarkPending"/>; the sync's compare-and-swap token.</summary>
    public int Generation { get { lock (_lock) return _generation; } }

    public SyncStatus Status { get { lock (_lock) return _data.Status; } }

    public DateTime? LastSync { get { lock (_lock) return _data.LastSync; } }

    public bool AutoAddRoots { get { lock (_lock) return _data.AutoAddRoots; } }

    public bool AutoSync { get { lock (_lock) return _data.AutoSync; } }

    public string? Player { get { lock (_lock) return _data.Player; } }

    public bool GroupByFolder { get { lock (_lock) return _data.GroupByFolder; } }

    public IReadOnlyList<LibraryRoot> Roots { get { lock (_lock) return _data.Roots.ToArray(); } }

    public IReadOnlyList<string> ExtraExtensions { get { lock (_lock) return _data.ExtraExtensions.ToArray(); } }

    /// <summary>The scan's extension set: built-ins plus the extras.</summary>
    public IReadOnlySet<string> Extensions { get { lock (_lock) return FileKinds.All(_data.ExtraExtensions); } }

    /// <summary>Edits anything but the flag (folders, player, options) and saves.</summary>
    public void Update(Action<ManifestData> change)
    {
        lock (_lock)
        {
            change(_data);
            Save();
        }
    }

    /// <summary>The root that contains <paramref name="filePath"/> — the deepest one, when roots nest.</summary>
    public LibraryRoot? RootOf(string filePath)
    {
        lock (_lock)
            return _data.Roots
                .Where(r => PathUtil.IsUnder(filePath, r.Path))
                .OrderByDescending(r => r.Path.Length)
                .FirstOrDefault();
    }

    /// <summary>Something changed (a download, a folder): the index is behind. Bumps the generation.</summary>
    public void MarkPending()
    {
        lock (_lock)
        {
            _generation++;
            _data.Status = SyncStatus.Pending;
            Save();
        }
    }

    /// <summary>A sync started. Does not touch the generation — that is the sync's own token.</summary>
    public void MarkSyncing()
    {
        lock (_lock)
        {
            _data.Status = SyncStatus.Syncing;
            Save();
        }
    }

    /// <summary>
    /// The sync finished and wrote the index. Only becomes <c>synced</c> if nothing flipped the flag since
    /// <paramref name="generation"/> was read; otherwise the flag is left as it is (pending) and this returns false.
    /// </summary>
    public bool TryMarkSynced(int generation, DateTime when)
    {
        lock (_lock)
        {
            if (_generation != generation)
                return false;
            _data.Status = SyncStatus.Synced;
            _data.LastSync = when;
            Save();
            return true;
        }
    }

    /// <summary>The sync stopped before writing the index (error, cancel): the next run is a full one.</summary>
    public void MarkFailed()
    {
        lock (_lock)
        {
            _data.Status = SyncStatus.Failed;
            Save();
        }
    }

    private void Save()
        => AtomicFile.Write(_path, JsonSerializer.Serialize(_data, LibraryJson.Manifest));
}

internal static class PathUtil
{
    private static readonly char[] Separators = [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar];

    /// <summary>Full path without a trailing separator, so the same folder always compares equal.</summary>
    public static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Separators);
        return trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':') ? full : trimmed;   // keep "C:\" whole
    }

    /// <summary><paramref name="path"/> is <paramref name="directory"/> itself or somewhere below it.</summary>
    public static bool IsUnder(string path, string directory)
    {
        var dir = Normalize(directory);
        var full = Normalize(path);
        if (full.Equals(dir, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = dir.EndsWith(System.IO.Path.DirectorySeparatorChar) ? dir : dir + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path of <paramref name="path"/> relative to <paramref name="directory"/>, "" when it is the directory itself.</summary>
    public static string Relative(string path, string directory)
    {
        var rel = System.IO.Path.GetRelativePath(Normalize(directory), Normalize(path));
        return rel == "." ? "" : rel;
    }
}
