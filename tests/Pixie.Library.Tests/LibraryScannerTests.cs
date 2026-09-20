using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The scanner against a real temporary tree: what it takes, what it reads, what it reuses, what it keeps when it cannot look — as a tree.</summary>
public sealed class LibraryScannerTests : IDisposable
{
    private readonly TempTree _tree = new("scan");
    private readonly FakeTagReader _tags = new();
    private readonly FakeIdentity _identity = new();

    /// <summary>The plan: the test tree as the storage root, plus these sources.</summary>
    private ScanPlan PlanFor(IEnumerable<string>? extras = null, params ItemData[] items)
    {
        var data = Items.Data(items);
        data.ExtraExtensions = (extras ?? []).ToList();
        return ScanPlan.Build(Items.Snapshot(data), _tree.Root);
    }

    private ScanResult Scan(IndexData? previous = null, bool full = false, IEnumerable<string>? extras = null,
        IEnumerable<string>? priority = null, ScanPlan? plan = null, IProgress<ScanProgress>? progress = null, Func<string, FileIdentityInfo>? identity = null)
        => LibraryScanner.Scan(
            new ScanRequest(plan ?? PlanFor(extras), previous ?? new IndexData(), full, (priority ?? []).ToArray(), _tags.Read, identity ?? _identity.Get),
            progress, CancellationToken.None);

    private static string[] Names(IndexData index) => index.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

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
        Assert.Equal(2, result.Unreadable);   // junk mp4/flac: the reader gave nothing, the nodes keep their names
        Assert.Equal(3, _tags.Calls.Count);
        Assert.Equal(3, _identity.Calls.Count);   // one handle per listed file
        var a = result.Index.FindFile(_tree.Full("a.mp3"))!;
        Assert.Equal("a", a.Title);
        Assert.Equal("Fake", a.Artist);
        Assert.Equal(new FileInfo(a.FullPath).Length, a.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(a.FullPath), a.Modified);
        Assert.Equal(File.GetCreationTimeUtc(a.FullPath), a.Created);
        Assert.Equal(_identity.Ids[a.FullPath], a.FileId);
        Assert.Equal("FAKEVOL", a.VolumeSerial);

        // The tree: one source, its root named after the folder, sub and sub/deeper below it, each stamped
        var source = Assert.Single(result.Index.Sources);
        Assert.Equal(_tree.Root, source.RootPath);
        Assert.Equal("FAKEVOL", source.VolumeSerial);
        Assert.Equal(Path.GetFileName(_tree.Root), source.Root!.Name);
        Assert.Equal(Directory.GetLastWriteTimeUtc(_tree.Root), source.Root.Modified);
        var c = result.Index.FindFile(_tree.Full("sub/deeper/c.flac"))!;
        Assert.Equal("deeper", c.Parent!.Name);
        Assert.Equal("sub", c.Parent.Parent!.Name);
        Assert.Same(source.Root, c.Parent.Parent.Parent);
        Assert.Equal(_tree.Full("sub/deeper/c.flac"), c.FullPath);
        Assert.Equal(3, source.Folders.Count());
        Assert.NotNull(result.Index.ScannedAt);
        Assert.Equal(LibraryScanner.RulesVersion, result.Index.RulesVersion);
        Assert.Contains(".mp3", result.Index.Extensions);
    }

    [Fact]
    public void Nothing_changed_means_no_tag_read_and_the_same_tree()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("sub/b.mp3");
        var first = Scan();
        _tags.Calls.Clear();
        _identity.Calls.Clear();

        var second = Scan(first.Index);

        Assert.Empty(_tags.Calls);
        Assert.Empty(_identity.Calls);   // nothing was listed, so nothing was opened
        Assert.Equal(0, second.Added + second.Changed + second.Removed);
        Assert.Equal(first.Index.Entries.Select(e => (e.FullPath, e.Size, e.Modified, e.FileId, e.Title)), second.Index.Entries.Select(e => (e.FullPath, e.Size, e.Modified, e.FileId, e.Title)));
        Assert.NotSame(first.Index.FindFile(_tree.Full("a.mp3")), second.Index.FindFile(_tree.Full("a.mp3")));   // a new tree, not the old nodes re-parented
        Assert.False(LibraryScanner.DirectoriesChanged(second.Index, PlanFor(null)));
    }

    [Fact]
    public void A_new_file_reads_only_itself_and_a_deleted_one_goes_away()
    {
        _tree.Mp3("a.mp3");
        var b = _tree.Mp3("sub/b.mp3");
        var first = Scan();
        _tags.Calls.Clear();
        var stamp = first.Index.FindFolder(_tree.Full("sub"))!.Modified;

        var c = _tree.Mp3("sub/c.mp3");
        File.Delete(b);
        Assert.True(_tree.WaitForFolderChange(stamp, "sub"));
        Assert.True(LibraryScanner.DirectoriesChanged(first.Index, PlanFor(null)));

        var second = Scan(first.Index);

        Assert.Equal([c], _tags.Calls);
        Assert.Equal(["a.mp3", "c.mp3"], Names(second.Index));
        Assert.Equal(1, second.Added);
        Assert.Equal(1, second.Removed);
    }

    [Fact]
    public void A_renamed_file_is_a_new_node_read_again()
    {
        var a = _tree.Mp3("a.mp3");
        var first = Scan();
        _tags.Calls.Clear();

        var renamed = _tree.Full("renamed.mp3");
        File.Move(a, renamed);
        if (!_tree.WaitForFolderChange(first.Index.Sources[0].Root!.Modified))
            return;   // this volume does not stamp the folder for a rename: the incremental scan cannot see it (the full one does)
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
        Assert.Equal("a", full.Index.FindFile(_tree.Full("a.mp3"))!.Title);   // the tags came along
        Assert.True(full.Full);
    }

    [Fact]
    public void A_file_swapped_for_another_with_the_same_size_and_mtime_is_reread_because_its_id_changed()
    {
        // Same path, same size, same mtime — a copy restored from a backup, say — but another NTFS object:
        // the file id is part of the identity when both sides know it.
        var a = _tree.Mp3("a.mp3");
        var first = Scan();
        _tags.Calls.Clear();
        _identity.Ids[a] = "another-object";

        var full = Scan(first.Index, full: true);

        Assert.Equal([a], _tags.Calls);
        Assert.Equal(1, full.Changed);
        Assert.Equal("another-object", full.Index.Entries.Single().FileId);
    }

    [Fact]
    public void Without_file_ids_the_identity_is_size_and_mtime_alone()
    {
        // A volume that gives no id (FAT, a share): the scanner does not read tags again for that.
        _tree.Mp3("a.mp3");
        var first = Scan(identity: _ => FileIdentityInfo.None);
        Assert.Null(first.Index.Entries.Single().FileId);
        Assert.Null(first.Index.Sources[0].VolumeSerial);
        _tags.Calls.Clear();

        var full = Scan(first.Index, full: true, identity: _ => FileIdentityInfo.None);

        Assert.Empty(_tags.Calls);
    }

    [Fact]
    public void An_unavailable_source_keeps_its_tree_and_is_reported()
    {
        _tree.Mp3("a.mp3");
        var first = Scan();
        var other = new TempTree("gone");
        var gonePath = other.Root;
        other.Mp3("x.mp3");
        other.Mp3("deep/y.mp3");
        var plan = PlanFor(null, Items.SourceFolder(gonePath, "Externo"));
        var withBoth = Scan(first.Index, plan: plan);
        Assert.Equal(["a.mp3", "x.mp3", "y.mp3"], Names(withBoth.Index));
        other.Dispose();   // the drive was unplugged

        var second = Scan(withBoth.Index, plan: plan);

        Assert.Equal(["a.mp3", "x.mp3", "y.mp3"], Names(second.Index));
        Assert.Equal([gonePath], second.UnavailableSources);
        Assert.Equal(0, second.Removed);
        var gone = LibraryScanner.FindSource(second.Index, gonePath)!;
        Assert.True(gone.IsUnavailable);
        Assert.Equal("FAKEVOL", gone.VolumeSerial);
        Assert.Equal(Path.Combine(gonePath, "deep", "y.mp3"), second.Index.FindFile(Path.Combine(gonePath, "deep", "y.mp3"))!.FullPath);
        Assert.False(LibraryScanner.DirectoriesChanged(second.Index, plan));   // nothing to observe there: not a change

        // Back, untouched: the same mtimes, nothing reread; the source is ok again.
        Directory.CreateDirectory(gonePath);
        Assert.True(LibraryScanner.DirectoriesChanged(second.Index, plan));
        var third = Scan(second.Index, plan: plan);
        Assert.False(LibraryScanner.FindSource(third.Index, gonePath)!.IsUnavailable);
        Directory.Delete(gonePath, recursive: true);
    }

    [Fact]
    public void A_source_taken_out_of_the_plan_drops_its_tree()
    {
        _tree.Mp3("a.mp3");
        using var other = new TempTree("other");
        other.Mp3("x.mp3");
        var first = Scan(plan: PlanFor(null, Items.SourceFolder(other.Root, "Outra")));
        Assert.Equal(2, first.Index.Entries.Count);
        Assert.Equal(2, first.Index.Sources.Count);

        var second = Scan(first.Index);

        Assert.Equal(["a.mp3"], Names(second.Index));
        Assert.Equal(1, second.Removed);
        Assert.Single(second.Index.Sources);
        Assert.Null(LibraryScanner.FindSource(second.Index, other.Root));
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_keeps_what_the_tree_knew_and_is_reported()
    {
        // B5: inaccessible is not empty. The folder's subtree survives a full scan, marked, so the tab can say so.
        _tree.Mp3("a.mp3");
        var b = _tree.Mp3("locked/b.mp3");
        _tree.Mp3("locked/deeper/c.mp3");
        var first = Scan();
        Assert.Equal(3, first.Index.Entries.Count);
        _tags.Calls.Clear();
        _tree.DenyListing("locked");

        var full = Scan(first.Index, full: true);

        Assert.Equal(["a.mp3", "b.mp3", "c.mp3"], Names(full.Index));
        Assert.Equal(0, full.Removed);
        Assert.Empty(_tags.Calls);
        Assert.Equal([_tree.Full("locked")], full.InaccessibleDirectories);
        Assert.Equal([_tree.Full("locked")], full.Index.InaccessibleDirectories);
        var locked = full.Index.FindFolder(_tree.Full("locked"))!;
        Assert.True(locked.IsInaccessible);
        Assert.False(locked.Folders.Single().IsInaccessible);   // only the folder that refused is marked
        Assert.NotNull(full.Index.FindFile(b));
        Assert.False(LibraryScanner.DirectoriesChanged(full.Index, PlanFor(null)));   // a folder that cannot be read is not asked again every few seconds

        // Listable again: the next full scan sees it as it is, and the report is clean.
        _tree.AllowListing("locked");
        File.Delete(b);
        var again = Scan(full.Index, full: true);
        Assert.Equal(["a.mp3", "c.mp3"], Names(again.Index));
        Assert.Empty(again.InaccessibleDirectories);
        Assert.False(again.Index.FindFolder(_tree.Full("locked"))!.IsInaccessible);
    }

    [Fact]
    public void An_explicit_file_outside_every_folder_is_stat_ed_not_walked()
    {
        _tree.Mp3("a.mp3");
        using var other = new TempTree("other");
        var x = other.Mp3("x.mp3");
        other.Mp3("neighbour.mp3");   // next to it, but not a source: never listed
        var plan = PlanFor(null, Items.SourceFile(x, id: "file-x"));
        Assert.Single(plan.FileSources);

        var first = Scan(plan: plan);

        Assert.Equal(["a.mp3", "x.mp3"], Names(first.Index));
        var source = LibraryScanner.FindSource(first.Index, x)!;
        Assert.False(source.IsFolder);
        Assert.Equal(x, source.File!.FullPath);
        Assert.Null(source.File.Parent);
        Assert.Equal("FAKEVOL", source.VolumeSerial);
        Assert.Null(first.Index.FindFolder(other.Root));
        Assert.False(LibraryScanner.DirectoriesChanged(first.Index, plan));

        // Gone with its folder still there: a valid observation, the node goes. Touched: it counts as changed.
        File.Delete(x);
        var second = Scan(first.Index, plan: plan);
        Assert.Equal(["a.mp3"], Names(second.Index));
        Assert.Equal(1, second.Removed);
        Assert.Empty(second.UnavailableSources);
        Assert.Single(second.Index.Sources);
    }

    [Fact]
    public void An_explicit_file_whose_folder_is_gone_is_kept_as_unavailable()
    {
        var other = new TempTree("other");
        var x = other.Mp3("x.mp3");
        var plan = PlanFor(null, Items.SourceFile(x));
        var first = Scan(plan: plan);
        Assert.Single(first.Index.Entries);
        other.Dispose();

        var second = Scan(first.Index, plan: plan);

        Assert.Equal(["x.mp3"], Names(second.Index));
        Assert.Equal([x], second.UnavailableSources);
        Assert.Equal(0, second.Removed);
        Assert.True(LibraryScanner.FindSource(second.Index, x)!.IsUnavailable);
    }

    [Fact]
    public void Extra_extensions_bring_other_files_in_and_a_tree_made_without_them_needs_a_full_scan()
    {
        _tree.Mp3("a.mp3");
        _tree.Junk("loop.gif");
        _tree.Junk("pic.webp");

        var plain = Scan();
        Assert.Equal(["a.mp3"], Names(plain.Index));
        Assert.False(LibraryScanner.RulesChanged(plain.Index, PlanFor(null)));
        Assert.True(LibraryScanner.RulesChanged(plain.Index, PlanFor(["gif"])));
        Assert.False(LibraryScanner.RulesChanged(new IndexData(), PlanFor(["gif"])));   // never scanned: nothing to compare

        var withGif = Scan(full: true, extras: ["gif", " .WEBP "]);
        Assert.Equal(["a.mp3", "loop.gif", "pic.webp"], Names(withGif.Index));
        Assert.Contains(".gif", withGif.Index.Extensions);
        Assert.False(LibraryScanner.RulesChanged(withGif.Index, PlanFor(["GIF", "webp"])));
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
        Assert.Equal([fresh], ready.Ready!.Select(e => e.FullPath));   // attached: the node knows where it is
        Assert.Equal("fresh", ready.Ready![0].Title);
        Assert.Equal(3, result.Index.Entries.Count);
    }

    [Fact]
    public void A_folder_inside_another_is_walked_once_by_the_outer_one()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("sub/b.mp3");
        var plan = PlanFor(null, Items.SourceFolder(_tree.Full("sub"), "Sub", "sub-id"));
        Assert.Single(plan.FolderSources);   // the covering set

        var result = Scan(plan: plan);

        Assert.Equal(["a.mp3", "b.mp3"], Names(result.Index));
        Assert.Equal(2, _tags.Calls.Count);
        Assert.Single(result.Index.Sources);
        Assert.Same(result.Index.Sources[0], result.Index.SourceOf(_tree.Full("sub/b.mp3")));
    }

    [Fact]
    public void A_new_source_counts_as_changed_only_when_its_folder_exists()
    {
        _tree.Mp3("a.mp3");
        var first = Scan();
        var plan = PlanFor(null, Items.SourceFolder(_tree.Full("nova"), "Nova"));

        Assert.False(LibraryScanner.DirectoriesChanged(first.Index, plan));
        Directory.CreateDirectory(_tree.Full("nova"));
        Assert.True(LibraryScanner.DirectoriesChanged(first.Index, plan));
    }

    [Fact]
    public void An_empty_folder_is_a_node_of_the_tree()
    {
        // "Criar pasta" has to show up before anything is in it.
        _tree.Mp3("a.mp3");
        _tree.Dir("Nova");

        var result = Scan();

        var nova = result.Index.FindFolder(_tree.Full("Nova"))!;
        Assert.Empty(nova.Files);
        Assert.Same(result.Index.Sources[0].Root, nova.Parent);
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        _tree.Mp3("a.mp3");
        _tree.Mp3("b.mp3");
        using var cts = new CancellationTokenSource();
        _tags.OnRead = _ => cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LibraryScanner.Scan(
            new ScanRequest(PlanFor(null), new IndexData(), false, [], _tags.Read, _identity.Get), null, cts.Token));
        Assert.Single(_tags.Calls);
    }

    public void Dispose() => _tree.Dispose();

    private sealed class Collector(List<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => reports.Add(value);
    }
}
