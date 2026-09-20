using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Pixie.Library.Catalog;

namespace Pixie.Library;

/// <summary>A folder the tab lists as a root: the storage root (no item of its own) or a folder source.</summary>
public sealed record SourceRef(string? ItemId, string Name, string Path)
{
    public bool IsStorageRoot => ItemId is null;
}

public enum RowKind
{
    /// <summary>A file of the tree (or a file item the tree does not reach but the disk has).</summary>
    File,

    /// <summary>A folder of the tree: a container that opens on its children.</summary>
    Folder,

    /// <summary>A group: a container that opens on its child groups and its members.</summary>
    Group,

    /// <summary>A member the disk does not have where the locator says (or that cannot be observed): listed, dimmed, with Localizar…</summary>
    Missing,
}

/// <summary>
/// One line of the single list: a file, a folder, a group or a member that went missing — every container the
/// same collapsible line, with its depth. Immutable: the projection builds the rows from the model and the
/// expanded flags, and a changed model or a click on a chevron builds them again; <see cref="ApplyRowsKey"/>
/// is what lets the list keep its scroll and selection across that. The same file under several containers
/// is several rows: projections of one entity, told apart by their key.
/// </summary>
public sealed class LibraryRow
{
    private LibraryRow(string key, RowKind kind, int depth)
    {
        Key = key;
        Kind = kind;
        Depth = depth;
        Indent = new Thickness(depth * 18, 0, 0, 0);
    }

    /// <summary>A file of the tree, under a folder container or the top; <paramref name="inGroup"/> when projected inside a group.</summary>
    internal static LibraryRow ForFile(string key, int depth, FileNode file, SourceRef? source, GroupView? inGroup, ItemView? item)
    {
        var row = new LibraryRow(key, RowKind.File, depth) { File = file, Item = item, InGroup = inGroup };
        row.FillFile(file.FullPath, source, file);
        return row;
    }

    /// <summary>A file item the tree does not list (outside every source) but the disk has: opened by its path, without tags.</summary>
    internal static LibraryRow ForOutsideFile(string key, int depth, ItemView item, GroupView? inGroup)
    {
        var row = new LibraryRow(key, RowKind.File, depth) { Item = item, InGroup = inGroup, IsOutside = true };
        row.FillFile(item.ResolvedPath ?? item.Data.Locator.LastKnownPath, null, null);
        row.Subtitle = "fora das pastas da biblioteca";
        row.ToolTip += "\nfora das pastas da biblioteca: aparece porque está num grupo";
        return row;
    }

    internal static LibraryRow ForFolder(string key, int depth, FolderNode folder, SourceRef? source, GroupView? inGroup, ItemView? item, bool expanded, int count, bool isSourceRoot)
    {
        var path = folder.FullPath;
        var row = new LibraryRow(key, RowKind.Folder, depth)
        {
            Folder = folder,
            Item = item,
            InGroup = inGroup,
            IsExpanded = expanded,
            Count = count,
            Path = path,
            Title = isSourceRoot && source is not null ? source.Name : folder.Name,
            IsSourceRoot = isSourceRoot,
            RootPath = source?.Path ?? "",
            RootName = source?.Name ?? "",
            Presence = item?.Presence ?? Presence.Unknown,
        };
        row.Subtitle = folder.IsInaccessible ? "sem acesso — o que se sabia foi mantido" : folder.Source.IsUnavailable ? "não disponível — disco desligado?" : "";
        row.SearchKey = SearchText.Normalize(row.Title);
        row.ToolTip = path + (row.Subtitle.Length > 0 ? "\n" + row.Subtitle : "");
        row.Signature = string.Join("|", key, row.Title, row.Subtitle, expanded, count, depth);
        return row;
    }

    /// <summary>"Tudo": the synthetic top container over the storage root's tree and the other sources.</summary>
    internal static LibraryRow ForTop(string key, string storageRoot, bool expanded, int count)
    {
        var row = new LibraryRow(key, RowKind.Folder, 0) { IsTop = true, IsExpanded = expanded, Count = count, Path = storageRoot, Title = "Tudo" };
        row.SearchKey = "tudo";
        row.ToolTip = "Tudo que está na biblioteca: a pasta da biblioteca (" + storageRoot + ") e as pastas adicionadas";
        row.Signature = string.Join("|", key, expanded, count);
        return row;
    }

    internal static LibraryRow ForGroup(string key, int depth, GroupView group, bool expanded, int count)
    {
        var row = new LibraryRow(key, RowKind.Group, depth)
        {
            Group = group,
            IsExpanded = expanded,
            Count = count,
            Title = group.Name,
            Subtitle = group.DetachedReason ?? "",
        };
        row.SearchKey = SearchText.Normalize(group.Name);
        row.ToolTip = "Grupo" + (group.Parent is { } parent ? $" em {parent.Name}" : "") + (row.Subtitle.Length > 0 ? "\n" + row.Subtitle : "");
        row.Signature = string.Join("|", key, group.Name, row.Subtitle, expanded, count, depth);
        return row;
    }

    /// <summary>A member the disk does not have where it was, or on a volume that is off: named, dimmed, with what the reconciler said.</summary>
    internal static LibraryRow ForMissing(string key, int depth, ItemView item, GroupView? inGroup)
    {
        var path = item.ResolvedPath ?? item.Data.Locator.LastKnownPath;
        var row = new LibraryRow(key, RowKind.Missing, depth)
        {
            Item = item,
            InGroup = inGroup,
            Path = path,
            Title = System.IO.Path.GetFileName(path),
            FileName = System.IO.Path.GetFileName(path),
            Extension = item.IsFile ? System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant() : "",
            Presence = item.Presence,
            CanRelocate = true,
        };
        var diagnostics = string.Join("; ", item.Self.Select(d => d.Message));
        row.Subtitle = diagnostics.Length > 0 ? diagnostics : "não encontrado";
        row.SearchKey = SearchText.Normalize(row.Title);
        row.ToolTip = path + "\n" + row.Subtitle;
        row.Signature = string.Join("|", key, row.Title, row.Subtitle, depth);
        return row;
    }

    private void FillFile(string path, SourceRef? source, FileNode? file)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        FileKind = FileKinds.KindOf(path);
        Title = file?.Title ?? System.IO.Path.GetFileNameWithoutExtension(path);
        RootPath = source?.Path ?? "";
        RootName = source?.Name ?? "";
        Directory = PathUtil.Normalize(System.IO.Path.GetDirectoryName(path) ?? path);
        var folder = source is null ? Directory : PathUtil.Relative(Directory, source.Path);
        var byline = string.Join(" · ", new[] { file?.Artist, file?.Album }.Where(s => !string.IsNullOrEmpty(s)));
        Subtitle = byline.Length > 0 ? byline : folder.Length > 0 ? folder : RootName;
        Duration = file?.Duration ?? 0;
        DurationText = file?.Duration is { } d ? Format.Duration(d) : "";
        Size = file?.Size ?? Item?.Data.Locator.Size ?? 0;
        SizeText = Size > 0 ? Format.Size(Size) : "";
        Added = file?.Created ?? DateTime.MinValue;
        AddedText = Added == DateTime.MinValue ? "" : Added.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        Chapters = file?.Chapters ?? 0;
        ChaptersText = Chapters > 0 ? $"{Chapters} faixas" : "";
        Presence = Item?.Presence ?? Presence.Unknown;
        SearchKey = SearchText.Normalize(string.Join(" ", new[] { Title, file?.Artist, file?.Album, FileName, folder, RootName }.Where(s => !string.IsNullOrEmpty(s))));
        var tip = new StringBuilder(path);
        if (file?.Bitrate is { } b) tip.Append($"\n{b} kbps");
        if (Chapters > 0) tip.Append($"\n{Chapters} capítulos ID3");
        if (file is not null) tip.Append($"\nadicionado em {file.Created.ToLocalTime():g}");
        if (Item is not null && Item.Groups.Count > 0) tip.Append("\nem: ").Append(string.Join(", ", Item.Groups.Select(g => g.Name)));
        ToolTip = tip.ToString();
        Signature = string.Join("|", Key, file?.Modified.Ticks, file?.Size, file?.FileId, Title, Subtitle, RootName, Depth, Item?.Groups.Count, Presence);
    }

    // ───── identity ─────

    /// <summary>The projection key: the chain of containers plus the node — the same file in two places is two keys.</summary>
    internal string Key { get; }

    /// <summary>Everything the row shows, in one string: a different signature at the same key is a changed row.</summary>
    internal string Signature { get; private set; } = "";

    public RowKind Kind { get; }
    public bool IsFile => Kind == RowKind.File;
    public bool IsFolder => Kind == RowKind.Folder;
    public bool IsGroup => Kind == RowKind.Group;
    public bool IsMissing => Kind == RowKind.Missing;
    public bool IsContainer => Kind is RowKind.Folder or RowKind.Group;

    /// <summary>A file or folder: something with a place on disk that an operation can take.</summary>
    public bool IsPhysical => Kind is RowKind.File or RowKind.Folder;

    public int Depth { get; }
    public Thickness Indent { get; }

    internal FileNode? File { get; private init; }
    internal FolderNode? Folder { get; private init; }
    internal GroupView? Group { get; private init; }
    internal ItemView? Item { get; private init; }

    /// <summary>The group this row is projected inside (directly or under a member folder); null under "Tudo".</summary>
    internal GroupView? InGroup { get; private init; }

    /// <summary>A folder row that is a source's root (the storage root's children are listed under "Tudo" directly): its name is the source's.</summary>
    public bool IsSourceRoot { get; private init; }

    /// <summary>A file item outside every source: no tags, no tree, but a path to open.</summary>
    public bool IsOutside { get; private init; }

    /// <summary>The "Tudo" container: not a folder of the disk, though it opens on the storage root.</summary>
    public bool IsTop { get; private init; }

    // ───── what the line shows ─────

    public string Path { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string Extension { get; private set; } = "";
    public FileKind FileKind { get; private set; } = FileKind.Other;
    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public string RootPath { get; private set; } = "";
    public string RootName { get; private set; } = "";

    /// <summary>The file's directory, normalized.</summary>
    public string Directory { get; private set; } = "";
    public double Duration { get; private set; }
    public string DurationText { get; private set; } = "";
    public long Size { get; private set; }
    public string SizeText { get; private set; } = "";
    public DateTime Added { get; private set; }
    public string AddedText { get; private set; } = "";
    public int Chapters { get; private set; }
    public string ChaptersText { get; private set; } = "";
    public bool HasChapters => Chapters > 0;
    public string SearchKey { get; private set; } = "";
    public string ToolTip { get; private set; } = "";

    /// <summary>Container: open, showing its children. Set by the projection from the expanded flags; the chevron asks the view model to flip it.</summary>
    public bool IsExpanded { get; private init; }

    /// <summary>Container: files below it that the list shows.</summary>
    public int Count { get; private init; }

    internal Presence Presence { get; private set; } = Presence.Unknown;
    public bool CanRelocate { get; private init; }
    public bool IsDimmed => Kind == RowKind.Missing;

    /// <summary>The item's id when the row stands for an item (a member, a source, a missing member).</summary>
    public string? ItemId => Item?.Id;
    public string? GroupId => Group?.Id;

    public override string ToString() => $"{Kind} {Title} [{Key}]";
}

/// <summary>Accent- and case-insensitive matching: "deja" finds "Déjà Vu", every word of the query has to be there.</summary>
internal static class SearchText
{
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            if (char.IsWhiteSpace(c) || c is '_' or '-' or '–' or '—' or '/' or '\\' or '.' or ',' or '(' or ')' or '[' or ']')
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Every whitespace-separated word of <paramref name="query"/> appears in <paramref name="key"/> (already normalized).</summary>
    public static bool Matches(string key, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        foreach (var word in Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!key.Contains(word, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}

internal static class Format
{
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"4,2 MB" in the current culture; hours and minutes for a total ("21 h 05 min").</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("N1", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("N1", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("N0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes + " B",
    };

    public static string TotalDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min";
    }

    public static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
