using System.IO;
using System.Text.Json;

namespace Pixie.Library.Catalog;

/// <summary>
/// The plugin's files under <c>data\library\</c>, versioned by name so a 1.x and a 2.x never touch each other's:
/// <c>manifest.v2.json</c> (owned by <see cref="LibraryManifest"/>), <c>index.v2.json</c> (the tree, written
/// whole once per sync), <c>inbox.v2\</c> (the download cache — one small block per delivered file, waiting
/// to be folded into the index). <c>manifest.json</c>, <c>index.json</c> and <c>inbox\</c> belong to the 1.x;
/// the 2.x reads the first and the last, once, to migrate, and never writes them.
/// </summary>
internal sealed class LibraryStore
{
    public LibraryStore(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        ManifestPath = Path.Combine(dataDirectory, "manifest.v2.json");
        LegacyManifestPath = Path.Combine(dataDirectory, "manifest.json");
        IndexPath = Path.Combine(dataDirectory, "index.v2.json");
        LegacyInboxDirectory = Path.Combine(dataDirectory, "inbox");
        Inbox = new Inbox(Path.Combine(dataDirectory, "inbox.v2"));
    }

    public string DataDirectory { get; }
    public string ManifestPath { get; }
    public string LegacyManifestPath { get; }
    public string IndexPath { get; }
    public string LegacyInboxDirectory { get; }
    public Inbox Inbox { get; }

    /// <summary>
    /// The index as last written. Missing → empty. Unreadable, from another schema (older or newer: the tree is
    /// rebuilt, never converted) → set aside as <c>index.v2.json.bad</c> and empty, which makes the next scan see
    /// every file as new — the same as a full sync, no flag needed. Never touches the manifest.
    /// </summary>
    public IndexData ReadIndex(Action<string>? warn = null)
    {
        if (!File.Exists(IndexPath))
            return new IndexData();
        try
        {
            var data = JsonSerializer.Deserialize<IndexData>(File.ReadAllText(IndexPath), LibraryJson.Index);
            if (data is null)
                throw new JsonException("vazio");
            if (data.SchemaVersion != IndexData.CurrentSchema)
                throw new JsonException($"schema {data.SchemaVersion}, este plugin escreve o {IndexData.CurrentSchema}");
            data.Sources.RemoveAll(s => s is null || string.IsNullOrEmpty(s.RootPath) || (s.Root is null && s.File is null));
            data.Attach();
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"index.v2.json ilegível ({ex.Message}); guardado como index.v2.json.bad — a próxima varredura refaz tudo");
            try { File.Move(IndexPath, IndexPath + ".bad", overwrite: true); } catch { /* the next write replaces it */ }
            return new IndexData();
        }
    }

    public void WriteIndex(IndexData index)
        => AtomicFile.Write(IndexPath, JsonSerializer.Serialize(index, LibraryJson.Index));
}

/// <summary>
/// The download cache of the roadmap: each delivered file becomes one block — a file that either exists whole
/// or does not exist, written by temp-and-rename. The sync folds the blocks in first (so what was just
/// downloaded is the first thing read), writes the index, and only then deletes them: the reverse order would
/// lose the items if the index write failed. Folding a block twice is harmless — the index is keyed by path.
/// </summary>
internal sealed class Inbox(string directory)
{
    private int _sequence;

    public string Directory { get; } = directory;

    /// <summary>Records that <paramref name="filePath"/> was delivered. Never throws — the scan finds the file anyway.</summary>
    public void Append(string filePath, DateTime? now = null)
    {
        try
        {
            var block = new InboxBlockData { CreatedAt = now ?? DateTime.UtcNow, Files = [filePath] };
            var name = $"{block.CreatedAt:yyyyMMdd-HHmmss-fff}-{Interlocked.Increment(ref _sequence):D3}-{Guid.NewGuid():N}.json";
            AtomicFile.Write(Path.Combine(Directory, name), JsonSerializer.Serialize(block, LibraryJson.Manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the inbox is an optimisation: the file is still on disk, and the next scan lists it
        }
    }

    /// <summary>Every block, oldest first. A block that cannot be read is dropped on the spot: its file is on disk regardless.</summary>
    public IReadOnlyList<InboxBlock> ReadAll()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        var blocks = new List<InboxBlock>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var data = JsonSerializer.Deserialize<InboxBlockData>(File.ReadAllText(file), LibraryJson.Manifest);
                if (data is null)
                    throw new JsonException("vazio");
                blocks.Add(new InboxBlock(file, data.CreatedAt, data.Files.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray()));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                try { File.Delete(file); } catch { /* next time */ }
            }
        }
        return blocks;
    }

    /// <summary>After the index that contains them is on disk.</summary>
    public void Delete(IEnumerable<InboxBlock> blocks)
    {
        foreach (var block in blocks)
        {
            try { File.Delete(block.FilePath); } catch { /* folded again next time, harmlessly */ }
        }
    }

    private sealed class InboxBlockData
    {
        public DateTime CreatedAt { get; set; }
        public List<string> Files { get; set; } = [];
    }
}

/// <summary>One block of the inbox: the file it lives in, when it was written, and the delivered files it names.</summary>
internal sealed record InboxBlock(string FilePath, DateTime CreatedAt, IReadOnlyList<string> Files);
