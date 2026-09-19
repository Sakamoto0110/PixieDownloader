using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The scanner against a real temporary tree: what it takes, what it reads, what it reuses.</summary>
public sealed class LibraryScannerTests : IDisposable
{
    private readonly TempTree _tree = new("scan");
    private readonly FakeTagReader _tags = new();

    private LibraryRoot Root => new("Músicas", _tree.Root);

    private ScanResult Scan(IndexData? previous = null, bool full = false, IEnumerable<string>? extras = null,
        IEnumerable<string>? priority = null, IReadOnlyList<LibraryRoot>? roots = null, IProgress<ScanProgress>? progress = null)
        => LibraryScanner.Scan(
            new ScanRequest(roots ?? [Root], previous ?? new IndexData(), full, FileKinds.All(extras ?? []), (priority ?? []).ToArray(), _tags.Read),
            progress, CancellationToken.None);

    private static string[] Names(IndexData index) => index.Entries.Select(e => Path.GetFileName(e.Path)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void First_scan_takes_audio_and_video_by_extension_and_reads_each_once()
    {
        _tree.Mp3("a.mp3");
        _tree.Junk("b.MP4");
        _tree.Junk("notes.txt");
        _tree.Junk("sub/deeper/c.flac");
        _tree.Junk("cover.jpg");

        var result = Scan();

        Assert.Equal(["a.mp3", "b.MP4", "c.flac"], Names(result.Index));
        Assert.Equal(3, result.Added);
        Assert.Equal(3, result.TagsRead);
        Assert.Equal(2, result.Unreadable);   // junk mp4/flac: the reader gave nothing, the entries keep their names
        Assert.Equal(3, _tags.Calls.Count);
        var a = result.Index.Entries.Single(e => e.Path.EndsWith("a.mp3"));
        Assert.Equal("a", a.Title);
        Assert.Equal("Fake", a.Artist);
        Assert.Equal(new FileInfo(a.Path).Length, a.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(a.Path), a.Modified);
        Assert.Equal(File.GetCreationTimeUtc(a.Path), a.Created);
        // root, sub and sub/deeper are stamped
        Assert.Equal(3, result.Index.Directories.Count);
        Assert.Contains(result.Index.Directories, d => d.Path.Equals(_tree.Root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nothing_changed_means_no_tag_read_and_the_same_entries()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("sub/b.mp3");
        var first = Scan();
        _tags.Calls.Clear();

        var second = Scan(first.Index);

        Assert.Empty(_tags.Calls);
        Assert.Equal(0, second.Added + second.Changed + second.Removed);
        Assert.Equal(first.Index.Entries, second.Index.Entries);
        Assert.False(LibraryScanner.DirectoriesChanged(second.Index, [Root]));
    }

    [Fact]
    public void A_new_file_reads_only_itself_and_a_deleted_one_goes_away()
    {
        _tree.Mp3("a.mp3");
        var b = _tree.Mp3("sub/b.mp3");
        var first = Scan();
        _tags.Calls.Clear();

        var c = _tree.Mp3("sub/c.mp3");
        File.Delete(b);
        _tree.WaitForFolderChange(first.Index.Directories.Single(d => d.Path.EndsWith("sub")).Modified, "sub");
        Assert.True(LibraryScanner.DirectoriesChanged(first.Index, [Root]));

        var second = Scan(first.Index);

        Assert.Equal([c], _tags.Calls);
        Assert.Equal(["a.mp3", "c.mp3"], Names(second.Index));
        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Removed);
    }

    [Fact]
    public void A_renamed_file_is_a_new_entry_read_again()
    {
        var a = _tree.Mp3("a.mp3");
        var first = Scan();
        _tags.Calls.Clear();

        var renamed = _tree.Full("renamed.mp3");
        File.Move(a, renamed);
        if (!_tree.WaitForFolderChange(first.Index.Directories.Single().Modified))
            return;   // this volume did not stamp the folder for the rename (the GitHub runner's temp drive): the incremental scan cannot see it; the full scan is the catch-up
        var second = Scan(first.Index);

        Assert.Equal([renamed], _tags.Calls);
        Assert.Equal(["renamed.mp3"], Names(second.Index));
    }

    [Fact]
    public void A_file_rewritten_in_place_is_missed_by_the_incremental_scan_and_caught_by_the_full_one()
    {
        // Content written into an existing file does not touch the folder's mtime: that is the trade-off of
        // the incremental scan, and what the button's full scan is for. A download or a tag write by swap
        // (temp + rename) does touch it, so the usual paths are covered.
        var b = _tree.Junk("b.mp4", 64);
        var first = Scan();
        _tags.Calls.Clear();
        File.WriteAllBytes(b, new byte[4096]);

        var incremental = Scan(first.Index);
        Assert.Empty(_tags.Calls);
        Assert.Equal(64, incremental.Index.Entries.Single().Size);

        var full = Scan(incremental.Index, full: true);
        Assert.Equal([b], _tags.Calls);
        Assert.Equal(4096, full.Index.Entries.Single().Size);
        Assert.Equal(1, full.Changed);
    }

    [Fact]
    public void A_full_scan_does_not_reread_files_whose_identity_held()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("sub/b.mp3");
        var first = Scan();
        _tags.Calls.Clear();

        var full = Scan(first.Index, full: true);

        Assert.Empty(_tags.Calls);
        Assert.Equal(2, full.Index.Entries.Count);
        Assert.True(full.Full);
    }

    [Fact]
    public void An_unavailable_root_keeps_its_entries_and_is_reported()
    {
        _tree.Mp3("a.mp3");
        var first = Scan();
        var other = new TempTree("gone");
        var gonePath = other.Root;
        other.Mp3("x.mp3");
        var withBoth = Scan(first.Index, roots: [Root, new LibraryRoot("Externo", gonePath)]);
        Assert.Equal(["a.mp3", "x.mp3"], Names(withBoth.Index));
        other.Dispose();   // the drive was unplugged

        var second = Scan(withBoth.Index, roots: [Root, new LibraryRoot("Externo", gonePath)]);

        Assert.Equal(["a.mp3", "x.mp3"], Names(second.Index));
        Assert.Equal([gonePath], second.UnavailableRoots);
        Assert.Equal(0, second.Removed);
        Assert.Contains(second.Index.Directories, d => d.Path.Equals(gonePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_root_taken_out_drops_its_entries()
    {
        _tree.Mp3("a.mp3");
        using var other = new TempTree("other");
        other.Mp3("x.mp3");
        var first = Scan(roots: [Root, new LibraryRoot("Outra", other.Root)]);
        Assert.Equal(2, first.Index.Entries.Count);

        var second = Scan(first.Index, roots: [Root]);

        Assert.Equal(["a.mp3"], Names(second.Index));
        Assert.Equal(1, second.Removed);
        Assert.DoesNotContain(second.Index.Directories, d => d.Path.Equals(other.Root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extra_extensions_bring_other_files_in()
    {
        _tree.Mp3("a.mp3");
        _tree.Junk("loop.gif");
        _tree.Junk("pic.webp");

        var plain = Scan();
        Assert.Equal(["a.mp3"], Names(plain.Index));

        var withGif = Scan(full: true, extras: ["gif", " .WEBP "]);
        Assert.Equal(["a.mp3", "loop.gif", "pic.webp"], Names(withGif.Index));
        Assert.Equal(FileKind.Other, FileKinds.KindOf(_tree.Full("loop.gif")));
        Assert.Equal(FileKind.Audio, FileKinds.KindOf(_tree.Full("a.mp3")));
        Assert.Equal(FileKind.Video, FileKinds.KindOf(_tree.Full("a.MKV")));
    }

    [Fact]
    public void Inbox_files_are_read_first_and_published_as_ready_before_the_rest()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("b.mp3");
        var fresh = _tree.Mp3("sub/fresh.mp3");
        var reports = new List<ScanProgress>();

        var result = Scan(priority: [fresh], progress: new Collector(reports));

        Assert.Equal(fresh, _tags.Calls[0]);
        var ready = Assert.Single(reports, r => r.Ready is not null);
        Assert.Equal([fresh], ready.Ready!.Select(e => e.Path));
        Assert.Equal(3, result.Index.Entries.Count);
    }

    [Fact]
    public void Nested_roots_do_not_duplicate_entries()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("sub/b.mp3");

        var result = Scan(roots: [Root, new LibraryRoot("Sub", _tree.Full("sub"))]);

        Assert.Equal(["a.mp3", "b.mp3"], Names(result.Index));
        Assert.Equal(2, _tags.Calls.Count);
    }

    [Fact]
    public void A_new_root_counts_as_changed_only_when_its_folder_exists()
    {
        _tree.Mp3("a.mp3");
        var first = Scan();

        Assert.False(LibraryScanner.DirectoriesChanged(first.Index, [Root, new LibraryRoot("Nova", _tree.Full("nova"))]));
        Directory.CreateDirectory(_tree.Full("nova"));
        Assert.True(LibraryScanner.DirectoriesChanged(first.Index, [Root, new LibraryRoot("Nova", _tree.Full("nova"))]));
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("b.mp3");
        using var cts = new CancellationTokenSource();
        _tags.OnRead = _ => cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LibraryScanner.Scan(
            new ScanRequest([Root], new IndexData(), false, FileKinds.All([]), [], _tags.Read), null, cts.Token));
        Assert.Single(_tags.Calls);
    }

    public void Dispose() => _tree.Dispose();

    private sealed class Collector(List<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => reports.Add(value);
    }
}
