using System.IO;
using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The 2.x reading the 1.x side: once to migrate, then through the snapshot; never writing it. The Music folder is the storage root, never a source.</summary>
public sealed class LegacyMigrationTests : IDisposable
{
    private const string Music = @"C:\Users\someone\Music";
    private readonly TempTree _data = new("data");

    private string V1Path => _data.Full("manifest.json");
    private string V1Inbox => _data.Full("inbox");

    private void WriteV1(params (string Name, string Path)[] roots)
    {
        var items = string.Join(", ", roots.Select(r => $$"""{ "name": "{{r.Name}}", "path": "{{r.Path.Replace(@"\", @"\\")}}" }"""));
        File.WriteAllText(V1Path, $$"""{ "schemaVersion": 1, "status": "synced", "roots": [ {{items}} ], "autoAddRoots": true, "autoSync": true, "extraExtensions": [], "groupByFolder": true }""");
    }

    [Fact]
    public void Migrate_turns_roots_into_sources_except_the_music_folder_and_takes_the_snapshot()
    {
        var v1 = new LegacyManifestV1
        {
            Roots = [new LibraryRoot("Músicas", @"D:\Music\"), new LibraryRoot("", @"E:\Sets"), new LibraryRoot("Bad", @"relative\x"), new LibraryRoot("Twin", @"d:\MUSIC"), new LibraryRoot("Music", Music + @"\")],
            Player = "vlc.exe", AutoAddRoots = false, AutoSync = false, ExtraExtensions = ["gif"], GroupByFolder = false, Status = SyncStatus.Synced,
        };

        var data = LegacyMigration.Migrate(v1, new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc), Music);

        Assert.Equal(SyncStatus.Pending, data.Status);
        Assert.Equal(("vlc.exe", false, false, false), (data.Player, data.AutoAddRoots, data.AutoSync, data.GroupByFolder));
        Assert.Equal(["gif"], data.ExtraExtensions);
        Assert.Null(data.StorageRoot);   // the Music root is the storage root, implicit
        Assert.Equal(2, data.Items.Count);   // the relative one is dropped, the twin is the same path, the Music root is not a source
        Assert.Equal(["Músicas", "Sets"], data.Items.Select(i => i.Source!.Name));
        Assert.Equal([@"D:\Music", @"E:\Sets"], data.Items.Select(i => i.Locator.LastKnownPath));
        Assert.All(data.Items, i => Assert.Equal((ItemKinds.Folder, true), (i.Kind, i.IsSource)));
        Assert.All(data.Items, i => Assert.False(string.IsNullOrEmpty(i.Id)));
        Assert.Empty(data.Groups);
        Assert.Equal([@"c:\users\someone\music", @"d:\music", @"e:\sets"], data.LegacyV1!.ObservedRoots);
        Assert.Equal(new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc), data.LegacyV1.ObservedAt);
        Assert.Equal(64, data.LegacyV1.Fingerprint!.Length);
    }

    [Fact]
    public void A_root_added_during_a_downgrade_is_imported_once_and_a_removed_one_is_not_propagated()
    {
        WriteV1(("Músicas", @"D:\Music"));
        var manifest = LibraryManifest.Load(_data.Root, Music);
        Assert.Single(manifest.Snapshot().Items);

        // Nothing changed on the 1.x side: nothing happens, whatever the 2.x did meanwhile.
        manifest.Update(m => m.Items.Clear());
        Assert.Empty(LegacyMigration.SyncRootsSnapshot(manifest, LegacyMigration.TryReadV1(V1Path), DateTime.UtcNow));
        Assert.Empty(manifest.Snapshot().Items);   // the removal in 2.x stands: "v1 − v2" would have resurrected it

        // The 1.x gained a root (and the Music folder) and lost one.
        WriteV1(("Sets", @"E:\Sets"), ("Music", Music));
        var added = LegacyMigration.SyncRootsSnapshot(manifest, LegacyMigration.TryReadV1(V1Path), DateTime.UtcNow);

        var item = Assert.Single(added);
        Assert.Equal((@"E:\Sets", "Sets"), (item.Locator.LastKnownPath, item.Source!.Name));
        Assert.Equal([item.Id], manifest.Snapshot().Items.Select(i => i.Id));
        Assert.Equal([@"c:\users\someone\music", @"e:\sets"], manifest.Snapshot().LegacyV1!.ObservedRoots);

        // Again with the same 1.x file: the fingerprint matches, nothing to do — and a path an item already has is not doubled.
        Assert.Empty(LegacyMigration.SyncRootsSnapshot(manifest, LegacyMigration.TryReadV1(V1Path), DateTime.UtcNow));
        WriteV1(("Sets", @"E:\Sets"), ("Manual twin", @"e:\SETS\"));
        Assert.Empty(LegacyMigration.SyncRootsSnapshot(manifest, LegacyMigration.TryReadV1(V1Path), DateTime.UtcNow));
        Assert.Single(manifest.Snapshot().Items);
        Assert.Empty(LegacyMigration.SyncRootsSnapshot(manifest, null, DateTime.UtcNow));   // no 1.x file at all
    }

    [Fact]
    public void An_unreadable_1x_manifest_is_ignored_with_a_warning()
    {
        File.WriteAllText(V1Path, "{ nope");
        var warnings = new List<string>();

        Assert.Null(LegacyMigration.TryReadV1(V1Path, warnings.Add));
        Assert.Single(warnings);
        var manifest = LibraryManifest.Load(_data.Root, Music, warnings.Add);
        Assert.Empty(manifest.Snapshot().Items);
        Assert.Null(manifest.Snapshot().LegacyV1);
        Assert.Equal("{ nope", File.ReadAllText(V1Path));
    }

    [Fact]
    public void The_1x_inbox_is_translated_once_by_block_name_and_never_written()
    {
        var manifest = LibraryManifest.Load(_data.Root, Music);
        Directory.CreateDirectory(V1Inbox);
        File.WriteAllText(Path.Combine(V1Inbox, "2026-09-16-a.json"), """{ "createdAt": "2026-09-16T10:00:00Z", "files": [ "D:\\Music\\one.mp3", "D:\\Music\\two.mp3" ] }""");
        File.WriteAllText(Path.Combine(V1Inbox, "2026-09-16-b.json"), "garbage");
        var delivered = new List<(string, DateTime)>();
        var warnings = new List<string>();

        var count = LegacyMigration.TranslateInbox(manifest, V1Inbox, (file, at) => { delivered.Add((file, at)); return file.EndsWith("one.mp3"); }, warnings.Add);

        Assert.Equal(1, count);   // the delegate said only one entered the catalogue
        Assert.Equal([(@"D:\Music\one.mp3", new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc)), (@"D:\Music\two.mp3", new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc))], delivered);
        Assert.Contains(warnings, w => w.Contains("2026-09-16-b.json"));
        Assert.Equal(["2026-09-16-a.json", "2026-09-16-b.json"], manifest.Snapshot().LegacyV1!.ObservedInbox);
        Assert.Equal(2, Directory.GetFiles(V1Inbox).Length);   // still there for the 1.x

        // A second start: nothing is replayed.
        delivered.Clear();
        Assert.Equal(0, LegacyMigration.TranslateInbox(manifest, V1Inbox, (_, _) => true));
        Assert.Empty(delivered);

        // The 1.x consumed a block (deleted it) and wrote a new one: only the new one is replayed, the list is pruned.
        File.Delete(Path.Combine(V1Inbox, "2026-09-16-a.json"));
        File.WriteAllText(Path.Combine(V1Inbox, "2026-09-17-c.json"), """{ "createdAt": "2026-09-17T10:00:00Z", "files": [ "D:\\Music\\three.mp3" ] }""");
        Assert.Equal(1, LegacyMigration.TranslateInbox(manifest, V1Inbox, (file, _) => { delivered.Add((file, default)); return true; }));
        Assert.Equal([@"D:\Music\three.mp3"], delivered.Select(d => d.Item1));
        Assert.Equal(["2026-09-16-b.json", "2026-09-17-c.json"], manifest.Snapshot().LegacyV1!.ObservedInbox);

        Assert.Equal(0, LegacyMigration.TranslateInbox(manifest, _data.Full("no-such-inbox"), (_, _) => true));
    }

    [Fact]
    public void NameFor_is_the_folders_own_name_or_the_whole_path_of_a_volume_root()
    {
        Assert.Equal("Music", LegacyMigration.NameFor(@"D:\Music\"));
        Assert.Equal("D:", LegacyMigration.NameFor(@"D:\"));
        Assert.Equal(@"\\nas\share", LegacyMigration.NameFor(@"\\nas\share\"));   // a share is a root too
        Assert.Equal("Music", LegacyMigration.NameFor(@"\\nas\share\Music"));
    }

    public void Dispose() => _data.Dispose();
}
