using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>
/// The reconciler over a table-driven file system: the storage root, every item resolved through the tree, the
/// disk and the volume (present, replaced, relocated, unverifiable, broken), the group tree with its parents
/// and cycles, the memberships, and the actions it asks for without doing them.
/// </summary>
public sealed class ReconcilerTests
{
    private const string Music = @"C:\Users\someone\Music";
    private static readonly DateTime M = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static ReconcileResult Run(ManifestData data, FakeProbe probe, IndexData? index = null, bool readOnly = false, SyncStatus status = SyncStatus.Synced)
    {
        data.Status = status;
        var snapshot = Items.Snapshot(data, readOnly);
        var plan = ScanPlan.Build(snapshot, Music);
        index ??= new TestIndex(plan).Build();
        return Reconciler.Build(new ReconcileInput(snapshot, index, plan), probe);
    }

    /// <summary>A scanned tree with these files, each under the plan's source that contains it.</summary>
    private static IndexData Index(ManifestData data, params (string Path, long Size, string? FileId)[] files)
    {
        var index = new TestIndex(ScanPlan.Build(Items.Snapshot(data), Music));
        foreach (var (path, size, fileId) in files)
            index.File(path, size, M, fileId, serial: "VOL1");
        return index.Build();
    }

    private static (string Path, long Size, string? FileId) File(string path, long size = 100, string? fileId = null) => (path, size, fileId);

    private static FakeProbe ProbeWithMusic() => new FakeProbe().Folder(Music);

    // ───── The storage root ─────

    [Fact]
    public void The_storage_root_is_probed_and_its_absence_is_a_warning_not_an_error()
    {
        var data = Items.Data();

        var present = Run(data, ProbeWithMusic());
        Assert.Equal(Presence.Present, present.Model.Status.StorageRootPresence);
        Assert.Empty(present.Model.Problems);
        Assert.Equal(Music, present.Model.StorageRoot);

        var missing = Run(data, new FakeProbe().Root(Music));   // the drive is there, the folder is not
        Assert.Equal(Presence.Missing, missing.Model.Status.StorageRootPresence);
        var problem = Assert.Single(missing.Model.Problems);
        Assert.Equal(("storageRootMissing", Severity.Warning), (problem.Code, problem.Severity));

        var unknown = Run(data, new FakeProbe());   // no drive at all
        Assert.Equal(Presence.Unknown, unknown.Model.Status.StorageRootPresence);
        Assert.Equal("storageRootUnknown", Assert.Single(unknown.Model.Problems).Code);

        var file = Run(data, new FakeProbe().File(Music));
        Assert.Equal(Presence.TypeMismatch, file.Model.Status.StorageRootPresence);
        Assert.Equal(Severity.Error, Assert.Single(file.Model.Problems).Severity);
    }

    // ───── Items: the tree, the disk, the volume ─────

    [Fact]
    public void A_source_that_is_there_is_present_with_its_tree_and_a_locator_without_a_fingerprint_learns_it_silently()
    {
        var complete = Items.SourceFolder(@"D:\Sets", id: "sets", fileId: "d1", serial: "VOL1");
        var learning = Items.SourceFile(@"E:\solo.mp3", id: "solo");   // added before any probe answered
        var data = Items.Data(complete, learning);
        var probe = ProbeWithMusic().Folder(@"D:\Sets", fileId: "d1").File(@"E:\solo.mp3", size: 7, modified: M, fileId: "f2");
        var index = Index(data, File(@"D:\Sets\a.mp3", fileId: "a1"), File(@"E:\solo.mp3", 7, "f2"));

        var (model, actions) = Run(data, probe, index);

        var sets = model.FindItem("sets")!;
        Assert.Equal((Presence.Present, Resolution.Resolved, @"D:\Sets"), (sets.Presence, sets.Resolution, sets.ResolvedPath));
        Assert.NotNull(sets.Tree);
        Assert.Same(sets.Tree!.Root, sets.Folder);
        Assert.True(sets.Indexed);
        Assert.Equal("Sets", sets.Name);
        Assert.Empty(sets.Self);
        var solo = model.FindItem("solo")!;
        Assert.Equal(Presence.Present, solo.Presence);
        Assert.Equal(@"E:\solo.mp3", solo.File!.FullPath);
        var rewrite = Assert.Single(actions.OfType<RewriteLocator>());
        Assert.Equal(("solo", "f2", "VOL1", 7L, M), (rewrite.ItemId, rewrite.Locator.FileId, rewrite.Locator.VolumeSerial, rewrite.Locator.Size, rewrite.Locator.Mtime));
        Assert.Equal(["sets", "solo"], model.Sources.Select(s => s.Id));
        Assert.Empty(model.Problems);
    }

    [Fact]
    public void Another_object_at_the_same_path_is_replaced_and_the_locator_is_rewritten()
    {
        var data = Items.Data(Items.SourceFile(@"E:\a.mp3", id: "a", fileId: "old", serial: "VOL1", size: 100, mtime: M));
        var probe = ProbeWithMusic().File(@"E:\a.mp3", fileId: "new");

        var (model, actions) = Run(data, probe);

        var item = model.FindItem("a")!;
        Assert.Equal((Presence.Present, Resolution.Replaced), (item.Presence, item.Resolution));
        Assert.Equal("link.replaced", Assert.Single(item.Self).Code);
        Assert.Equal("new", Assert.Single(actions.OfType<RewriteLocator>()).Locator.FileId);
    }

    [Fact]
    public void A_file_moved_inside_the_library_is_found_in_the_tree_by_its_id_before_the_volume_is_asked()
    {
        var data = Items.Data(Items.SourceFolder(@"D:\Music", id: "d"), Items.File(@"D:\Music\a.mp3", id: "a", fileId: "f1", serial: "VOL1"), Items.Group("Fav", id: "g"), Items.Member("g", "a"));
        var probe = ProbeWithMusic().Folder(@"D:\Music").File(@"D:\Music\Archive\a.mp3", fileId: "f1");   // gone from where it was
        var index = Index(data, File(@"D:\Music\Archive\a.mp3", fileId: "f1"));

        var (model, actions) = Run(data, probe, index);

        var item = model.FindItem("a")!;
        Assert.Equal((Presence.Present, Resolution.Relocated, @"D:\Music\Archive\a.mp3"), (item.Presence, item.Resolution, item.ResolvedPath));
        Assert.Equal(@"D:\Music\Archive\a.mp3", item.File!.FullPath);
        Assert.Empty(probe.IdLookups);   // the tree answered
        Assert.Equal(@"D:\Music\Archive\a.mp3", Assert.Single(actions.OfType<RewriteLocator>()).Locator.LastKnownPath);
        Assert.Equal(["g"], item.Groups.Select(g => g.Id));   // the membership came along
        Assert.DoesNotContain(actions, a => a is RequestManualRelocation);
    }

    [Fact]
    public void A_folder_moved_on_its_volume_is_found_by_the_probe()
    {
        var data = Items.Data(Items.SourceFolder(@"D:\Sets", id: "s", fileId: "d1", serial: "VOL1"));
        var probe = ProbeWithMusic().Folder(@"D:\Old\Sets", fileId: "d1").Root(@"D:\Sets");

        var (model, actions) = Run(data, probe);

        var item = model.FindItem("s")!;
        Assert.Equal((Resolution.Relocated, @"D:\Old\Sets"), (item.Resolution, item.ResolvedPath));
        Assert.Equal(["d1"], probe.IdLookups);
        Assert.Equal("link.relocated", Assert.Single(item.Self).Code);
        Assert.Single(actions.OfType<RewriteLocator>());
    }

    [Fact]
    public void A_missing_item_on_a_volume_that_is_off_is_unverifiable_and_a_source_says_so_in_the_problems()
    {
        var data = Items.Data(Items.SourceFolder(@"F:\Sets", id: "s", fileId: "d1", serial: "VOLF"), Items.File(@"F:\loose.mp3", id: "l", fileId: "x", serial: "VOLF"), Items.Group("G", id: "g"), Items.Member("g", "l"));
        var probe = ProbeWithMusic();   // no F: at all

        var (model, actions) = Run(data, probe);

        var s = model.FindItem("s")!;
        Assert.Equal((Presence.Unknown, Resolution.Unverifiable), (s.Presence, s.Resolution));
        Assert.Equal((Presence.Unknown, Resolution.Unverifiable), (model.FindItem("l")!.Presence, model.FindItem("l")!.Resolution));
        Assert.Equal("sourceUnavailable", Assert.Single(model.Problems).Code);   // only the source is a problem of the whole; the member says it on its row
        Assert.Empty(actions);
        Assert.Empty(probe.IdLookups);
    }

    [Fact]
    public void A_missing_item_on_a_volume_that_is_on_and_not_found_is_broken_and_asks_a_person()
    {
        var data = Items.Data(Items.SourceFile(@"E:\gone.mp3", id: "c", fileId: "f1", serial: "VOL1"), Items.File(@"E:\member.mp3", id: "m", fileId: "f2", serial: "VOL1"), Items.Group("G", id: "g"), Items.Member("g", "m"));
        var probe = ProbeWithMusic().Root(@"E:\gone.mp3").Volume("VOL1");

        var (model, actions) = Run(data, probe);

        Assert.Equal((Presence.Missing, Resolution.Broken), (model.FindItem("c")!.Presence, model.FindItem("c")!.Resolution));
        Assert.Equal(Resolution.Broken, model.FindItem("m")!.Resolution);
        Assert.Equal(["f1", "f2"], probe.IdLookups);
        Assert.Equal(["c", "m"], actions.OfType<RequestManualRelocation>().Select(a => a.ItemId));
        Assert.Equal("sourceBroken", Assert.Single(model.Problems).Code);
        Assert.Equal(["g"], model.FindItem("m")!.Groups.Select(g => g.Id));   // the membership is not touched
    }

    [Fact]
    public void A_missing_item_without_a_fingerprint_cannot_be_looked_up_and_is_broken_at_once()
    {
        var data = Items.Data(Items.SourceFile(@"E:\gone.mp3", id: "c"));
        var probe = ProbeWithMusic().Root(@"E:\gone.mp3");

        var (model, _) = Run(data, probe);

        Assert.Equal(Resolution.Broken, model.FindItem("c")!.Resolution);
        Assert.Empty(probe.IdLookups);
    }

    [Fact]
    public void A_type_mismatch_and_an_invalid_locator_are_structural_conflicts()
    {
        var data = Items.Data(Items.SourceFolder(@"E:\thing", id: "f"), Items.SourceFile(@"relative\a.mp3", id: "r"), Items.File(@"E:\locked.mp3", id: "l"));
        var probe = ProbeWithMusic().File(@"E:\thing").Denied(@"E:\locked.mp3");

        var (model, _) = Run(data, probe);

        Assert.Equal((Presence.TypeMismatch, Resolution.StructuralConflict), (model.FindItem("f")!.Presence, model.FindItem("f")!.Resolution));
        Assert.Equal(Resolution.StructuralConflict, model.FindItem("r")!.Resolution);
        Assert.Null(model.FindItem("r")!.ResolvedPath);
        Assert.DoesNotContain(probe.Stats, s => s.Contains("relative"));
        Assert.Equal(Presence.Inaccessible, model.FindItem("l")!.Presence);
        Assert.Equal("presence.inaccessible", Assert.Single(model.FindItem("l")!.Self).Code);
    }

    [Fact]
    public void A_member_outside_every_source_is_present_but_not_indexed()
    {
        var data = Items.Data(Items.File(@"E:\outside.mp3", id: "o", fileId: "f", serial: "VOL1"), Items.Group("G", id: "g"), Items.Member("g", "o"));
        var probe = ProbeWithMusic().File(@"E:\outside.mp3", fileId: "f");

        var (model, _) = Run(data, probe);

        var item = model.FindItem("o")!;
        Assert.Equal(Presence.Present, item.Presence);
        Assert.False(item.Indexed);
        Assert.Null(item.Tree);
        Assert.Equal("outside.mp3", item.Name);
    }

    [Fact]
    public void Each_path_is_probed_once_per_reconciliation()
    {
        var data = Items.Data(Items.SourceFile(@"E:\x\a.mp3", id: "one"), Items.File(@"e:\X\A.MP3", id: "two"));
        var probe = ProbeWithMusic().File(@"E:\x\a.mp3");

        var (model, _) = Run(data, probe);

        Assert.Equal(1, probe.Stats.Count(s => s.Equals(@"E:\x\a.mp3", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(Presence.Present, model.FindItem("two")!.Presence);
        Assert.Same(model.FindItem("one"), model.ItemAt(@"E:\X\a.mp3"));
    }

    // ───── Groups ─────

    [Fact]
    public void Groups_nest_by_parent_and_memberships_stay_where_they_are()
    {
        var data = Items.Data(
            Items.Group("Favoritos", id: "fav"), Items.Group("Música", id: "mus", parent: "fav"), Items.Group("Vídeos", id: "vid", parent: "fav"), Items.Group("Gym", id: "gym"),
            Items.Folder(@"D:\Music\Sets", id: "sets"), Items.File(@"D:\Music\foo.mp3", id: "foo"),
            Items.Member("mus", "sets"), Items.Member("gym", "foo"), Items.Member("mus", "foo"), Items.Member("mus", "foo"));
        var probe = ProbeWithMusic().Folder(@"D:\Music\Sets").File(@"D:\Music\foo.mp3");

        var (model, _) = Run(data, probe);

        Assert.Equal(["fav", "gym"], model.Groups.Select(g => g.Id));
        var fav = model.FindGroup("fav")!;
        Assert.Equal(["mus", "vid"], fav.Children.Select(c => c.Id));
        Assert.Empty(fav.Members);   // nothing implied from the child
        Assert.Same(fav, model.FindGroup("mus")!.Parent);
        Assert.Equal(["sets", "foo"], model.FindGroup("mus")!.Members.Select(m => m.Id));   // the duplicate pair counted once
        Assert.Equal(["gym", "mus"], model.FindItem("foo")!.Groups.Select(g => g.Id));   // in membership order
        Assert.All(model.AllGroups.Values, g => Assert.True(g.Attached));
        Assert.Equal(["fav", "mus", "vid"], fav.SelfAndDescendants().Select(g => g.Id));
        Assert.Empty(model.Problems);
    }

    [Fact]
    public void A_group_whose_parent_is_gone_or_cyclic_is_shown_at_the_top_with_the_reason()
    {
        var data = Items.Data(
            Items.Group("Orphan", id: "o", parent: "nope"),
            Items.Group("A", id: "a", parent: "b"), Items.Group("B", id: "b", parent: "a"),
            Items.Group("C", id: "c", parent: "a"),
            Items.Group("Ok", id: "ok"), Items.Group("Inside", id: "in", parent: "ok"));

        var (model, _) = Run(data, ProbeWithMusic());

        Assert.Equal(["o", "a", "b", "c", "ok"], model.Groups.Select(g => g.Id));
        Assert.Equal("structure.missingParent", Assert.Single(model.FindGroup("o")!.Self).Code);
        Assert.Equal("structure.parentCycle", Assert.Single(model.FindGroup("a")!.Self).Code);
        Assert.Equal("structure.parentCycle", Assert.Single(model.FindGroup("b")!.Self).Code);
        Assert.NotNull(model.FindGroup("c")!.DetachedReason);   // its chain runs into the cycle
        Assert.False(model.FindGroup("a")!.Attached);
        Assert.True(model.FindGroup("in")!.Attached);
        Assert.Equal(4, model.Problems.Count(p => p.Code == "structure"));
    }

    [Fact]
    public void A_membership_pointing_nowhere_is_kept_and_reported()
    {
        var data = Items.Data(Items.Group("G", id: "g"), Items.File(@"E:\a.mp3", id: "a"), Items.Member("g", "ghost"), Items.Member("ghostGroup", "a"), Items.Member("g", "a"));

        var (model, _) = Run(data, ProbeWithMusic().File(@"E:\a.mp3"));

        Assert.Equal(["a"], model.FindGroup("g")!.Members.Select(m => m.Id));
        Assert.Equal(2, model.Problems.Count(p => p.Code == "membership.dangling"));
    }

    [Fact]
    public void Ids_in_conflict_across_items_and_groups_resolve_nothing()
    {
        var data = Items.Data(Items.SourceFolder(@"D:\Music", id: "dup"), Items.Group("Dup", id: "dup"), Items.Group("Child", id: "child", parent: "dup"), Items.Member("dup", "dup"));

        var (model, _) = Run(data, ProbeWithMusic().Folder(@"D:\Music"));

        Assert.Null(model.FindItem("dup"));
        Assert.Null(model.FindGroup("dup"));
        Assert.Empty(model.Sources);
        Assert.Equal("structure.parentConflicted", Assert.Single(model.FindGroup("child")!.Self).Code);
        Assert.Contains(model.Problems, p => p.Code == "conflictedId");
        Assert.Contains(model.Problems, p => p.Code == "membership.dangling");
    }

    // ───── Status and actions ─────

    [Fact]
    public void A_fresh_tree_or_other_rules_ask_for_a_full_scan_and_a_pending_or_failed_manifest_for_a_scan()
    {
        var data = Items.Data(Items.SourceFolder(@"D:\Music", id: "d"));
        var probe = ProbeWithMusic().Folder(@"D:\Music");

        static (ScanReason, bool) ScanOf(ReconcileResult result)
        {
            var scan = Assert.Single(result.Actions.OfType<RequestScan>());
            return (scan.Reason, scan.Full);
        }

        Assert.Equal((ScanReason.ScanPlanChanged, true), ScanOf(Run(data, probe, new IndexData())));
        var withGif = Items.Data(Items.SourceFolder(@"D:\Music", id: "d"));
        withGif.ExtraExtensions = ["gif"];
        var stale = new TestIndex(ScanPlan.Build(Items.Snapshot(withGif), Music)).Build();
        Assert.Equal((ScanReason.ScanPlanChanged, true), ScanOf(Run(data, probe, stale)));
        Assert.True(Run(data, probe, stale).Model.Status.ScanPlanChanged);
        Assert.Equal((ScanReason.Pending, false), ScanOf(Run(data, probe, status: SyncStatus.Pending)));
        Assert.Equal((ScanReason.Pending, true), ScanOf(Run(data, probe, status: SyncStatus.Failed)));
        Assert.Empty(Run(data, probe).Actions);
    }

    [Fact]
    public void A_read_only_manifest_gets_a_model_and_no_actions()
    {
        var data = Items.Data(Items.SourceFile(@"E:\gone\a.mp3", id: "c", fileId: "f1", serial: "VOL1"));
        var probe = ProbeWithMusic().Root(@"E:\gone\a.mp3").Volume("VOL1");

        var (model, actions) = Run(data, probe, readOnly: true, status: SyncStatus.Failed);

        Assert.True(model.Status.ReadOnly);
        Assert.Equal(Resolution.Broken, model.FindItem("c")!.Resolution);
        Assert.Empty(actions);
    }

    [Fact]
    public void Unlistable_folders_come_back_as_problems()
    {
        var data = Items.Data(Items.SourceFolder(@"D:\Music", id: "d"));
        var index = new TestIndex(ScanPlan.Build(Items.Snapshot(data), Music)).Inaccessible(@"D:\Music\locked").Build();

        var (model, _) = Run(data, ProbeWithMusic().Folder(@"D:\Music"), index);

        var locked = Assert.Single(model.Problems, p => p.Code == "inaccessibleDirectory");
        Assert.Equal((Severity.Warning, @"D:\Music\locked"), (locked.Severity, locked.Path));
    }
}
