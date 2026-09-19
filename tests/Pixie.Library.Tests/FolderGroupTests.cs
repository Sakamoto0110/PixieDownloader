using System.Windows.Data;

namespace Pixie.Library.Tests;

/// <summary>The folder headers of the grouped list: what they show, how they order, and what a search does to them.</summary>
public sealed class FolderGroupTests
{
    [Fact]
    public void A_sub_folder_shows_its_root_as_a_prefix_and_the_root_s_own_files_show_the_root()
    {
        var sub = new FolderGroup(@"C:\Music\Playlists\2020", "Music", @"Playlists\2020");
        var root = new FolderGroup(@"C:\Music", "Music", "");

        Assert.Equal(@"Playlists\2020", sub.Title);
        Assert.Equal("Music › ", sub.Prefix);
        Assert.Equal("Music", root.Title);
        Assert.Equal("", root.Prefix);
    }

    [Fact]
    public void Folders_start_collapsed_and_a_search_opens_them_all_until_it_is_cleared()
    {
        var closed = new FolderGroup(@"C:\Music\A", "Music", "A");
        var open = new FolderGroup(@"C:\Music\B", "Music", "B") { IsExpanded = true };
        Assert.False(closed.IsExpanded);

        closed.SetSearching(true);
        open.SetSearching(true);
        Assert.True(closed.IsExpanded);
        Assert.True(open.IsExpanded);

        open.IsExpanded = false;   // collapsed by hand while searching: the search's business only

        closed.SetSearching(false);
        open.SetSearching(false);
        Assert.False(closed.IsExpanded);
        Assert.True(open.IsExpanded);   // back to what it was before the search
    }

    [Fact]
    public void Headers_order_by_root_then_by_folder_whatever_the_rows_sort_says()
    {
        var groups = new[]
        {
            new FolderGroup(@"D:\Downloads\z", "Downloads", "z"),
            new FolderGroup(@"C:\Music\Playlists", "Music", "Playlists"),
            new FolderGroup(@"C:\Music", "Music", ""),
            new FolderGroup(@"C:\Music\mixes", "Music", "mixes"),
        };
        var view = new ListCollectionView(groups.Select(g => new Row(g)).ToList()) { CustomSort = Comparer<object>.Create((_, _) => 0) };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Group)) { CustomSort = FolderGroup.Comparer });

        var order = view.Groups!.Cast<CollectionViewGroup>().Select(g => ((FolderGroup)g.Name).Prefix + ((FolderGroup)g.Name).Title).ToList();

        Assert.Equal(["Downloads › z", "Music", "Music › mixes", "Music › Playlists"], order);
    }

    private sealed record Row(FolderGroup Group);
}
