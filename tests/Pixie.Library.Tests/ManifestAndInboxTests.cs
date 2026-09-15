using System.IO;
using System.Text.Json;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The manifest's state machine on disk, and the inbox blocks.</summary>
public sealed class ManifestAndInboxTests : IDisposable
{
    private readonly TempTree _data = new("data");
    private string ManifestPath => _data.Full("manifest.json");

    // ───── Manifest ─────

    [Fact]
    public void First_load_writes_a_pending_manifest_with_the_defaults()
    {
        var manifest = LibraryManifest.Load(ManifestPath);

        Assert.Equal(SyncStatus.Pending, manifest.Status);
        Assert.Empty(manifest.Roots);
        Assert.True(manifest.AutoAddRoots);
        Assert.True(manifest.AutoSync);
        Assert.Null(manifest.Player);
        Assert.True(File.Exists(ManifestPath));
        Assert.False(File.Exists(ManifestPath + ".tmp"));
        var json = File.ReadAllText(ManifestPath);
        Assert.Contains("\"status\": \"pending\"", json);   // a person reads this file
        Assert.Contains("\"autoAddRoots\": true", json);
    }

    [Fact]
    public void Everything_but_the_flag_round_trips_through_Update()
    {
        var manifest = LibraryManifest.Load(ManifestPath);
        manifest.Update(m =>
        {
            m.Roots.Add(new LibraryRoot("Mixes", @"D:\Music\Mixes"));
            m.Player = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
            m.AutoAddRoots = false;
            m.ExtraExtensions.Add("gif");
        });
        Assert.True(manifest.TryMarkSynced(manifest.Generation, new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc)));

        var again = LibraryManifest.Load(ManifestPath);

        Assert.Equal(SyncStatus.Synced, again.Status);
        Assert.Equal([new LibraryRoot("Mixes", @"D:\Music\Mixes")], again.Roots);
        Assert.Equal(@"C:\Program Files\VideoLAN\VLC\vlc.exe", again.Player);
        Assert.False(again.AutoAddRoots);
        Assert.Equal(["gif"], again.ExtraExtensions);
        Assert.Contains(".gif", again.Extensions);
        Assert.Contains(".mp3", again.Extensions);
        Assert.Equal(new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc), again.LastSync);
    }

    [Fact]
    public void MarkPending_bumps_the_generation_and_the_swap_only_lands_on_the_one_it_read()
    {
        var manifest = LibraryManifest.Load(ManifestPath);
        var generation = manifest.Generation;
        manifest.MarkSyncing();
        Assert.Equal(SyncStatus.Syncing, manifest.Status);

        manifest.MarkPending();   // a download landed mid-scan

        Assert.False(manifest.TryMarkSynced(generation, DateTime.UtcNow));
        Assert.Equal(SyncStatus.Pending, manifest.Status);
        Assert.Equal(SyncStatus.Pending, LibraryManifest.Load(ManifestPath).Status);

        Assert.True(manifest.TryMarkSynced(manifest.Generation, DateTime.UtcNow));
        Assert.Equal(SyncStatus.Synced, LibraryManifest.Load(ManifestPath).Status);
    }

    [Fact]
    public void Syncing_found_on_disk_means_the_run_died_and_becomes_failed()
    {
        var manifest = LibraryManifest.Load(ManifestPath);
        manifest.MarkSyncing();

        var reopened = LibraryManifest.Load(ManifestPath);

        Assert.Equal(SyncStatus.Failed, reopened.Status);
        Assert.Contains("\"failed\"", File.ReadAllText(ManifestPath));
    }

    [Fact]
    public void An_unreadable_manifest_is_set_aside_and_started_over()
    {
        File.WriteAllText(ManifestPath, "{ this is not json");
        var warnings = new List<string>();

        var manifest = LibraryManifest.Load(ManifestPath, warnings.Add);

        Assert.Equal(SyncStatus.Pending, manifest.Status);
        Assert.True(File.Exists(ManifestPath + ".bad"));
        Assert.Single(warnings);
        Assert.NotNull(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(ManifestPath)).GetProperty("status").GetString());
    }

    [Fact]
    public void RootOf_picks_the_deepest_root_that_contains_the_file()
    {
        var manifest = LibraryManifest.Load(ManifestPath);
        manifest.Update(m =>
        {
            m.Roots.Add(new LibraryRoot("Tudo", @"D:\Music"));
            m.Roots.Add(new LibraryRoot("Sets", @"D:\Music\Sets\"));
        });

        Assert.Equal("Sets", manifest.RootOf(@"D:\Music\Sets\2025\mix.mp3")!.Name);
        Assert.Equal("Tudo", manifest.RootOf(@"D:\Music\solo.mp3")!.Name);
        Assert.Equal("Tudo", manifest.RootOf(@"d:\music\SETSHOW\x.mp3")!.Name);   // "Sets" is not a prefix of "SETSHOW"
        Assert.Null(manifest.RootOf(@"E:\other.mp3"));
    }

    // ───── Inbox ─────

    [Fact]
    public void Blocks_are_written_whole_read_oldest_first_and_deleted_after()
    {
        var inbox = new Inbox(_data.Full("inbox"));
        inbox.Append(@"D:\Music\first.mp3", new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc));
        inbox.Append(@"D:\Music\second.mp3", new DateTime(2026, 9, 15, 10, 0, 1, DateTimeKind.Utc));

        var blocks = inbox.ReadAll();

        Assert.Equal(2, blocks.Count);
        Assert.Equal([@"D:\Music\first.mp3"], blocks[0].Files);
        Assert.Equal([@"D:\Music\second.mp3"], blocks[1].Files);
        Assert.Empty(Directory.GetFiles(_data.Full("inbox"), "*.tmp"));

        inbox.Delete(blocks);
        Assert.Empty(inbox.ReadAll());
        Assert.Empty(Directory.GetFiles(_data.Full("inbox")));
    }

    [Fact]
    public void An_unreadable_block_is_dropped_on_read()
    {
        var inbox = new Inbox(_data.Full("inbox"));
        inbox.Append(@"D:\Music\ok.mp3");
        File.WriteAllText(_data.Full("inbox/00000000-000000-000-zzz.json"), "garbage");

        var blocks = inbox.ReadAll();

        Assert.Single(blocks);
        Assert.Single(Directory.GetFiles(_data.Full("inbox")));
    }

    [Fact]
    public void An_inbox_that_does_not_exist_yet_is_empty()
        => Assert.Empty(new Inbox(_data.Full("nope")).ReadAll());

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
    public void The_index_round_trips_with_full_timestamp_precision()
    {
        var store = new LibraryStore(_data.Root);
        var modified = new DateTime(638_000_000_123_456_7L, DateTimeKind.Utc);   // 100 ns ticks survive
        var index = new IndexData
        {
            ScannedAt = DateTime.UtcNow,
            Directories = [new DirectoryStamp(@"D:\Music", modified)],
            Entries = [new LibraryEntry { Path = @"D:\Music\a.mp3", Size = 10, Modified = modified, Created = modified, Title = "A", Chapters = 3 }],
        };

        store.WriteIndex(index);
        var back = store.ReadIndex();

        Assert.Equal(index.Entries, back.Entries);
        Assert.Equal(index.Directories, back.Directories);
        Assert.True(back.Entries[0].SameFileAs(10, modified));
        Assert.DoesNotContain("\"artist\"", File.ReadAllText(store.IndexPath));   // nulls stay out
    }

    public void Dispose() => _data.Dispose();
}
