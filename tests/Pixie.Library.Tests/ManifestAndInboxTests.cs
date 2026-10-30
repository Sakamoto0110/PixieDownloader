using System.IO;
using System.Text.Json;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The manifest's state machine on disk (items, groups, read-only from the future, the 1.x file left alone), the inbox blocks and the index file.</summary>
public sealed class ManifestAndInboxTests : IDisposable
{
    private const string Music = @"C:\Users\someone\Music";
    private readonly TempTree _data = new("data");
    private string ManifestPath => _data.Full("manifest.v2.json");

    // ───── Manifest ─────

    [Fact]
    public void First_load_writes_a_pending_manifest_with_the_defaults()
    {
        var manifest = LibraryManifest.Load(_data.Root, Music);

        Assert.Equal(SyncStatus.Pending, manifest.Status);
        Assert.Empty(manifest.Snapshot().Items);
        Assert.Empty(manifest.Snapshot().Groups);
        Assert.True(manifest.AutoAddRoots);
        Assert.True(manifest.AutoSync);
        Assert.True(manifest.GroupByFolder);
        Assert.Null(manifest.Player);
        Assert.Equal(Music, manifest.StorageRoot);
        Assert.Equal(Music, manifest.MusicFolder);
        Assert.False(manifest.ReadOnly);
        Assert.True(File.Exists(ManifestPath));
        Assert.False(File.Exists(ManifestPath + ".tmp"));
        Assert.False(File.Exists(_data.Full("manifest.json")));   // the 1.x file is never created
        var json = File.ReadAllText(ManifestPath);
        Assert.Contains("\"schemaVersion\": 3", json);
        Assert.Contains("\"status\": \"pending\"", json);   // a person reads this file
        Assert.Contains("\"autoAddRoots\": true", json);
        Assert.DoesNotContain("\"storageRoot\"", json);   // null = the Music folder, never written as a literal path
    }

    [Fact]
    public void Everything_but_the_flag_round_trips_through_Update()
    {
        var manifest = LibraryManifest.Load(_data.Root, Music);
        var source = Items.SourceFolder(@"D:\Music\Mixes", "Mixes", fileId: "0000000000000000000000000000abcd", serial: "1234abcd");
        var file = Items.File(@"E:\solo.mp3", id: "solo", fileId: "f1", serial: "1234abcd", size: 10, mtime: new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc));
        manifest.Update(m =>
        {
            m.Items.AddRange([source, file]);
            m.Groups.AddRange([Items.Group("Favoritos", "fav"), Items.Group("Música", "mus", parent: "fav")]);
            m.Memberships.Add(Items.Member("mus", "solo"));
            m.StorageRoot = @"E:\Biblioteca";
            m.Player = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
            m.AutoAddRoots = false;
            m.GroupByFolder = false;
            m.ExtraExtensions.Add("gif");
        });
        Assert.True(manifest.TryMarkSynced(manifest.Generation, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc)));

        var again = LibraryManifest.Load(_data.Root, Music);

        Assert.Equal(SyncStatus.Synced, again.Status);
        Assert.Equal(@"C:\Program Files\VideoLAN\VLC\vlc.exe", again.Player);
        Assert.Equal(@"E:\Biblioteca", again.StorageRoot);
        Assert.False(again.AutoAddRoots);
        Assert.False(again.GroupByFolder);
        Assert.Equal(["gif"], again.ExtraExtensions);
        Assert.Contains(".gif", again.Extensions);
        Assert.Contains(".mp3", again.Extensions);
        Assert.Equal(new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc), again.LastSync);
        var snapshot = again.Snapshot();
        Assert.Equal(2, snapshot.Items.Count);
        var s = snapshot.FindItem(source.Id)!;
        Assert.True(s.IsSource && s.IsFolder);
        Assert.Equal(("Mixes", @"D:\Music\Mixes", "0000000000000000000000000000abcd", "1234abcd"), (s.Source!.Name, s.Locator.LastKnownPath, s.Locator.FileId, s.Locator.VolumeSerial));
        var f = snapshot.FindItem("solo")!;
        Assert.False(f.IsSource);
        Assert.Equal((10L, new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc)), (f.Locator.Size, f.Locator.Mtime));
        Assert.Equal("fav", snapshot.FindGroup("mus")!.ParentGroupId);
        Assert.Equal([("mus", "solo")], snapshot.Memberships.Select(m => (m.GroupId, m.ItemId)));
        Assert.Same(s, snapshot.FindItemByPath(@"d:\music\MIXES\"));
        Assert.Equal([source.Id], snapshot.Sources.Select(i => i.Id));
        var json = File.ReadAllText(ManifestPath);
        Assert.Contains("\"kind\": \"folder\"", json);
        Assert.Contains("\"parentGroupId\": \"fav\"", json);
        Assert.DoesNotContain("\"source\": null", json);   // nulls stay out
        Assert.DoesNotContain("\"isSource\"", json);        // derived, never written
    }

    [Fact]
    public void Snapshots_are_copies_the_manifest_does_not_share()
    {
        var manifest = LibraryManifest.Load(_data.Root, Music);
        manifest.Update(m => m.Groups.Add(Items.Group("A", "a")));

        manifest.Snapshot().Groups[0].Name = "changed";

        Assert.Equal("A", manifest.Snapshot().FindGroup("a")!.Name);
    }

    [Fact]
    public void MarkPending_bumps_the_generation_and_the_swap_only_lands_on_the_one_it_read()
    {
        var manifest = LibraryManifest.Load(_data.Root, Music);
        var generation = manifest.Generation;
        manifest.MarkSyncing();
        Assert.Equal(SyncStatus.Syncing, manifest.Status);

        manifest.MarkPending();   // a download landed mid-scan

        Assert.False(manifest.TryMarkSynced(generation, DateTime.UtcNow));
        Assert.Equal(SyncStatus.Pending, manifest.Status);
        Assert.Equal(SyncStatus.Pending, LibraryManifest.Load(_data.Root, Music).Status);

        Assert.True(manifest.TryMarkSynced(manifest.Generation, DateTime.UtcNow));
        Assert.Equal(SyncStatus.Synced, LibraryManifest.Load(_data.Root, Music).Status);
    }

    [Fact]
    public void Syncing_found_on_disk_means_the_run_died_and_becomes_failed()
    {
        var manifest = LibraryManifest.Load(_data.Root, Music);
        manifest.MarkSyncing();

        var reopened = LibraryManifest.Load(_data.Root, Music);

        Assert.Equal(SyncStatus.Failed, reopened.Status);
        Assert.Contains("\"failed\"", File.ReadAllText(ManifestPath));
    }

    [Fact]
    public void An_unreadable_manifest_is_set_aside_and_the_backup_is_used()
    {
        var manifest = LibraryManifest.Load(_data.Root, Music);
        manifest.Update(m => m.Groups.Add(Items.Group("Sobrevive", "s")));
        manifest.Update(m => m.Player = "x.exe");   // this save keeps the previous file as .bak
        File.WriteAllText(ManifestPath, "{ this is not json");
        var warnings = new List<string>();

        var reopened = LibraryManifest.Load(_data.Root, Music, warnings.Add);

        Assert.NotNull(reopened.Snapshot().FindGroup("s"));
        Assert.True(File.Exists(ManifestPath + ".bad"));
        Assert.Contains(warnings, w => w.Contains("restaurado do .bak"));
        Assert.NotNull(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(ManifestPath)).GetProperty("status").GetString());
    }

    [Fact]
    public void An_unreadable_manifest_without_a_backup_starts_over()
    {
        File.WriteAllText(ManifestPath, "{ this is not json");
        var warnings = new List<string>();

        var manifest = LibraryManifest.Load(_data.Root, Music, warnings.Add);

        Assert.Equal(SyncStatus.Pending, manifest.Status);
        Assert.Empty(manifest.Snapshot().Items);
        Assert.True(File.Exists(ManifestPath + ".bad"));
        Assert.Contains(warnings, w => w.Contains("sem .bak"));
    }

    [Fact]
    public void A_manifest_from_a_newer_plugin_is_read_only()
    {
        File.WriteAllText(ManifestPath, """{ "schemaVersion": 4, "status": "synced", "items": [ { "id": "n1", "kind": "hologram", "locator": { "lastKnownPath": "D:\\x" } } ], "somethingNew": 1 }""");
        var warnings = new List<string>();

        var manifest = LibraryManifest.Load(_data.Root, Music, warnings.Add);

        Assert.True(manifest.ReadOnly);
        Assert.Equal(SyncStatus.Synced, manifest.Status);
        Assert.Contains(warnings, w => w.Contains("versão mais nova"));
        Assert.Throws<InvalidOperationException>(() => manifest.Update(m => m.Player = "x"));
        manifest.MarkPending();   // silently nothing
        Assert.Contains("\"schemaVersion\": 4", File.ReadAllText(ManifestPath));   // untouched
        Assert.Equal("hologram", manifest.Snapshot().FindItem("n1")!.Kind);   // kept as data
        Assert.True(manifest.Snapshot().ReadOnly);
    }

    [Fact]
    public void The_unpublished_2_0_shape_is_set_aside_and_the_1x_manifest_migrated_instead()
    {
        // The branch's schema 2 (nodes, managed root) never shipped: there is nothing to convert, only the owner's own files to step over.
        File.WriteAllText(ManifestPath, """{ "schemaVersion": 2, "revision": 25, "status": "synced", "nodes": [ { "id": "n1", "kind": "virtualFolder", "name": "favoritos" } ] }""");
        File.WriteAllText(_data.Full("manifest.json"), """{ "schemaVersion": 1, "status": "synced", "roots": [ { "name": "Sets", "path": "D:\\Sets" } ] }""");
        var warnings = new List<string>();

        var manifest = LibraryManifest.Load(_data.Root, Music, warnings.Add);

        Assert.False(manifest.ReadOnly);
        Assert.True(File.Exists(ManifestPath + ".bad"));
        Assert.Contains(warnings, w => w.Contains("schema 2"));
        Assert.Contains(warnings, w => w.Contains("migrado"));
        Assert.Equal([@"D:\Sets"], manifest.Snapshot().Sources.Select(s => s.Locator.LastKnownPath));
        Assert.Empty(manifest.Snapshot().Groups);
        Assert.Contains("\"schemaVersion\": 3", File.ReadAllText(ManifestPath));
    }

    [Fact]
    public void Unknown_properties_survive_a_round_trip()
    {
        File.WriteAllText(ManifestPath, """{ "schemaVersion": 3, "status": "pending", "futureOption": { "a": 1 }, "items": [ { "id": "n1", "kind": "file", "locator": { "lastKnownPath": "D:\\x.mp3" }, "futureField": "keep" } ], "groups": [ { "id": "g", "name": "X", "futureToo": 2 } ] }""");
        var manifest = LibraryManifest.Load(_data.Root, Music);

        manifest.Update(m => m.Player = "p.exe");

        var json = File.ReadAllText(ManifestPath);
        Assert.Contains("\"futureOption\"", json);
        Assert.Contains("\"futureField\": \"keep\"", json);
        Assert.Contains("\"futureToo\": 2", json);
    }

    [Fact]
    public void A_duplicated_id_is_a_conflict_that_hides_both_from_Find_across_items_and_groups()
    {
        File.WriteAllText(ManifestPath, """{ "schemaVersion": 3, "status": "pending", "items": [ { "id": "dup", "kind": "file", "locator": { "lastKnownPath": "D:\\a.mp3" }, "source": { "name": "a" } } ], "groups": [ { "id": "dup", "name": "B" }, { "id": "ok", "name": "C" } ] }""");

        var manifest = LibraryManifest.Load(_data.Root, Music);

        Assert.Equal(["dup"], manifest.ConflictedIds);
        var snapshot = manifest.Snapshot();
        Assert.Null(snapshot.FindItem("dup"));
        Assert.Null(snapshot.FindGroup("dup"));
        Assert.NotNull(snapshot.FindGroup("ok"));
        Assert.Empty(snapshot.Sources);
        Assert.Single(snapshot.Items);   // nothing dropped, nothing repaired
        Assert.Equal(2, snapshot.Groups.Count);
    }

    [Fact]
    public void A_1x_manifest_is_migrated_on_the_first_load_and_left_as_it_was()
    {
        var v1 = """{ "schemaVersion": 1, "status": "synced", "roots": [ { "name": "Músicas", "path": "D:\\Music" }, { "name": "Sets", "path": "D:\\Music\\Sets\\" }, { "name": "Music", "path": "C:\\Users\\someone\\Music" } ], "player": "C:\\vlc\\vlc.exe", "autoAddRoots": false, "autoSync": true, "extraExtensions": [ "gif" ], "groupByFolder": false, "lastSync": "2026-09-16T10:00:00Z" }""";
        File.WriteAllText(_data.Full("manifest.json"), v1);
        var warnings = new List<string>();

        var manifest = LibraryManifest.Load(_data.Root, Music, warnings.Add);

        Assert.Contains(warnings, w => w.Contains("migrado"));
        Assert.Equal(SyncStatus.Pending, manifest.Status);   // the first 2.x scan builds the tree
        Assert.Equal(@"C:\vlc\vlc.exe", manifest.Player);
        Assert.False(manifest.AutoAddRoots);
        Assert.False(manifest.GroupByFolder);
        Assert.Equal(["gif"], manifest.ExtraExtensions);
        Assert.Equal(Music, manifest.StorageRoot);   // the Music root of the 1.x is the storage root, not a source
        var items = manifest.Snapshot().Items;
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.IsSource && i.IsFolder));
        Assert.Equal((@"D:\Music", "Músicas"), (items[0].Locator.LastKnownPath, items[0].Source!.Name));
        Assert.Equal(@"D:\Music\Sets", items[1].Locator.LastKnownPath);   // normalized: no trailing separator
        var legacy = manifest.Snapshot().LegacyV1!;
        Assert.Equal([@"c:\users\someone\music", @"d:\music", @"d:\music\sets"], legacy.ObservedRoots);
        Assert.NotNull(legacy.Fingerprint);
        Assert.NotNull(legacy.ObservedAt);
        Assert.Equal(v1, File.ReadAllText(_data.Full("manifest.json")));   // byte for byte
        Assert.True(File.Exists(ManifestPath));
    }

    // ───── Inbox ─────

    [Fact]
    public void Blocks_are_written_whole_read_oldest_first_and_deleted_after()
    {
        var inbox = new Inbox(_data.Full("inbox.v2"));
        inbox.Append(@"D:\Music\first.mp3", new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc));
        inbox.Append(@"D:\Music\second.mp3", new DateTime(2026, 9, 15, 10, 0, 1, DateTimeKind.Utc));

        var blocks = inbox.ReadAll();

        Assert.Equal(2, blocks.Count);
        Assert.Equal([@"D:\Music\first.mp3"], blocks[0].Files);
        Assert.Equal([@"D:\Music\second.mp3"], blocks[1].Files);
        Assert.Empty(Directory.GetFiles(_data.Full("inbox.v2"), "*.tmp"));

        inbox.Delete(blocks);
        Assert.Empty(inbox.ReadAll());
        Assert.Empty(Directory.GetFiles(_data.Full("inbox.v2")));
    }

    [Fact]
    public void An_unreadable_block_is_dropped_on_read()
    {
        var inbox = new Inbox(_data.Full("inbox.v2"));
        inbox.Append(@"D:\Music\ok.mp3");
        File.WriteAllText(_data.Full("inbox.v2/00000000-000000-000-zzz.json"), "garbage");

        var blocks = inbox.ReadAll();

        Assert.Single(blocks);
        Assert.Single(Directory.GetFiles(_data.Full("inbox.v2")));
    }

    [Fact]
    public void An_inbox_that_does_not_exist_yet_is_empty()
        => Assert.Empty(new Inbox(_data.Full("nope")).ReadAll());

    [Fact]
    public void The_store_points_at_the_versioned_files()
    {
        var store = new LibraryStore(_data.Root);

        Assert.Equal(_data.Full("manifest.v2.json"), store.ManifestPath);
        Assert.Equal(_data.Full("manifest.json"), store.LegacyManifestPath);
        Assert.Equal(_data.Full("index.v2.json"), store.IndexPath);
        Assert.Equal(_data.Full("inbox"), store.LegacyInboxDirectory);
    }

    // ───── Store ─────

    [Fact]
    public void An_unreadable_index_is_set_aside_and_read_as_empty()
    {
        var store = new LibraryStore(_data.Root);
        File.WriteAllText(store.IndexPath, "{ nope");
        var warnings = new List<string>();

        var index = store.ReadIndex(warnings.Add);

        Assert.Empty(index.Entries);
        Assert.True(File.Exists(store.IndexPath + ".bad"));
        Assert.Single(warnings);
    }

    [Fact]
    public void An_index_from_a_newer_plugin_is_set_aside_too()
    {
        var store = new LibraryStore(_data.Root);
        File.WriteAllText(store.IndexPath, """{ "schemaVersion": 9, "entries": [] }""");
        var warnings = new List<string>();

        var index = store.ReadIndex(warnings.Add);

        Assert.Empty(index.Entries);
        Assert.Null(index.ScannedAt);
        Assert.True(File.Exists(store.IndexPath + ".bad"));
        Assert.Single(warnings);
    }

    [Fact]
    public void The_index_round_trips_as_a_tree_with_full_timestamp_precision()
    {
        var store = new LibraryStore(_data.Root);
        var modified = new DateTime(638_000_000_123_456_7L, DateTimeKind.Utc);   // 100 ns ticks survive
        var a = new FileNode { Name = "a.mp3", Size = 10, Modified = modified, Created = modified, Title = "A", Chapters = 3, FileId = "00ff" };
        var index = new IndexData
        {
            ScannedAt = DateTime.UtcNow,
            Extensions = [".mp3"],
            RulesVersion = LibraryScanner.RulesVersion,
            Sources =
            [
                new SourceTree
                {
                    RootPath = @"D:\Music", VolumeSerial = "1234",
                    Root = new FolderNode
                    {
                        Name = "Music", Modified = modified,
                        Folders = [new FolderNode { Name = "locked", State = NodeStates.Inaccessible }],
                        Files = [a],
                    },
                },
                new SourceTree { RootPath = @"E:\solo.mp3", Kind = SourceKinds.File, State = NodeStates.Unavailable, File = new FileNode { Name = "solo.mp3", Size = 5, Modified = modified, Created = modified } },
            ],
        };

        store.WriteIndex(index);
        var back = store.ReadIndex();

        Assert.Equal([@"D:\Music\a.mp3", @"E:\solo.mp3"], back.Entries.Select(e => e.FullPath));
        var file = back.FindFile(@"d:\music\A.MP3")!;
        Assert.Equal((10L, modified, modified, "A", 3, "00ff", "1234"), (file.Size, file.Modified, file.Created, file.Title, file.Chapters, file.FileId, file.VolumeSerial));
        Assert.Same(back.Sources[0].Root, file.Parent);
        Assert.Equal(modified, back.FindFolder(@"D:\Music")!.Modified);
        Assert.True(back.FindFolder(@"D:\Music\locked")!.IsInaccessible);
        Assert.Equal([@"D:\Music\locked"], back.InaccessibleDirectories);
        Assert.True(back.Sources[1].IsUnavailable);
        Assert.Equal(@"E:\solo.mp3", back.Sources[1].File!.FullPath);
        Assert.True(file.SameFileAs(10, modified, "00FF"));    // the id compares case-insensitively
        Assert.True(file.SameFileAs(10, modified, null));      // an observation without an id cannot contradict
        Assert.False(file.SameFileAs(10, modified, "0001"));   // another object at the same path
        Assert.False(file.SameFileAs(11, modified, "00ff"));
        var json = File.ReadAllText(store.IndexPath);
        Assert.DoesNotContain("\"artist\"", json);     // nulls stay out
        Assert.DoesNotContain("\"fullPath\"", json);   // derived, never written
        Assert.DoesNotContain("\"parent\"", json);
    }

    [Fact]
    public void An_index_of_another_schema_is_set_aside_and_rebuilt()
    {
        // The flat index of the 2.0 branch (never published) and any older one: not converted, scanned again.
        var store = new LibraryStore(_data.Root);
        File.WriteAllText(store.IndexPath, """{ "schemaVersion": 2, "generation": 4, "entries": [] }""");
        var warnings = new List<string>();

        var index = store.ReadIndex(warnings.Add);

        Assert.Empty(index.Sources);
        Assert.True(File.Exists(store.IndexPath + ".bad"));
        Assert.Single(warnings);
    }

    public void Dispose() => _data.Dispose();
}
