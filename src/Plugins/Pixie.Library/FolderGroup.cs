using System.Collections;
using System.Windows.Data;
using YtDlpCore;

namespace Pixie.Library;

/// <summary>
/// One folder of the list when the rows are grouped by folder: every row of a directory points at the same
/// instance, so the view's group description finds the group by reference and the expanded flag lives here —
/// not in a group container the list box virtualises away and recreates. Folders start collapsed: the list of
/// folders is the overview, a click opens one. While a search is on every folder shows its hits, whatever the
/// flag says — a collapsed folder hiding matches would make the search lie — and the flags come back as they
/// were when the search is cleared.
/// </summary>
public sealed class FolderGroup : ObservableObject
{
    private bool _isExpanded;
    private bool _wasExpanded;
    private bool _searching;

    internal FolderGroup(string directory, string rootName, string folder)
    {
        Directory = directory;
        RootName = rootName;
        Folder = folder;
        Title = folder.Length > 0 ? folder : rootName;
        Prefix = folder.Length > 0 ? rootName + " › " : "";
    }

    /// <summary>The real directory, normalized — the identity.</summary>
    public string Directory { get; }
    public string RootName { get; }

    /// <summary>Relative to the root; empty for files sitting in the root itself.</summary>
    public string Folder { get; }

    /// <summary>What the header shows: the relative folder, or the root's name for its own files.</summary>
    public string Title { get; }

    /// <summary>"Music › " in front of a sub-folder, so "Todas" tells the roots apart; nothing for the root itself.</summary>
    public string Prefix { get; }

    /// <summary>Two-way with the header. During a search this is the search's override, the user's own flag is kept aside.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
                return;
            if (!_searching)
                _wasExpanded = value;
        }
    }

    /// <summary>A search opens every folder and a cleared one restores what the user had.</summary>
    internal void SetSearching(bool searching)
    {
        if (searching == _searching)
            return;
        _searching = searching;
        if (searching)
        {
            _wasExpanded = _isExpanded;
            SetProperty(ref _isExpanded, true, nameof(IsExpanded));
        }
        else
        {
            SetProperty(ref _isExpanded, _wasExpanded, nameof(IsExpanded));
        }
    }

    /// <summary>
    /// Folder order for the headers: root by root (by name), then the relative path — the tree as the disk has
    /// it, not wherever the newest file happens to be. The view hands this the groups themselves.
    /// </summary>
    public static IComparer Comparer { get; } = new GroupComparer();

    private sealed class GroupComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (x as CollectionViewGroup)?.Name as FolderGroup;
            var b = (y as CollectionViewGroup)?.Name as FolderGroup;
            if (a is null || b is null)
                return a is null ? (b is null ? 0 : -1) : 1;
            var c = string.Compare(a.RootName, b.RootName, StringComparison.CurrentCultureIgnoreCase);
            return c != 0 ? c : string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
