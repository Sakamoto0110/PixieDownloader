using Pixie.Library.Catalog;

namespace Pixie.Library.Tests;

/// <summary>
/// The rows of the single list from a model: "Tudo" over the storage root and the sources, the groups with their
/// children and members, a member folder opening on the index subtree, the same file in several places, the
/// filters, the search that opens everything, the flat list.
/// </summary>
public sealed class ProjectionTests
{
    private const string Music = @"C:\Users\someone\Music";

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();
    private static IReadOnlySet<string> Open(params string[] keys) => new HashSet<string>(keys.Prepend(LibraryProjection.TopKey), StringComparer.Ordinal);

    private static ProjectionOptions Options(IReadOnlySet<string>? expanded = null, string? root = null, FileKind? kind = null, string search = "", bool grouped = true, IReadOnlyList<FileNode>? early = null)
        => new(root, kind, search, SortOption.All[1].Comparer /* Título */, grouped, expanded ?? Open(), early ?? []);

    /// <summary>A library: Music with Sets\a, Sets\b, standalone; D:\Sets as a source with x; groups Favoritos › Música (Sets, standalone) and Gym (a, D:\Sets\x).</summary>
    private static (LibraryModel Model, IReadOnlyList<SourceRef> Sources) Library(Action<ManifestData>? tweak = null, Action<TestIndex>? more = null)
    {
        var data = Items.Data(
            Items.SourceFolder(@"D:\Sets", "Externa", id: "ext"),
            Items.Folder(Music + @"\Sets", id: "sets"), Items.File(Music + @"\standalone.mp3", id: "standalone"), Items.File(Music + @"\Sets\a.mp3", id: "a"), Items.File(@"D:\Sets\x.mp3", id: "x"),
            Items.Group("Favoritos", id: "fav"), Items.Group("Música", id: "mus", parent: "fav"), Items.Group("Gym", id: "gym"),
            Items.Member("mus", "sets"), Items.Member("mus", "standalone"), Items.Member("gym", "a"), Items.Member("gym", "x"));
        tweak?.Invoke(data);
        var snapshot = Items.Snapshot(data);
        var plan = ScanPlan.Build(snapshot, Music);
        var index = new TestIndex(plan)
            .File(Music + @"\Sets\a.mp3", title: "Alpha").File(Music + @"\Sets\b.mp3", title: "Beta").File(Music + @"\standalone.mp3", title: "Solo").Folder(Music + @"\Empty")
            .File(@"D:\Sets\x.mp3", title: "Xis");
        more?.Invoke(index);
        var probe = new FakeProbe().Folder(Music).Folder(Music + @"\Sets").Folder(@"D:\Sets").File(Music + @"\standalone.mp3").File(Music + @"\Sets\a.mp3").File(@"D:\Sets\x.mp3");
        var model = Reconciler.Build(new ReconcileInput(snapshot, index.Build(), plan), probe).Model;
        var sources = new List<SourceRef> { new(null, "Music", Music), new("ext", "Externa", @"D:\Sets") };
        return (model, sources);
    }

    private static string[] Lines(IEnumerable<LibraryRow> rows) => rows.Select(r => new string(' ', r.Depth * 2) + (r.IsContainer ? (r.IsExpanded ? "v " : "> ") : "") + r.Title).ToArray();

    private static List<LibraryRow> Build(LibraryModel model, IReadOnlyList<SourceRef> sources, ProjectionOptions options) => LibraryProjection.Build(model, sources, options).Rows;

    [Fact]
    public void Tudo_lists_the_storage_root_and_the_sources_then_the_top_level_groups_all_closed_at_first()
    {
        var (model, sources) = Library();

        var rows = Build(model, sources, Options());

        Assert.Equal(["v Tudo", "  > Empty", "  > Sets", "  > Externa", "  Solo", "> Favoritos", "> Gym"], Lines(rows));
        var tudo = rows[0];
        Assert.True(tudo.IsTop && tudo.IsFolder);
        Assert.Equal(4, tudo.Count);   // a, b, standalone, x
        Assert.Equal(2, rows.Single(r => r.Title == "Sets").Count);
        Assert.Equal(0, rows.Single(r => r.Title == "Empty").Count);   // a folder just created shows up empty
        Assert.True(rows.Single(r => r.Title == "Externa").IsSourceRoot);
        Assert.Equal(3, rows.Single(r => r.Title == "Favoritos").Count);   // Música: Sets (a, b) + standalone
        Assert.Equal(2, rows.Single(r => r.Title == "Gym").Count);
        Assert.All(rows.Where(r => r.IsGroup), r => Assert.False(r.IsExpanded));
        Assert.Equal("standalone", rows.Single(r => r.Title == "Solo").ItemId);
    }

    [Fact]
    public void An_open_group_shows_its_child_groups_and_members_and_a_member_folder_the_index_subtree()
    {
        var (model, sources) = Library();

        var rows = Build(model, sources, Options(Open("g:fav", "g:fav|g:mus", "g:fav|g:mus|" + PathUtil.Canonical(Music + @"\Sets"), "g:gym")));

        Assert.Equal([
            "v Tudo", "  > Empty", "  > Sets", "  > Externa", "  Solo",
            "v Favoritos", "  v Música", "    v Sets", "      Alpha", "      Beta", "    Solo",
            "v Gym", "  Alpha", "  Xis",
        ], Lines(rows));
        var setsInGroup = rows.Single(r => r.Title == "Sets" && r.Depth == 2);
        Assert.Equal("mus", setsInGroup.InGroup!.Id);
        Assert.Equal("sets", setsInGroup.ItemId);
        var betaInGroup = rows.Single(r => r.Title == "Beta");
        Assert.Equal("mus", betaInGroup.InGroup!.Id);   // inside the member folder: the same subtree of the index, no membership of its own
        Assert.Null(betaInGroup.ItemId);
        var alphas = rows.Where(r => r.Title == "Alpha").ToList();
        Assert.Equal(2, alphas.Count);   // once under Música › Sets, once in Gym: two rows, two keys, one file
        Assert.NotEqual(alphas[0].Key, alphas[1].Key);
        Assert.Same(alphas[0].File, alphas[1].File);
        Assert.Equal("gym", alphas[1].InGroup!.Id);
        Assert.Equal("a", alphas[1].ItemId);
    }

    [Fact]
    public void The_physical_tree_opens_folder_by_folder_with_the_same_keys_across_projections()
    {
        var (model, sources) = Library();
        var setsKey = LibraryProjection.TopKey + "|" + PathUtil.Canonical(Music + @"\Sets");

        var rows = Build(model, sources, Options(Open(setsKey)));

        Assert.Equal(["v Tudo", "  > Empty", "  v Sets", "    Alpha", "    Beta", "  > Externa", "  Solo", "> Favoritos", "> Gym"], Lines(rows));
        Assert.Equal(setsKey, rows.Single(r => r.Title == "Sets").Key);
        Assert.Equal(["Alpha", "Beta"], rows.Where(r => r.Depth == 2).Select(r => r.Title));   // sorted by title inside the container

        var closed = Build(model, sources, Options(Nothing));
        Assert.Equal(["> Tudo", "> Favoritos", "> Gym"], Lines(closed));
    }

    [Fact]
    public void A_search_opens_everything_and_keeps_only_containers_with_a_hit()
    {
        var (model, sources) = Library();

        var rows = Build(model, sources, Options(Nothing, search: "alpha"));

        Assert.Equal(["v Tudo", "  v Sets", "    Alpha", "v Favoritos", "  v Música", "    v Sets", "      Alpha", "v Gym", "  Alpha"], Lines(rows));
        Assert.Equal(1, rows[0].Count);

        var byFolder = Build(model, sources, Options(Nothing, search: "sets"));   // the folder name is part of a file's key
        Assert.Contains("      Alpha", Lines(byFolder));
        Assert.Contains("      Beta", Lines(byFolder));
        Assert.DoesNotContain("  Solo", Lines(byFolder));
    }

    [Fact]
    public void The_chip_and_the_type_filter_files_and_containers_follow()
    {
        var (model, sources) = Library();

        var external = Build(model, sources, Options(Open("g:gym"), root: @"D:\Sets"));
        Assert.Equal(["v Tudo", "  > Externa", "> Favoritos", "v Gym", "  Xis"], Lines(external));   // nothing under Music; Favoritos stays, empty: the chip filters files, not groups

        var video = Build(model, sources, Options(Open("g:gym"), kind: FileKind.Video));
        Assert.Empty(video);   // no container at all: not even Tudo has a video
    }

    [Fact]
    public void Pastas_off_is_one_flat_list_of_files_in_the_sort_order()
    {
        var (model, sources) = Library();

        var rows = Build(model, sources, Options(Nothing, grouped: false));

        Assert.Equal(["Alpha", "Beta", "Solo", "Xis"], Lines(rows));
        Assert.All(rows, r => Assert.True(r.IsFile && r.Depth == 0));
        Assert.Equal("Externa", rows.Single(r => r.Title == "Xis").RootName);
    }

    [Fact]
    public void A_member_that_went_missing_is_listed_dimmed_with_the_reason_and_one_outside_every_source_as_a_file()
    {
        var (model, sources) = Library(data =>
        {
            data.Items.Add(Items.File(@"E:\gone.mp3", id: "gone", fileId: "f9", serial: "VOL1"));
            data.Items.Add(Items.File(Music + @"\loose.mp3", id: "loose"));   // not in the tree (a scan has not seen it yet), but on disk
            data.Memberships.Add(Items.Member("gym", "gone"));
            data.Memberships.Add(Items.Member("gym", "loose"));
        });

        var rows = Build(model, sources, Options(Open("g:gym")));

        var gone = rows.Single(r => r.Title == "gone.mp3");
        Assert.True(gone.IsMissing && gone.IsDimmed && gone.CanRelocate);
        Assert.Equal("gym", gone.InGroup!.Id);
        Assert.Contains("não", gone.Subtitle);
        Assert.DoesNotContain(rows, r => r.Title == "loose");   // the probe knows nothing of it either: missing, not "outside"
        Assert.Equal(2, rows.Single(r => r.IsGroup && r.Title == "Gym").Count);   // the missing ones are not files shown

        var searched = Build(model, sources, Options(Nothing, search: "gone"));
        Assert.DoesNotContain(searched, r => r.IsMissing);   // a filter lists what it can find
    }

    [Fact]
    public void Files_a_scan_just_read_show_under_Tudo_until_the_model_has_them()
    {
        var (model, sources) = Library();
        var fresh = new SourceTree { RootPath = Music, Root = new FolderNode { Name = "Music", Files = [new FileNode { Name = "fresh.mp3", Title = "Fresh", Size = 3 }] } };
        fresh.Attach();

        var rows = Build(model, sources, Options(Open(), early: [fresh.Root!.Files[0]]));

        Assert.Contains("  Fresh", Lines(rows));
        Assert.Equal(5, rows[0].Count);
        var known = new SourceTree { RootPath = Music, Root = new FolderNode { Name = "Music", Files = [new FileNode { Name = "standalone.mp3", Title = "Solo again" }] } };
        known.Attach();
        var again = Build(model, sources, Options(Open(), early: [known.Root!.Files[0]]));
        Assert.Equal(4, again[0].Count);   // already in the tree: not twice
    }
}
