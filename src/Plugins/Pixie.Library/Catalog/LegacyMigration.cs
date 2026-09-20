using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pixie.Library.Catalog;

/// <summary>
/// The 2.x reading the 1.x side, and only reading it: <c>manifest.json</c> is migrated once into
/// <c>manifest.v2.json</c> and then watched through the <c>legacyV1</c> snapshot — roots that appear in it after
/// the snapshot were added during a downgrade and are imported once; roots that disappear are not propagated;
/// nothing else of it is. A 1.x root that is the Music folder is the storage root itself and is not duplicated
/// as a source. The 1.x <c>inbox\</c> blocks are translated into <c>inbox.v2\</c> by file name, once each. The
/// 2.x never writes, renames or deletes a 1.x file.
/// </summary>
internal static class LegacyMigration
{
    public static LegacyManifestV1? TryReadV1(string path, Action<string>? warn = null)
    {
        try
        {
            var data = JsonSerializer.Deserialize<LegacyManifestV1>(File.ReadAllText(path), LibraryJson.Manifest);
            if (data is null)
                throw new JsonException("vazio");
            data.Roots = (data.Roots ?? []).Where(r => !string.IsNullOrWhiteSpace(r?.Path)).ToList();
            data.ExtraExtensions = (data.ExtraExtensions ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"manifest.json (1.x) ilegível ({ex.Message}); ignorado");
            return null;
        }
    }

    /// <summary>The first upgrade: every root but the Music folder becomes a source; the options come along; the snapshot is taken.</summary>
    public static ManifestData Migrate(LegacyManifestV1 v1, DateTime now, string musicFolder)
    {
        var data = new ManifestData
        {
            Status = SyncStatus.Pending,   // the first 2.x scan builds the tree
            Player = v1.Player,
            AutoAddRoots = v1.AutoAddRoots,
            AutoSync = v1.AutoSync,
            ExtraExtensions = v1.ExtraExtensions.ToList(),
            GroupByFolder = v1.GroupByFolder,
        };
        foreach (var root in v1.Roots)
        {
            if (!PathUtil.TryNormalizeAbsolute(root.Path, out var path, out _))
                continue;
            if (PathUtil.IsSamePath(path, musicFolder))
                continue;   // the storage root: always scanned, never a source of its own
            if (data.Items.Any(i => PathUtil.IsSamePath(i.Locator.LastKnownPath, path)))
                continue;
            data.Items.Add(NewSourceFolder(path, root.Name));
        }
        data.LegacyV1 = new LegacyV1Data
        {
            ObservedAt = now,
            ObservedRoots = CanonicalRoots(v1),
            Fingerprint = FingerprintOf(CanonicalRoots(v1)),
        };
        return data;
    }

    /// <summary>
    /// A later start: roots the 1.x gained since the snapshot (a downgrade in between) are added as sources
    /// unless an item already has that path (or it is the Music folder); removals are not propagated. Returns
    /// what was added. <paramref name="v1"/> null (file gone or unreadable) means nothing to do.
    /// </summary>
    public static IReadOnlyList<ItemData> SyncRootsSnapshot(LibraryManifest manifest, LegacyManifestV1? v1, DateTime now)
    {
        if (v1 is null || manifest.ReadOnly)
            return [];
        var current = CanonicalRoots(v1);
        var fingerprint = FingerprintOf(current);
        var snapshot = manifest.Snapshot();
        if (snapshot.LegacyV1?.Fingerprint == fingerprint)
            return [];

        var observed = new HashSet<string>(snapshot.LegacyV1?.ObservedRoots ?? [], StringComparer.Ordinal);
        var added = new List<ItemData>();
        foreach (var root in v1.Roots)
        {
            if (!PathUtil.TryNormalizeAbsolute(root.Path, out var path, out _))
                continue;
            if (observed.Contains(PathUtil.Canonical(path)))
                continue;
            if (PathUtil.IsSamePath(path, manifest.MusicFolder))
                continue;
            if (snapshot.FindItemByPath(path) is not null || added.Any(a => PathUtil.IsSamePath(a.Locator.LastKnownPath, path)))
                continue;
            added.Add(NewSourceFolder(path, root.Name));
        }
        manifest.Update(m =>
        {
            m.Items.AddRange(added);
            m.LegacyV1 ??= new LegacyV1Data();
            m.LegacyV1.ObservedAt = now;
            m.LegacyV1.ObservedRoots = current;
            m.LegacyV1.Fingerprint = fingerprint;
        });
        return added;
    }

    /// <summary>
    /// Translates the 1.x <c>inbox\</c> blocks the 2.x has not seen into <c>inbox.v2\</c>, replaying each file the
    /// way a delivered download is handled (<paramref name="deliver"/>), and remembers their names. The list of
    /// names is pruned to the blocks still on disk — the 1.x deletes the ones it consumed.
    /// </summary>
    public static int TranslateInbox(LibraryManifest manifest, string legacyInboxDirectory, Func<string, DateTime, bool> deliver, Action<string>? warn = null)
    {
        if (manifest.ReadOnly)
            return 0;
        var present = new List<string>();
        try
        {
            if (Directory.Exists(legacyInboxDirectory))
                present.AddRange(Directory.EnumerateFiles(legacyInboxDirectory, "*.json").Select(System.IO.Path.GetFileName)!.OrderBy(n => n, StringComparer.Ordinal)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"inbox (1.x) não pôde ser lido: {ex.Message}");
            return 0;
        }

        var snapshot = manifest.Snapshot();
        var observed = new HashSet<string>(snapshot.LegacyV1?.ObservedInbox ?? [], StringComparer.OrdinalIgnoreCase);
        var translated = 0;
        foreach (var name in present)
        {
            if (observed.Contains(name))
                continue;
            try
            {
                var block = JsonSerializer.Deserialize<LegacyInboxBlock>(File.ReadAllText(System.IO.Path.Combine(legacyInboxDirectory, name)), LibraryJson.Manifest);
                foreach (var file in block?.Files ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(file) && deliver(file, block!.CreatedAt))
                        translated++;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warn?.Invoke($"bloco {name} do inbox (1.x) ilegível ({ex.Message}); ignorado");
            }
            observed.Add(name);
        }

        var kept = observed.Where(n => present.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var before = snapshot.LegacyV1?.ObservedInbox ?? [];
        if (!kept.SequenceEqual(before, StringComparer.OrdinalIgnoreCase))
        {
            manifest.Update(m =>
            {
                m.LegacyV1 ??= new LegacyV1Data();
                m.LegacyV1.ObservedInbox = kept;
            });
        }
        return translated;
    }

    /// <summary>A folder source as a migration makes it: no fingerprint yet, the first probe fills the locator.</summary>
    public static ItemData NewSourceFolder(string path, string? name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = ItemKinds.Folder,
        Locator = new LocatorData { LastKnownPath = path },
        Source = new SourceData { Name = string.IsNullOrWhiteSpace(name) ? NameFor(path) : name.Trim() },
    };

    /// <summary>The folder's own name, or the whole path for a volume root.</summary>
    public static string NameFor(string path)
    {
        var name = System.IO.Path.GetFileName(PathUtil.Normalize(path));
        return string.IsNullOrEmpty(name) ? path.TrimEnd(System.IO.Path.DirectorySeparatorChar) : name;
    }

    private static List<string> CanonicalRoots(LegacyManifestV1 v1)
        => v1.Roots.Select(r => PathUtil.TryNormalizeAbsolute(r.Path, out var p, out _) ? PathUtil.Canonical(p) : null)
            .Where(p => p is not null).Select(p => p!).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private static string FingerprintOf(IEnumerable<string> canonicalRoots)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', canonicalRoots))));

    private sealed class LegacyInboxBlock
    {
        public DateTime CreatedAt { get; set; }
        public List<string> Files { get; set; } = [];
    }
}
