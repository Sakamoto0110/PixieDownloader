using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>The scan plan: the storage root plus the sources, as the covering set; what cannot be observed is left out.</summary>
public sealed class ScanPlanTests
{
    private const string Music = @"C:\Users\someone\Music";

    private static ScanPlan Build(ManifestData data) => ScanPlan.Build(Items.Snapshot(data), Music);

    private static ScanPlan Build(params object[] parts) => Build(Items.Data(parts));

    [Fact]
    public void The_storage_root_is_the_music_folder_unless_configured_and_is_always_a_source()
    {
        Assert.Equal(Music, ScanPlan.StorageRootOf(null, Music));
        Assert.Equal(Music, ScanPlan.StorageRootOf("  ", Music));
        Assert.Equal(@"E:\Biblioteca", ScanPlan.StorageRootOf(@"E:\Biblioteca\", Music));

        var plan = Build();
        Assert.Equal(Music, plan.StorageRoot);
        var source = Assert.Single(plan.FolderSources);
        Assert.Equal((Music, (string?)null), (source.Path, source.ItemId));   // no item stands for the storage root
        Assert.Empty(plan.FileSources);
        Assert.True(plan.IsInStorageRoot(Music + @"\Sets\a.mp3"));
        Assert.False(plan.IsInStorageRoot(@"C:\Users\someone\Music2\a.mp3"));

        var configured = Items.Data();
        configured.StorageRoot = @"E:\Biblioteca";
        Assert.Equal([@"E:\Biblioteca"], Build(configured).FolderSources.Select(s => s.Path));
    }

    [Fact]
    public void A_folder_inside_another_folder_source_is_not_a_source_of_its_own()
    {
        var plan = Build(Items.SourceFolder(@"D:\Music\Sets", id: "inner"), Items.SourceFolder(@"D:\Music", id: "outer"), Items.SourceFolder(@"d:\music\", id: "twin"),
            Items.SourceFolder(Music + @"\Sets", id: "inStorage"));

        Assert.Equal([null, "outer"], plan.FolderSources.Select(s => s.ItemId));   // the storage root walks its own subtree; D:\Music the first on its path
        Assert.Equal(@"D:\Music", plan.FolderSources[1].Path);
        Assert.Same(plan.FolderSources[1], plan.SourceOf(@"D:\Music\Sets\x.mp3"));
        Assert.Same(plan.FolderSources[0], plan.SourceOf(Music + @"\Sets\x.mp3"));
        Assert.Null(plan.SourceOf(@"E:\elsewhere.mp3"));
    }

    [Fact]
    public void A_file_under_a_folder_source_is_walked_by_it_and_one_outside_is_stat_ed()
    {
        var plan = Build(Items.SourceFolder(@"D:\Music", id: "folder"), Items.SourceFile(@"D:\Music\Sets\a.mp3", id: "inside"), Items.SourceFile(@"E:\solo.mp3", id: "outside"),
            Items.SourceFile(@"e:\SOLO.mp3", id: "twin"), Items.SourceFile(Music + @"\own.mp3", id: "inStorage"));

        Assert.Equal([null, "folder"], plan.FolderSources.Select(s => s.ItemId));
        Assert.Equal(["outside"], plan.FileSources.Select(s => s.ItemId));
        Assert.Same(plan.FileSources[0], plan.SourceOf(@"E:\Solo.mp3"));
    }

    [Fact]
    public void Items_that_are_not_sources_or_cannot_be_observed_are_left_out()
    {
        var data = Items.Data(
            Items.SourceFolder(@"D:\Music", id: "dup"),
            Items.SourceFolder(@"D:\Other", id: "dup"),          // conflicted id: neither
            Items.SourceFolder(@"Music\relative", id: "rel"),    // invalid locator
            Items.File(@"D:\member.mp3", id: "member"),         // a group member is not walked on its own
            Items.Folder(@"D:\memberFolder", id: "mf"),
            Items.Group("Favoritos", id: "g"),
            Items.SourceFolder(@"D:\Ok", id: "ok"));
        data.Items.Add(new ItemData { Id = "odd", Kind = "playlist", Locator = new LocatorData { LastKnownPath = @"D:\odd" }, Source = new SourceData { Name = "odd" } });

        var plan = Build(data);

        Assert.Equal([null, "ok"], plan.Sources.Select(s => s.ItemId));
    }

    [Fact]
    public void Extensions_come_from_the_manifest()
    {
        var data = Items.Data();
        data.ExtraExtensions.Add("gif");

        var plan = Build(data);

        Assert.Contains(".gif", plan.Extensions);
        Assert.Contains(".mp3", plan.Extensions);
        Assert.DoesNotContain(".gif", Build().Extensions);
    }
}
