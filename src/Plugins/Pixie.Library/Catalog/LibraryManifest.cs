using System.IO;
using System.Text.Json;

namespace Pixie.Library.Catalog;

/// <summary>
/// An immutable view of the manifest for the code that only reads it (the scan plan, the reconciler, the
/// operations' checks): items, groups and memberships cloned, the conflicted ids computed, the options copied.
/// Taken under the manifest's lock, so it is one consistent state.
/// </summary>
internal sealed class ManifestSnapshot
{
    internal ManifestSnapshot(ManifestData data, IReadOnlySet<string> conflictedIds, bool readOnly)
    {
        Status = data.Status;
        LastSync = data.LastSync;
        StorageRoot = data.StorageRoot;
        Player = data.Player;
        AutoAddRoots = data.AutoAddRoots;
        AutoSync = data.AutoSync;
        GroupByFolder = data.GroupByFolder;
        ExtraExtensions = data.ExtraExtensions.ToArray();
        LegacyV1 = data.LegacyV1;
        Items = data.Items.Select(i => i.Clone()).ToArray();
        Groups = data.Groups.Select(g => g.Clone()).ToArray();
        Memberships = data.Memberships.Select(m => m.Clone()).ToArray();
        ConflictedIds = conflictedIds;
        ReadOnly = readOnly;
        var items = new Dictionary<string, ItemData>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (!string.IsNullOrEmpty(item.Id) && !conflictedIds.Contains(item.Id))
                items[item.Id] = item;
        }
        var groups = new Dictionary<string, GroupData>(StringComparer.Ordinal);
        foreach (var group in Groups)
        {
            if (!string.IsNullOrEmpty(group.Id) && !conflictedIds.Contains(group.Id))
                groups[group.Id] = group;
        }
        _items = items;
        _groups = groups;
    }

    private readonly Dictionary<string, ItemData> _items;
    private readonly Dictionary<string, GroupData> _groups;

    public SyncStatus Status { get; }
    public DateTime? LastSync { get; }
    public string? StorageRoot { get; }
    public string? Player { get; }
    public bool AutoAddRoots { get; }
    public bool AutoSync { get; }
    public bool GroupByFolder { get; }
    public IReadOnlyList<string> ExtraExtensions { get; }
    public LegacyV1Data? LegacyV1 { get; }
    public IReadOnlyList<ItemData> Items { get; }
    public IReadOnlyList<GroupData> Groups { get; }
    public IReadOnlyList<MembershipData> Memberships { get; }

    /// <summary>Ids that appear on more than one item or group: none of them resolves until a person sorts it out.</summary>
    public IReadOnlySet<string> ConflictedIds { get; }

    public bool ReadOnly { get; }

    /// <summary>The items the scanner observes on their own, in manifest order.</summary>
    public IEnumerable<ItemData> Sources => Items.Where(i => i.IsSource && !ConflictedIds.Contains(i.Id));

    /// <summary>The one item with this id, or null when there is none — or more than one.</summary>
    public ItemData? FindItem(string? id) => id is not null && _items.TryGetValue(id, out var item) ? item : null;

    public GroupData? FindGroup(string? id) => id is not null && _groups.TryGetValue(id, out var group) ? group : null;

    /// <summary>The item whose locator points at this path, by identity of the path.</summary>
    public ItemData? FindItemByPath(string path)
        => Items.FirstOrDefault(i => !ConflictedIds.Contains(i.Id) && PathUtil.TryNormalizeAbsolute(i.Locator.LastKnownPath, out var p, out _) && PathUtil.IsSamePath(p, path));
}

/// <summary>
/// <c>manifest.v2.json</c> on disk, with its rules: every write goes through temp + rename after keeping the
/// previous file as <c>.bak</c>; an unreadable file is set aside as <c>.bad</c> and the <c>.bak</c> is tried before
/// starting empty; a file from a newer plugin is loaded read-only; a file of an older schema (the shape of the
/// unpublished 2.0 branch) is set aside and the 1.x manifest migrated again; a <c>syncing</c> found on disk
/// means the previous run died and becomes <c>failed</c>. The first load with only the 1.x <c>manifest.json</c>
/// around migrates it — and never writes it back, which is what keeps a downgrade safe. The in-memory
/// <see cref="Generation"/> is the sync runner's compare-and-swap token: a download landing mid-scan makes
/// the run go again.
/// </summary>
internal sealed class LibraryManifest
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly string _musicFolder;
    private ManifestData _data;
    private HashSet<string> _conflicted;
    private int _generation;

    private LibraryManifest(string path, string legacyPath, string musicFolder, ManifestData data, bool readOnly)
    {
        _path = path;
        _legacyPath = legacyPath;
        _musicFolder = musicFolder;
        _data = data;
        ReadOnly = readOnly;
        _conflicted = ConflictedIdsOf(data);
    }

    /// <summary>
    /// Reads <c>manifest.v2.json</c> in <paramref name="dataDirectory"/>, migrating from <c>manifest.json</c> when
    /// there is only that. <paramref name="musicFolder"/> is the system's Music folder — the storage root when
    /// none is configured, and the 1.x root the migration does not duplicate.
    /// </summary>
    public static LibraryManifest Load(string dataDirectory, string musicFolder, Action<string>? warn = null)
    {
        var path = System.IO.Path.Combine(dataDirectory, "manifest.v2.json");
        var legacyPath = System.IO.Path.Combine(dataDirectory, "manifest.json");
        ManifestData? data = null;
        var readOnly = false;
        var fresh = false;
        var restored = false;

        if (File.Exists(path))
        {
            data = TryRead(path, warn);
            if (data is null)
            {
                try { File.Move(path, path + ".bad", overwrite: true); } catch { /* the rewrite below replaces it anyway */ }
                if (File.Exists(path + ".bak") && TryRead(path + ".bak", warn) is { } backup)
                {
                    warn?.Invoke("manifest.v2.json ilegível; guardado como manifest.v2.json.bad e restaurado do .bak");
                    data = backup;
                    restored = true;   // written back below: with only the .bak on disk, the next start would migrate the 1.x file again
                }
                else
                {
                    warn?.Invoke("manifest.v2.json ilegível e sem .bak utilizável; guardado como manifest.v2.json.bad");
                }
            }
            else if (data.SchemaVersion > ManifestData.CurrentSchema)
            {
                warn?.Invoke($"manifest.v2.json é de uma versão mais nova do plugin (schema {data.SchemaVersion}); aberto só pra leitura — atualize o plugin");
                readOnly = true;
            }
        }

        if (data is null)
        {
            fresh = true;
            var legacy = File.Exists(legacyPath) ? LegacyMigration.TryReadV1(legacyPath, warn) : null;
            if (legacy is not null)
            {
                data = LegacyMigration.Migrate(legacy, DateTime.UtcNow, musicFolder);
                warn?.Invoke($"manifest.json (1.x) migrado para manifest.v2.json: {data.Items.Count(i => i.IsSource)} pasta(s) viraram fontes; o arquivo antigo fica como está");
            }
            else
            {
                data = new ManifestData();
            }
        }

        data.Items ??= [];
        data.Groups ??= [];
        data.Memberships ??= [];
        data.ExtraExtensions = (data.ExtraExtensions ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        foreach (var item in data.Items)
            item.Locator ??= new LocatorData();
        var interrupted = data.Status == SyncStatus.Syncing;
        if (interrupted)
            data.Status = SyncStatus.Failed;

        var manifest = new LibraryManifest(path, legacyPath, musicFolder, data, readOnly);
        if ((fresh || interrupted || restored) && !readOnly)
            manifest.Save();
        return manifest;
    }

    private static ManifestData? TryRead(string path, Action<string>? warn)
    {
        try
        {
            var data = JsonSerializer.Deserialize<ManifestData>(File.ReadAllText(path), LibraryJson.Manifest);
            if (data is null)
                throw new JsonException("vazio");
            if (data.SchemaVersion < ManifestData.CurrentSchema)
                throw new JsonException($"schema {data.SchemaVersion} (a forma antiga do 2.0, nunca publicada); este plugin escreve o {ManifestData.CurrentSchema}");
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    public string FilePath => _path;
    public string LegacyPath => _legacyPath;

    /// <summary>The system's Music folder as this manifest was loaded with: the storage root when none is configured.</summary>
    public string MusicFolder => _musicFolder;

    /// <summary>A file from a newer plugin: readable, never written.</summary>
    public bool ReadOnly { get; }

    /// <summary>The compare-and-swap token of the sync runner (in memory only).</summary>
    public int Generation { get { lock (_lock) return _generation; } }

    public SyncStatus Status { get { lock (_lock) return _data.Status; } }
    public DateTime? LastSync { get { lock (_lock) return _data.LastSync; } }
    public bool AutoAddRoots { get { lock (_lock) return _data.AutoAddRoots; } }
    public bool AutoSync { get { lock (_lock) return _data.AutoSync; } }
    public string? Player { get { lock (_lock) return _data.Player; } }
    public bool GroupByFolder { get { lock (_lock) return _data.GroupByFolder; } }
    public IReadOnlyList<string> ExtraExtensions { get { lock (_lock) return _data.ExtraExtensions.ToArray(); } }

    /// <summary>The storage root as configured, or the Music folder: normalized.</summary>
    public string StorageRoot { get { lock (_lock) return ScanPlan.StorageRootOf(_data.StorageRoot, _musicFolder); } }

    /// <summary>The full set for a scan: built-ins plus the extras, each as ".ext".</summary>
    public IReadOnlySet<string> Extensions { get { lock (_lock) return FileKinds.All(_data.ExtraExtensions); } }

    public IReadOnlySet<string> ConflictedIds { get { lock (_lock) return _conflicted.ToHashSet(StringComparer.Ordinal); } }

    public ManifestSnapshot Snapshot()
    {
        lock (_lock)
            return new ManifestSnapshot(_data, _conflicted.ToHashSet(StringComparer.Ordinal), ReadOnly);
    }

    /// <summary>Edits anything but the flag (items, groups, options) and saves. Refused on a read-only manifest.</summary>
    public void Update(Action<ManifestData> change)
    {
        lock (_lock)
        {
            EnsureWritable();
            change(_data);
            _conflicted = ConflictedIdsOf(_data);
            Save();
        }
    }

    /// <summary>Something changed (a download, an import, a folder): the index is behind. Bumps the generation.</summary>
    public void MarkPending()
    {
        lock (_lock)
        {
            _generation++;
            _data.Status = SyncStatus.Pending;
            SaveIfWritable();
        }
    }

    /// <summary>A sync started. Does not touch the generation — that is the sync's own token.</summary>
    public void MarkSyncing()
    {
        lock (_lock)
        {
            _data.Status = SyncStatus.Syncing;
            SaveIfWritable();
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
            SaveIfWritable();
            return true;
        }
    }

    /// <summary>The sync stopped before writing the index (error, cancel): the next run is a full one.</summary>
    public void MarkFailed()
    {
        lock (_lock)
        {
            _data.Status = SyncStatus.Failed;
            SaveIfWritable();
        }
    }

    private void EnsureWritable()
    {
        if (ReadOnly)
            throw new InvalidOperationException("o manifesto é de uma versão mais nova do plugin e está aberto só pra leitura");
    }

    private void SaveIfWritable()
    {
        if (!ReadOnly)
            Save();
    }

    private void Save()
    {
        try
        {
            if (File.Exists(_path))
                File.Copy(_path, _path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no backup this time; the write below is still atomic
        }
        AtomicFile.Write(_path, JsonSerializer.Serialize(_data, LibraryJson.Manifest));
    }

    /// <summary>An id used by more than one item or group — items and groups share the id space, so a collision across them counts too.</summary>
    private static HashSet<string> ConflictedIdsOf(ManifestData data)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in data.Items.Select(i => i.Id).Concat(data.Groups.Select(g => g.Id)))
        {
            if (string.IsNullOrEmpty(id))
                continue;
            if (!seen.Add(id))
                conflicted.Add(id);
        }
        return conflicted;
    }
}
