using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The Win32 side of the locator (docs/library-model.md §9) on the real NTFS volume %TEMP% lives on: ids that survive a rename, and the lookup that finds the file again.</summary>
public sealed class FileIdentityTests : IDisposable
{
    private readonly TempTree _tree = new("identity");

    [Fact]
    public void A_file_id_is_stable_across_a_rename_and_a_move_and_differs_between_files()
    {
        var a = _tree.Mp3("a.mp3");
        var b = _tree.Mp3("b.mp3");
        Assert.True(FileIdentity.TryGet(a, isFolder: false, out var serial, out var idA));
        Assert.True(FileIdentity.TryGet(b, isFolder: false, out var serialB, out var idB));
        Assert.Equal(16, serial!.Length);
        Assert.Equal(32, idA!.Length);
        Assert.Equal(serial, serialB);
        Assert.NotEqual(idA, idB);

        var moved = _tree.Full("sub/renamed.mp3");
        Directory.CreateDirectory(_tree.Full("sub"));
        File.Move(a, moved);
        Assert.True(FileIdentity.TryGet(moved, isFolder: false, out _, out var idMoved));

        Assert.Equal(idA, idMoved);
        Assert.True(FileIdentity.TryGet(_tree.Full("sub"), isFolder: true, out _, out var idDir));
        Assert.NotNull(idDir);
        Assert.False(FileIdentity.TryGet(_tree.Full("nope.mp3"), isFolder: false, out _, out _));
    }

    [Fact]
    public void A_file_in_use_still_answers()
    {
        var a = _tree.Mp3("a.mp3");
        using var open = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.True(FileIdentity.TryGet(a, isFolder: false, out _, out var id));
        Assert.NotNull(id);
    }

    [Fact]
    public void FindById_locates_the_file_wherever_it_went_on_the_volume()
    {
        var a = _tree.Mp3("a.mp3");
        FileIdentity.TryGet(a, isFolder: false, out var serial, out var id);
        var root = PathUtil.VolumeRootOf(a)!;
        var moved = _tree.Full("elsewhere/deep/moved.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(a, moved);

        var found = FileIdentity.FindById(root, id!);

        Assert.NotNull(found);
        Assert.True(PathUtil.IsSamePath(moved, found!), found);
        File.Delete(moved);
        Assert.Null(FileIdentity.FindById(root, id!));
        Assert.Null(FileIdentity.FindById(root, "not-hex"));
    }

    [Fact]
    public void The_probe_reports_identity_and_relocates_through_it()
    {
        var a = _tree.Mp3("a.mp3");
        var probe = new WindowsFileProbe();

        var stat = probe.Stat(a);
        Assert.Equal((ProbeOutcome.Found, false, new FileInfo(a).Length), (stat.Outcome, stat.IsFolder, stat.Size));
        Assert.NotNull(stat.FileId);
        Assert.NotNull(stat.VolumeSerial);
        Assert.Equal(File.GetLastWriteTimeUtc(a), stat.Modified);
        var dir = probe.Stat(_tree.Root);
        Assert.True(dir.IsFolder);
        Assert.Equal(stat.VolumeSerial, dir.VolumeSerial);
        Assert.Equal(ProbeOutcome.NotFound, probe.Stat(_tree.Full("nope")).Outcome);
        Assert.True(probe.PathRootExists(a));
        Assert.False(probe.PathRootExists(@"Q:\nowhere\x.mp3"));
        Assert.True(probe.IsVolumeReady(stat.VolumeSerial!, a));
        Assert.False(probe.IsVolumeReady("0000000000000000", a));

        var moved = _tree.Full("moved.mp3");
        File.Move(a, moved);

        var found = probe.FindByFileId(stat.VolumeSerial!, stat.FileId!, a);
        Assert.NotNull(found);
        Assert.True(PathUtil.IsSamePath(moved, found!), found);
    }

    [Fact]
    public void The_scanner_and_the_reconciler_agree_on_the_id_so_a_moved_link_is_relocated_for_real()
    {
        // End to end on disk: a file is added as a source, the scanner records its id, the file moves outside
        // every source, the reconciler asks the volume by id and asks for the locator to be rewritten.
        using var data = new TempTree("data");
        using var music = new TempTree("music");
        var store = new LibraryStore(data.Root);
        var manifest = LibraryManifest.Load(data.Root, music.Root);
        var ops = new LibraryOperations(manifest, store, new WindowsFileProbe(), (_, _) => { });
        var original = _tree.Mp3("a.mp3");
        var added = ops.AddFile(original);
        var plan = ScanPlan.Build(manifest.Snapshot(), music.Root);
        var scan = LibraryScanner.Scan(new ScanRequest(plan, new IndexData(), false, [], _ => null, LibrarySync.WindowsIdentity), null, CancellationToken.None);
        var entry = Assert.Single(scan.Index.Entries);
        Assert.Equal(manifest.Snapshot().FindItem(added.Id)!.Locator.FileId, entry.FileId);

        var moved = _tree.Full("Sets/moved.mp3");
        Directory.CreateDirectory(_tree.Full("Sets"));
        File.Move(original, moved);
        var result = Reconciler.Build(new ReconcileInput(manifest.Snapshot(), scan.Index, plan), new WindowsFileProbe());

        var item = result.Model.FindItem(added.Id)!;
        Assert.Equal(Resolution.Relocated, item.Resolution);
        Assert.True(PathUtil.IsSamePath(moved, item.ResolvedPath!), item.ResolvedPath);
        var rewrite = Assert.Single(result.Actions.OfType<RewriteLocator>());
        Assert.True(PathUtil.IsSamePath(moved, rewrite.Locator.LastKnownPath));
        Assert.Equal(entry.FileId, rewrite.Locator.FileId);
    }

    public void Dispose() => _tree.Dispose();
}
