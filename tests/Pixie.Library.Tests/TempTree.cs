using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>A throw-away folder under %TEMP% with helpers to grow a fake music collection in it.</summary>
public sealed class TempTree : IDisposable
{
    public TempTree(string? name = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "pixie-library-tests", (name ?? "t") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public static string FixtureMp3 => Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.mp3");

    /// <summary>A copy of the 1 s silent MP3 at <paramref name="relative"/> (folders created as needed).</summary>
    public string Mp3(string relative)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(FixtureMp3, path, overwrite: true);
        return path;
    }

    /// <summary>A file of <paramref name="bytes"/> arbitrary bytes at <paramref name="relative"/> — any extension, no real content.</summary>
    public string Junk(string relative, int bytes = 64)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[bytes];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 7 + 3);
        File.WriteAllBytes(path, data);
        return path;
    }

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>A tag reader that remembers what it was asked and answers from a table (or nothing).</summary>
internal sealed class FakeTagReader
{
    public List<string> Calls { get; } = [];
    public Dictionary<string, TagInfo> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action<string>? OnRead { get; set; }

    public TagInfo? Read(string path)
    {
        Calls.Add(path);
        OnRead?.Invoke(path);
        return Answers.TryGetValue(path, out var info) ? info
            : Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? new TagInfo(Path.GetFileNameWithoutExtension(path), "Fake", null, 1, 128, 0)
            : null;
    }

    public int CallsFor(string path) => Calls.Count(c => c.Equals(path, StringComparison.OrdinalIgnoreCase));
}
