using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>
/// The operations against a real data folder, a real Music folder (a temp tree) and a real probe: what each
/// one writes to the manifest, what it does on disk, what it refuses, and what it never touches.
/// </summary>
public sealed class LibraryOperationsTests : IDisposable
{
    private readonly TempTree _data = new("data");
    private readonly TempTree _music = new("music");
    private readonly TempTree _ext = new("ext");
    private readonly List<string> _log = [];
    private readonly LibraryStore _store;
    private readonly LibraryManifest _manifest;
    private readonly LibraryOperations _ops;

    public LibraryOperationsTests()
    {
        _store = new LibraryStore(_data.Root);
        _manifest = LibraryManifest.Load(_data.Root, _music.Root);
        _ops = new LibraryOperations(_manifest, _store, new WindowsFileProbe(), (level, message) => _log.Add($"{level}: {message}"));
    }

    private ManifestSnapshot Snapshot => _manifest.Snapshot();
    private ItemData Item(string? id) => Snapshot.FindItem(id)!;
    private IReadOnlyList<string> InboxFiles => _store.Inbox.ReadAll().SelectMany(b => b.Files).ToList();
    private void Synced() => _manifest.TryMarkSynced(_manifest.Generation, DateTime.UtcNow);

    // ───── Adicionar ─────

    [Fact]
    public void AddFolder_makes_a_source_where_the_folder_is_and_marks_pending()
    {
        Synced();
        var sets = _ext.Dir("Sets");
        var generation = _manifest.Generation;

        var result = _ops.AddFolder(sets + @"\");

        Assert.True(result.Ok);
        var item = Item(result.Id);
        Assert.Equal((ItemKinds.Folder, "Sets", sets), (item.Kind, item.Source!.Name, item.Locator.LastKnownPath));
        Assert.NotNull(item.Locator.FileId);   // the real probe answered
        Assert.NotNull(item.Locator.VolumeSerial);
        Assert.Null(item.Locator.Size);
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
        Assert.Equal(generation + 1, _manifest.Generation);
        Assert.Contains(_log, l => l.Contains("entrou no catálogo"));
        Assert.Equal([sets], ScanPlan.Build(Snapshot, _music.Root).FolderSources.Skip(1).Select(s => s.Path));

        Assert.Contains("já está no catálogo", _ops.AddFolder(sets.ToUpperInvariant()).Message);
        Assert.Contains("não uma pasta", _ops.AddFolder(_ext.Mp3("a.mp3")).Message);
        Assert.Contains("inválido", _ops.AddFolder(@"Sets\relative").Message);
        var second = _ops.AddFolder(_ext.Dir("Other/Sets"));   // the same name elsewhere: the chip stays telling them apart
        Assert.Equal("Sets (2)", Item(second.Id).Source!.Name);
        var nested = _ops.AddFolder(_music.Dir("Inside"));   // inside the storage root: allowed, as the 1.1 allowed nested roots
        Assert.True(nested.Ok);
    }

    [Fact]
    public void AddFile_makes_a_source_inboxes_it_and_a_second_time_is_the_same_item()
    {
        var a = _ext.Mp3("a.mp3");

        var first = _ops.AddFile(a);
        var again = _ops.AddFile(a.ToUpperInvariant());

        Assert.True(first.Ok && again.Ok);
        Assert.Equal(first.Id, again.Id);
        Assert.Contains("já estava", again.Message);
        var item = Item(first.Id);
        Assert.Equal((ItemKinds.File, "a.mp3", new FileInfo(a).Length), (item.Kind, item.Source!.Name, item.Locator.Size));
        Assert.Equal([a], InboxFiles);
        Assert.Equal([a], ScanPlan.Build(Snapshot, _music.Root).FileSources.Select(s => s.Path));
        Assert.Contains("não um arquivo", _ops.AddFile(_ext.Dir("d")).Message);

        // A file the storage root already covers is in the library already: nothing is created
        var covered = _ops.AddFile(_music.Mp3("b.mp3"));
        Assert.True(covered.Ok);
        Assert.Null(covered.Id);
        Assert.Contains("já está na biblioteca", covered.Message);
        Assert.Single(Snapshot.Items);

        // A member that becomes a source keeps its identity
        var group = _ops.CreateGroup("G");
        var member = _ops.AddToGroup(group.Id!, [new Target(_ext.Mp3("m.mp3"), false)]);
        Assert.True(member.Ok);
        var memberItem = Snapshot.FindItemByPath(_ext.Full("m.mp3"))!;
        Assert.False(memberItem.IsSource);
        var promoted = _ops.AddFile(_ext.Full("m.mp3"));
        Assert.Equal(memberItem.Id, promoted.Id);
        Assert.True(Item(promoted.Id).IsSource);
        Assert.Equal(["m.mp3"], Snapshot.Items.Where(i => i.Id == memberItem.Id).Select(i => i.Source!.Name));
    }

    [Fact]
    public void RenameSource_changes_the_name_only_and_RemoveSource_keeps_an_item_a_group_still_names()
    {
        var sets = _ops.AddFolder(_ext.Dir("Sets"));
        var loose = _ops.AddFile(_ext.Mp3("loose.mp3"));
        var group = _ops.CreateGroup("G");
        Assert.True(_ops.AddToGroup(group.Id!, [new Target(_ext.Full("loose.mp3"), false)]).Ok);
        Synced();

        Assert.True(_ops.RenameSource(sets.Id!, "  Mixes ").Ok);
        Assert.Equal(("Mixes", _ext.Full("Sets")), (Item(sets.Id).Source!.Name, Item(sets.Id).Locator.LastKnownPath));
        Assert.Equal(SyncStatus.Synced, _manifest.Status);   // nothing the scanner sees changed
        Assert.False(_ops.RenameSource(sets.Id!, " ").Ok);
        Assert.False(_ops.RenameSource("nope", "X").Ok);

        Assert.True(_ops.RemoveSource(sets.Id!).Ok);
        Assert.Null(Snapshot.FindItem(sets.Id));   // nobody named it: gone
        Assert.True(Directory.Exists(_ext.Full("Sets")));
        Assert.True(_ops.RemoveSource(loose.Id!).Ok);
        var kept = Item(loose.Id);
        Assert.False(kept.IsSource);   // the group still names it: the item stays, unobserved on its own
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
        Assert.Empty(ScanPlan.Build(Snapshot, _music.Root).FileSources);
    }

    [Fact]
    public void Relocate_rewrites_the_locator_and_only_that()
    {
        var a = _ext.Mp3("a.mp3");
        var added = _ops.AddFile(a);
        var moved = _ext.Full("moved.mp3");
        File.Move(a, moved);

        Assert.Contains("não há nada", _ops.Relocate(added.Id!, _ext.Full("nope.mp3")).Message);
        Assert.Contains("não um arquivo", _ops.Relocate(added.Id!, _ext.Dir("dir")).Message);
        Assert.True(_ops.Relocate(added.Id!, moved).Ok);

        var item = Item(added.Id);
        Assert.Equal((moved, "a.mp3"), (item.Locator.LastKnownPath, item.Source!.Name));
        Assert.NotNull(item.Locator.FileId);
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
    }

    // ───── Importar ─────

    [Fact]
    public async Task ImportFiles_copies_into_the_storage_root_or_a_folder_inside_it_and_never_overwrites()
    {
        var a = _ext.Mp3("a.mp3");
        var b = _ext.Mp3("b.mp3");
        _music.Mp3("a.mp3");   // already there: the copy gets a suffix
        var albums = _music.Dir("Albums");
        var reports = new List<double>();

        var root = await _ops.ImportFilesAsync([a], null, new SyncProgress(reports.Add), CancellationToken.None);
        var sub = await _ops.ImportFilesAsync([b], albums, null, CancellationToken.None);

        Assert.True(root.Ok && sub.Ok);
        Assert.Null(root.Message);
        Assert.True(File.Exists(_music.Full("a (2).mp3")));
        Assert.True(File.Exists(_music.Full("Albums/b.mp3")));
        Assert.True(File.Exists(a) && File.Exists(b));   // the sources are untouched
        Assert.Equal(File.GetLastWriteTimeUtc(a), File.GetLastWriteTimeUtc(_music.Full("a (2).mp3")));
        Assert.Equal([_music.Full("a (2).mp3"), _music.Full("Albums/b.mp3")], InboxFiles);
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
        Assert.Empty(Snapshot.Items);   // no source, no item: the tree shows it
        Assert.Contains(100, reports);
        Assert.Empty(Directory.GetDirectories(_music.Root, StorageCopy.ImportTempPrefix + "*"));

        Assert.Contains("dentro da pasta da biblioteca", (await _ops.ImportFilesAsync([_music.Full("a.mp3")], null, null, CancellationToken.None)).Message);
        Assert.Contains("destino", (await _ops.ImportFilesAsync([a], _ext.Root, null, CancellationToken.None)).Message);
        Assert.Contains("não existe", (await _ops.ImportFilesAsync([_ext.Full("nope.mp3")], null, null, CancellationToken.None)).Message);
    }

    [Fact]
    public async Task ImportFolder_copies_the_tree_as_a_child_of_the_storage_root_and_makes_nothing_logical()
    {
        _ext.Mp3("Sets/a.mp3");
        _ext.Mp3("Sets/2019/b.mp3");
        _ext.Dir("Sets/empty");
        _music.Dir("Sets");   // the name is taken

        var result = await _ops.ImportFolderAsync(_ext.Full("Sets"), null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(File.Exists(_music.Full("Sets (2)/a.mp3")));
        Assert.True(File.Exists(_music.Full("Sets (2)/2019/b.mp3")));
        Assert.True(Directory.Exists(_music.Full("Sets (2)/empty")));
        Assert.True(File.Exists(_ext.Full("Sets/a.mp3")));
        Assert.Empty(Snapshot.Items);
        Assert.Empty(Snapshot.Groups);
        Assert.Equal(SyncStatus.Pending, _manifest.Status);

        Assert.Contains("não existe", (await _ops.ImportFolderAsync(_ext.Full("nope"), null, CancellationToken.None)).Message);
        Assert.Contains("já está dentro", (await _ops.ImportFolderAsync(_music.Full("Sets"), null, CancellationToken.None)).Message);
        Assert.Contains("contém a pasta da biblioteca", (await _ops.ImportFolderAsync(Path.GetDirectoryName(_music.Root)!, null, CancellationToken.None)).Message);
    }

    [Fact]
    public async Task A_cancelled_import_leaves_nothing()
    {
        var a = _ext.Mp3("a.mp3");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _ops.ImportFilesAsync([a], null, null, cts.Token);

        Assert.False(result.Ok);
        Assert.Contains("cancelada", result.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_music.Root));
        Assert.Empty(InboxFiles);
    }

    [Fact]
    public async Task A_storage_root_that_refuses_sends_the_write_to_the_music_folder_and_says_so()
    {
        // The configured root refuses writes; the Music folder takes the copy and the new folder, and becomes
        // a source so the scanner sees them; the configuration is not touched.
        using var locked = new TempTree("locked");
        Assert.True(_ops.SetStorageRoot(locked.Root).Ok);
        locked.DenyWriting("");
        var a = _ext.Mp3("a.mp3");

        var import = await _ops.ImportFilesAsync([a], null, null, CancellationToken.None);
        var folder = _ops.CreatePhysicalFolder(null, "Nova");

        Assert.True(import.Ok && folder.Ok);
        Assert.Contains("sem permissão", import.Message);
        Assert.Contains("sem permissão", folder.Message);
        Assert.True(File.Exists(_music.Full("a.mp3")));
        Assert.True(Directory.Exists(_music.Full("Nova")));
        Assert.Empty(Directory.GetFileSystemEntries(locked.Root));
        Assert.Equal(locked.Root, Snapshot.StorageRoot);   // still configured
        Assert.Contains(Snapshot.Sources, s => PathUtil.IsSamePath(s.Locator.LastKnownPath, _music.Root));   // Music is observed now
        Assert.Equal([_music.Full("a.mp3")], InboxFiles);
    }

    // ───── Criar pasta ─────

    [Fact]
    public void CreatePhysicalFolder_makes_a_real_folder_inside_the_storage_root()
    {
        Synced();

        var top = _ops.CreatePhysicalFolder(null, " Albums ");
        var inner = _ops.CreatePhysicalFolder(_music.Full("Albums"), "Artist: A");

        Assert.True(top.Ok && inner.Ok);
        Assert.True(Directory.Exists(_music.Full("Albums")));
        Assert.True(Directory.Exists(_music.Full("Albums/Artist_ A")));   // an invalid character is replaced, the name is not refused
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
        Assert.Empty(Snapshot.Items);

        Assert.Contains("já existe", _ops.CreatePhysicalFolder(null, "Albums").Message);
        Assert.Contains("dentro da pasta da biblioteca", _ops.CreatePhysicalFolder(_ext.Root, "X").Message);
        Assert.False(_ops.CreatePhysicalFolder(null, "  ").Ok);
    }

    // ───── Groups ─────

    [Fact]
    public void Groups_nest_by_parent_rename_and_move_without_touching_memberships_and_a_cycle_is_refused()
    {
        Synced();
        var fav = _ops.CreateGroup("  Favoritos ");
        var mus = _ops.CreateGroup("Música", fav.Id);
        var gym = _ops.CreateGroup("Gym");
        Assert.True(_ops.AddToGroup(mus.Id!, [new Target(_ext.Mp3("a.mp3"), false)]).Ok);

        Assert.Equal(("Favoritos", (string?)null), (Snapshot.FindGroup(fav.Id)!.Name, Snapshot.FindGroup(fav.Id)!.ParentGroupId));
        Assert.Equal(fav.Id, Snapshot.FindGroup(mus.Id)!.ParentGroupId);
        Assert.Equal(SyncStatus.Synced, _manifest.Status);   // logical only

        Assert.True(_ops.RenameGroup(mus.Id!, "Músicas").Ok);
        Assert.Equal("Músicas", Snapshot.FindGroup(mus.Id)!.Name);
        Assert.False(_ops.RenameGroup(mus.Id!, "").Ok);
        Assert.False(_ops.CreateGroup("X", "nope").Ok);

        Assert.Contains("dentro de si mesmo", _ops.MoveGroup(fav.Id!, fav.Id).Message);
        Assert.Contains("dentro de si mesmo", _ops.MoveGroup(fav.Id!, mus.Id).Message);   // into its own child
        Assert.Contains("não existe", _ops.MoveGroup(fav.Id!, "nope").Message);
        Assert.Null(_ops.WhyNotMoveGroup(mus.Id!, gym.Id));
        Assert.True(_ops.MoveGroup(mus.Id!, gym.Id).Ok);
        Assert.Equal(gym.Id, Snapshot.FindGroup(mus.Id)!.ParentGroupId);
        Assert.True(_ops.MoveGroup(mus.Id!, null).Ok);
        Assert.Null(Snapshot.FindGroup(mus.Id)!.ParentGroupId);
        Assert.Single(Snapshot.Memberships);   // untouched throughout
    }

    [Fact]
    public void DeleteGroup_takes_the_subtree_and_its_memberships_and_items_left_without_state()
    {
        var fav = _ops.CreateGroup("Favoritos");
        var mus = _ops.CreateGroup("Música", fav.Id);
        var gym = _ops.CreateGroup("Gym");
        var a = _ext.Mp3("a.mp3");
        var b = _ext.Mp3("b.mp3");
        var src = _ops.AddFile(_ext.Mp3("src.mp3"));
        Assert.True(_ops.AddToGroup(mus.Id!, [new Target(a, false), new Target(b, false), new Target(_ext.Full("src.mp3"), false)]).Ok);
        Assert.True(_ops.AddToGroup(gym.Id!, [new Target(b, false)]).Ok);

        var result = _ops.DeleteGroup(fav.Id!);

        Assert.True(result.Ok);
        Assert.Contains("2 grupos", result.Message);
        Assert.Equal([gym.Id], Snapshot.Groups.Select(g => g.Id));
        Assert.Equal([gym.Id], Snapshot.Memberships.Select(m => m.GroupId));
        Assert.Null(Snapshot.FindItemByPath(a));                 // nothing named it any more
        Assert.NotNull(Snapshot.FindItemByPath(b));              // Gym still does
        Assert.True(Item(src.Id).IsSource);                      // a source stays a source
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.False(_ops.DeleteGroup("nope").Ok);
    }

    // ───── Memberships ─────

    [Fact]
    public void AddToGroup_makes_the_item_once_and_the_folder_stands_for_what_is_selected_inside_it()
    {
        var sets = _ext.Dir("Sets");
        var inside = _ext.Mp3("Sets/a.mp3");
        var loose = _ext.Mp3("loose.mp3");
        var fav = _ops.CreateGroup("Favoritos");
        var gym = _ops.CreateGroup("Gym");
        Synced();

        var first = _ops.AddToGroup(fav.Id!, [new Target(inside, false), new Target(sets, true), new Target(loose, false), new Target(loose.ToUpperInvariant(), false)]);

        Assert.True(first.Ok);
        Assert.Contains("2 adicionados", first.Message);
        var setsItem = Snapshot.FindItemByPath(sets)!;
        var looseItem = Snapshot.FindItemByPath(loose)!;
        Assert.Null(Snapshot.FindItemByPath(inside));   // the folder stands for it
        Assert.Equal((ItemKinds.Folder, false), (setsItem.Kind, setsItem.IsSource));
        Assert.NotNull(setsItem.Locator.FileId);
        Assert.Equal(new FileInfo(loose).Length, looseItem.Locator.Size);
        Assert.Equal([(fav.Id!, setsItem.Id), (fav.Id!, looseItem.Id)], Snapshot.Memberships.Select(m => (m.GroupId, m.ItemId)));
        Assert.Equal(SyncStatus.Synced, _manifest.Status);   // logical only

        var again = _ops.AddToGroup(fav.Id!, [new Target(loose, false)]);
        Assert.Contains("já estava", again.Message);
        var other = _ops.AddToGroup(gym.Id!, [new Target(loose, false)]);
        Assert.Contains("adicionado ao grupo", other.Message);
        Assert.Equal(2, Snapshot.Memberships.Count(m => m.ItemId == looseItem.Id));   // both, the first not moved
        Assert.Equal(2, Snapshot.Items.Count);
        Assert.Equal(looseItem.Id, _ops.EnsureItem(loose, false).Id);
        Assert.Contains("não uma pasta", _ops.EnsureItem(loose, true).Message);
        Assert.False(_ops.AddToGroup("nope", [new Target(loose, false)]).Ok);
    }

    [Fact]
    public void RemoveFromGroup_takes_one_membership_and_the_item_when_nothing_else_names_it()
    {
        var fav = _ops.CreateGroup("Favoritos");
        var gym = _ops.CreateGroup("Gym");
        var a = _ext.Mp3("a.mp3");
        Assert.True(_ops.AddToGroup(fav.Id!, [new Target(a, false)]).Ok);
        Assert.True(_ops.AddToGroup(gym.Id!, [new Target(a, false)]).Ok);
        var item = Snapshot.FindItemByPath(a)!;

        Assert.True(_ops.RemoveFromGroup(fav.Id!, item.Id).Ok);
        Assert.NotNull(Snapshot.FindItem(item.Id));   // Gym still names it
        Assert.Contains("não está", _ops.RemoveFromGroup(fav.Id!, item.Id).Message);
        Assert.True(_ops.RemoveFromGroup(gym.Id!, item.Id).Ok);
        Assert.Null(Snapshot.FindItem(item.Id));
        Assert.True(File.Exists(a));
    }

    // ───── Mover fisicamente ─────

    [Fact]
    public async Task MovePhysical_moves_files_and_folders_and_points_the_items_under_them_at_the_new_place()
    {
        var a = _music.Mp3("a.mp3");
        var sets = _music.Dir("Sets");
        var inSets = _music.Mp3("Sets/b.mp3");
        var deeper = _music.Mp3("Sets/2019/c.mp3");
        var archive = _music.Dir("Archive");
        var fav = _ops.CreateGroup("Favoritos");
        Assert.True(_ops.AddToGroup(fav.Id!, [new Target(a, false), new Target(sets, true)]).Ok);
        Assert.True(_ops.AddToGroup(fav.Id!, [new Target(inSets, false)]).Ok);   // on its own: with the folder selected too it would stand for it
        var idA = Snapshot.FindItemByPath(a)!.Id;
        var idB = Snapshot.FindItemByPath(inSets)!.Id;
        var idSets = Snapshot.FindItemByPath(sets)!.Id;
        Synced();

        var result = await _ops.MovePhysicalAsync([new Target(a, false), new Target(sets, true), new Target(deeper, false)], archive, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("2 movidos", result.Message);   // the file inside Sets went with Sets
        Assert.True(File.Exists(_music.Full("Archive/a.mp3")));
        Assert.True(File.Exists(_music.Full("Archive/Sets/b.mp3")));
        Assert.True(File.Exists(_music.Full("Archive/Sets/2019/c.mp3")));
        Assert.False(File.Exists(a) || Directory.Exists(sets));
        Assert.Equal(_music.Full("Archive/a.mp3"), Item(idA).Locator.LastKnownPath);
        Assert.Equal(_music.Full("Archive/Sets/b.mp3"), Item(idB).Locator.LastKnownPath);
        Assert.Equal(_music.Full("Archive/Sets"), Item(idSets).Locator.LastKnownPath);
        Assert.NotNull(Item(idB).Locator.FileId);
        Assert.Equal(3, Snapshot.Memberships.Count);   // untouched
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
    }

    [Fact]
    public async Task MovePhysical_refuses_a_collision_before_moving_anything_and_a_folder_into_itself()
    {
        var a = _music.Mp3("a.mp3");
        var b = _music.Mp3("b.mp3");
        var archive = _music.Dir("Archive");
        _music.Mp3("Archive/b.mp3");

        var collision = await _ops.MovePhysicalAsync([new Target(a, false), new Target(b, false)], archive, CancellationToken.None);
        Assert.Contains("já existe", collision.Message);
        Assert.True(File.Exists(a));   // nothing moved

        var into = await _ops.MovePhysicalAsync([new Target(archive, true)], _music.Full("Archive"), CancellationToken.None);
        Assert.Contains("dentro de si mesma", into.Message);
        Assert.Contains("não existe", (await _ops.MovePhysicalAsync([new Target(a, false)], _music.Full("nope"), CancellationToken.None)).Message);
        Assert.Contains("já está lá", (await _ops.MovePhysicalAsync([new Target(a, false)], _music.Root, CancellationToken.None)).Message);
    }

    [Fact]
    public void Outermost_keeps_a_folder_and_drops_what_is_inside_it()
    {
        var targets = LibraryOperations.Outermost([
            new Target(@"D:\Music\Sets\a.mp3", false), new Target(@"D:\Music\Sets", true), new Target(@"d:\music\sets\", true),
            new Target(@"D:\Music\Sets2\b.mp3", false), new Target(@"D:\Music\Sets\2019", true), new Target(@"relative\x", false)]);

        Assert.Equal([@"D:\Music\Sets", @"D:\Music\Sets2\b.mp3"], targets.Select(t => t.Path));
    }

    // ───── The storage root ─────

    [Fact]
    public void SetStorageRoot_changes_the_configuration_only()
    {
        using var other = new TempTree("other");

        Assert.True(_ops.SetStorageRoot(other.Root + @"\").Ok);
        Assert.Equal((other.Root, other.Root), (Snapshot.StorageRoot, _ops.StorageRoot));
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
        Assert.True(_ops.SetStorageRoot(_music.Root).Ok);
        Assert.Null(Snapshot.StorageRoot);   // the Music folder is the default, not a configuration
        Assert.True(_ops.SetStorageRoot(null).Ok);
        Assert.Contains("não uma pasta", _ops.SetStorageRoot(_ext.Mp3("a.mp3")).Message);
        Assert.Empty(Directory.GetFileSystemEntries(other.Root));
    }

    // ───── Conflicted ids ─────

    [Fact]
    public void Conflicted_ids_are_repaired_only_on_request()
    {
        var a = _ext.Mp3("a.mp3");
        _manifest.Update(m =>
        {
            m.Items.Add(Items.SourceFile(a, id: "dup"));
            m.Groups.Add(Items.Group("Dup", id: "dup"));
            m.Groups.Add(Items.Group("Child", id: "child", parent: "dup"));
            m.Memberships.Add(Items.Member("dup", "dup"));
        });
        Assert.Equal(["dup"], Snapshot.ConflictedIds);
        Assert.False(_ops.RegenerateConflictedIds("child").Ok);

        Assert.True(_ops.RegenerateConflictedIds("dup").Ok);
        Assert.Empty(Snapshot.ConflictedIds);
        Assert.NotEqual("dup", Snapshot.Items.Single().Id);
        Assert.Equal(["dup"], Snapshot.Memberships.Select(m => m.ItemId));   // dangling on purpose: a person repairs it knowing which

        _manifest.Update(m => m.Items.Add(Items.SourceFile(_ext.Mp3("b.mp3"), id: "x")));
        _manifest.Update(m => m.Groups.Add(Items.Group("X", id: "x")));
        Assert.True(_ops.RemoveAllWithId("x").Ok);
        Assert.DoesNotContain(Snapshot.Items, i => i.Id == "x");
        Assert.DoesNotContain(Snapshot.Groups, g => g.Id == "x");
    }

    // ───── Downloads ─────

    [Fact]
    public void Deliver_inboxes_a_file_under_the_storage_root_or_a_source_and_ignores_what_the_catalogue_does_not_take()
    {
        var sets = _ext.Dir("Sets");
        _ops.AddFolder(sets);
        Synced();
        var inRoot = _music.Mp3("new.mp3");
        var inSource = _ext.Mp3("Sets/x.mp3");

        Assert.True(_ops.Deliver(inRoot, _music.Root));
        Assert.True(_ops.Deliver(inSource, sets));
        Assert.False(_ops.Deliver(_music.Junk("clip.gif"), _music.Root));   // not an extension the catalogue takes

        Assert.Equal([inRoot, inSource], InboxFiles);
        Assert.Equal(SyncStatus.Pending, _manifest.Status);
        Assert.Single(Snapshot.Sources);   // nothing new
    }

    [Fact]
    public void Deliver_outside_every_source_adds_the_output_folder_when_allowed()
    {
        var output = _ext.Dir("Downloads");
        var file = _ext.Mp3("Downloads/song.mp3");

        Assert.True(_ops.Deliver(file, output));

        var source = Assert.Single(Snapshot.Sources);
        Assert.Equal(("Downloads", output), (source.Source!.Name, source.Locator.LastKnownPath));
        Assert.Equal([file], InboxFiles);
        Assert.Contains(_log, l => l.Contains("por causa de um download"));

        // the request said a folder the file is not under: the file's own folder is the source
        var elsewhere = _ext.Mp3("Other/song2.mp3");
        Assert.True(_ops.Deliver(elsewhere, _ext.Full("Nope")));
        Assert.Contains(Snapshot.Sources, s => PathUtil.IsSamePath(s.Locator.LastKnownPath, _ext.Full("Other")));
    }

    [Fact]
    public void Deliver_with_autoAddRoots_off_only_logs()
    {
        _manifest.Update(m => m.AutoAddRoots = false);
        var file = _ext.Mp3("Downloads/song.mp3");

        Assert.False(_ops.Deliver(file, _ext.Full("Downloads")));

        Assert.Empty(Snapshot.Items);
        Assert.Empty(InboxFiles);
        Assert.Contains(_log, l => l.Contains("ficou fora do catálogo"));
    }

    // ───── Read-only ─────

    [Fact]
    public async Task Every_operation_is_refused_on_a_read_only_manifest()
    {
        using var data = new TempTree("ro");
        File.WriteAllText(Path.Combine(data.Root, "manifest.v2.json"), """{ "schemaVersion": 99, "status": "synced" }""");
        var manifest = LibraryManifest.Load(data.Root, _music.Root);
        Assert.True(manifest.ReadOnly);
        var ops = new LibraryOperations(manifest, new LibraryStore(data.Root), new WindowsFileProbe(), (_, _) => { });
        var a = _ext.Mp3("a.mp3");

        var results = new List<OperationResult>
        {
            ops.AddFile(a), ops.AddFolder(_ext.Root), ops.RenameSource("x", "y"), ops.RemoveSource("x"), ops.Relocate("x", a),
            await ops.ImportFilesAsync([a], null, null, CancellationToken.None), await ops.ImportFolderAsync(_ext.Root, null, CancellationToken.None),
            ops.CreatePhysicalFolder(null, "X"), ops.CreateGroup("G"), ops.RenameGroup("g", "H"), ops.MoveGroup("g", null), ops.DeleteGroup("g"),
            ops.EnsureItem(a, false), ops.AddToGroup("g", [new Target(a, false)]), ops.RemoveFromGroup("g", "i"),
            await ops.MovePhysicalAsync([new Target(a, false)], _music.Root, CancellationToken.None), ops.SetStorageRoot(null),
            ops.RegenerateConflictedIds("x"), ops.RemoveAllWithId("x"),
        };

        Assert.All(results, r => Assert.Contains("só pra leitura", r.Message));
        Assert.False(ops.Deliver(a, _ext.Root));
        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFileSystemEntries(_music.Root));
    }

    public void Dispose()
    {
        _data.Dispose();
        _music.Dispose();
        _ext.Dispose();
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
