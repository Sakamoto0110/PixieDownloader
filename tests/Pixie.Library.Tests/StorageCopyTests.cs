using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The copy an import makes into the storage root, and the names that keep it from landing on something already there.</summary>
public sealed class StorageCopyTests : IDisposable
{
    private readonly TempTree _tree = new("storage");

    // ───── Names ─────

    [Fact]
    public void A_name_gets_a_suffix_while_it_collides_with_what_is_in_the_folder()
    {
        _tree.Mp3("mix.mp3");
        _tree.Mp3("mix (2).mp3");
        _tree.Dir("Album");

        Assert.Equal("mix (3).mp3", StorageCopy.UniqueName(_tree.Root, "mix.mp3"));
        Assert.Equal("Album (2)", StorageCopy.UniqueName(_tree.Root, "Album"));
        Assert.Equal("fresh.mp3", StorageCopy.UniqueName(_tree.Root, "fresh.mp3"));
        Assert.Equal("mix.mp3", StorageCopy.UniqueName(_tree.Dir("elsewhere"), "mix.mp3"));   // another folder: no collision
        Assert.Equal("a_b_.mp3", StorageCopy.UniqueName(_tree.Root, "a|b?.mp3. "));
    }

    [Theory]
    [InlineData("  name  ", "name")]
    [InlineData("trailing.", "trailing")]
    [InlineData("a:b*c", "a_b_c")]
    [InlineData("...", "item")]
    [InlineData("", "item")]
    [InlineData("..", "item")]
    public void Sanitize_gives_a_name_windows_accepts(string input, string expected)
    {
        Assert.Equal(expected, StorageCopy.Sanitize(input));
    }

    // ───── The copy ─────

    [Fact]
    public async Task A_file_is_copied_through_a_temporary_folder_at_the_top_of_the_root_and_the_source_is_untouched()
    {
        using var source = new TempTree("source");
        var file = source.Mp3("mix.mp3");
        var modified = File.GetLastWriteTimeUtc(file);
        var created = File.GetCreationTimeUtc(file);
        var root = _tree.Full("root");
        var destination = Path.Combine(root, "Albums");
        var reports = new List<double>();

        var final = await StorageCopy.CopyInAsync(file, isFolder: false, root, destination, "mix.mp3", new Progress(reports), CancellationToken.None);

        Assert.Equal(Path.Combine(destination, "mix.mp3"), final);
        Assert.True(File.Exists(final));
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(final));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(final));   // the times travel: the mtime is part of the identity, the creation time is "adicionado em"
        Assert.Equal(created, File.GetCreationTimeUtc(final));
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetDirectories(root, StorageCopy.ImportTempPrefix + "*"));
        Assert.Empty(Directory.GetDirectories(destination, StorageCopy.ImportTempPrefix + "*"));
        Assert.Equal(100, reports.Last());
    }

    [Fact]
    public async Task A_folder_is_copied_whole_including_empty_subfolders()
    {
        using var source = new TempTree("source");
        source.Mp3("Album/1.mp3");
        source.Mp3("Album/disc2/2.mp3");
        source.Junk("Album/cover.jpg");
        source.Dir("Album/empty");
        var root = _tree.Full("root");

        var final = await StorageCopy.CopyInAsync(source.Full("Album"), isFolder: true, root, root, "Album", null, CancellationToken.None);

        Assert.Equal(Path.Combine(root, "Album"), final);
        Assert.True(File.Exists(Path.Combine(root, "Album", "1.mp3")));
        Assert.True(File.Exists(Path.Combine(root, "Album", "disc2", "2.mp3")));
        Assert.True(File.Exists(Path.Combine(root, "Album", "cover.jpg")));
        Assert.True(Directory.Exists(Path.Combine(root, "Album", "empty")));
        Assert.True(Directory.Exists(source.Full("Album")));
        Assert.Empty(Directory.GetDirectories(root, StorageCopy.ImportTempPrefix + "*"));
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_nothing_behind()
    {
        using var source = new TempTree("source");
        source.Mp3("Album/1.mp3");
        source.Mp3("Album/2.mp3");
        var root = _tree.Full("root");
        using var cts = new CancellationTokenSource();
        var progress = new Progress(_ => cts.Cancel());   // after the first file

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StorageCopy.CopyInAsync(source.Full("Album"), isFolder: true, root, root, "Album", progress, cts.Token));

        Assert.False(Directory.Exists(Path.Combine(root, "Album")));
        Assert.Empty(Directory.GetDirectories(root, StorageCopy.ImportTempPrefix + "*"));
        Assert.True(File.Exists(source.Full("Album/1.mp3")));
    }

    [Fact]
    public void Leftover_temporaries_are_removed_and_a_missing_root_is_fine()
    {
        var root = _tree.Full("root");
        Directory.CreateDirectory(Path.Combine(root, ".~import-dead", "x"));
        File.WriteAllText(Path.Combine(root, ".~import-dead", "x", "partial.mp3"), "…");
        Directory.CreateDirectory(Path.Combine(root, "keep"));

        StorageCopy.CleanupTemporaries(root);
        StorageCopy.CleanupTemporaries(_tree.Full("nope"));

        Assert.Equal([Path.Combine(root, "keep")], Directory.GetDirectories(root));
    }

    public void Dispose() => _tree.Dispose();

    private sealed class Progress(Action<double> onReport) : IProgress<double>
    {
        public Progress(List<double> sink) : this(sink.Add) { }
        public void Report(double value) => onReport(value);
    }
}
