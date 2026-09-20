using System.IO;

namespace Pixie.Library.Catalog;

/// <summary>
/// The copy an import makes into the storage root: to a temporary folder at the top of the root, validated,
/// then renamed into place (same volume: atomic); the source is never touched; a failure leaves nothing. Also
/// the names that keep a copy from landing on something already there — never overwrite, never merge.
/// </summary>
internal static class StorageCopy
{
    public const string ImportTempPrefix = ".~import-";

    /// <summary>Temporary folders of imports the app did not outlive: gone at the next start (best effort).</summary>
    public static void CleanupTemporaries(string storageRoot)
    {
        try
        {
            if (!Directory.Exists(storageRoot))
                return;
            foreach (var dir in Directory.GetDirectories(storageRoot, ImportTempPrefix + "*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* next time */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing to clean, or not now
        }
    }

    /// <summary>
    /// A name for something new in <paramref name="directory"/>: the wanted one, with a numeric suffix while a
    /// file or folder of that name is already there — "Sets (2)", "foo (2).mp3".
    /// </summary>
    public static string UniqueName(string directory, string desiredName)
    {
        var stem = Sanitize(desiredName);
        var extension = "";
        var dot = stem.LastIndexOf('.');
        if (dot > 0 && dot < stem.Length - 1 && stem.Length - dot <= 6)
        {
            extension = stem[dot..];
            stem = stem[..dot];
        }
        var candidate = stem + extension;
        for (var i = 2; Collides(candidate); i++)
            candidate = $"{stem} ({i}){extension}";
        return candidate;

        bool Collides(string name) => File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name));
    }

    /// <summary>A file/folder name Windows accepts: invalid characters out, no trailing dot or space, never empty.</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
            cleaned = "item";
        return cleaned;
    }

    /// <summary>
    /// The copy of an import: <paramref name="source"/> (a file or a folder, never touched) → a temporary folder
    /// at the top of <paramref name="storageRoot"/> → validated → renamed to <paramref name="destination"/>\<paramref name="name"/>
    /// (a folder inside the storage root; same volume, so the rename is atomic). Returns the final path.
    /// Cancellation or failure before the rename leaves nothing.
    /// </summary>
    public static async Task<string> CopyInAsync(string source, bool isFolder, string storageRoot, string destination, string name, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var temp = Path.Combine(storageRoot, ImportTempPrefix + Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(temp, name);
        var final = Path.Combine(destination, name);
        try
        {
            Directory.CreateDirectory(temp);
            if (isFolder)
            {
                var files = ListFiles(source);
                var totalBytes = files.Sum(f => f.Length);
                long copied = 0;
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, file.FullName);
                    var target = Path.Combine(staged, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await CopyFileAsync(file.FullName, target, ct).ConfigureAwait(false);
                    copied += file.Length;
                    if (totalBytes > 0)
                        progress?.Report(copied * 100.0 / totalBytes);
                }
                // empty folders travel too: the structure is part of what was imported
                foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                {
                    if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    Directory.CreateDirectory(Path.Combine(staged, Path.GetRelativePath(source, dir)));
                }
                Validate(files.Select(f => (Path.Combine(staged, Path.GetRelativePath(source, f.FullName)), f.Length)));
                ct.ThrowIfCancellationRequested();
                Directory.Move(staged, final);
            }
            else
            {
                var info = new FileInfo(source);
                await CopyFileAsync(source, staged, ct).ConfigureAwait(false);
                Validate([(staged, info.Length)]);
                ct.ThrowIfCancellationRequested();
                File.Move(staged, final);
            }
            progress?.Report(100);
            return final;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { /* cleaned at the next start */ }
        }
    }

    private static List<FileInfo> ListFiles(string source)
    {
        var files = new List<FileInfo>();
        Walk(new DirectoryInfo(source));
        return files;

        void Walk(DirectoryInfo dir)
        {
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                if (info is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                        Walk(sub);
                }
                else if (info is FileInfo file)
                {
                    files.Add(file);
                }
            }
        }
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        await using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await from.CopyToAsync(to, ct).ConfigureAwait(false);
            await to.FlushAsync(ct).ConfigureAwait(false);
        }
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source));   // "adicionado em" stays what it was
    }

    private static void Validate(IEnumerable<(string Path, long ExpectedLength)> copies)
    {
        foreach (var (path, expected) in copies)
        {
            var actual = new FileInfo(path);
            if (!actual.Exists || actual.Length != expected)
                throw new IOException($"a cópia de {Path.GetFileName(path)} não ficou inteira ({(actual.Exists ? actual.Length : 0)} de {expected} bytes)");
        }
    }
}
