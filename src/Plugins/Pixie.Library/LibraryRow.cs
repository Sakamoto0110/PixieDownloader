using System.Globalization;
using System.IO;
using System.Text;
using Pixie.Library.Catalog;

namespace Pixie.Library;

/// <summary>
/// One file as the tab shows it: an immutable snapshot of a <see cref="LibraryEntry"/> plus the root it sits
/// under. A changed entry is a new row in the same place, not a row that mutates.
/// </summary>
public sealed class LibraryRow
{
    internal LibraryRow(LibraryEntry entry, LibraryRoot? root)
    {
        Entry = entry;
        Path = entry.Path;
        FileName = System.IO.Path.GetFileName(entry.Path);
        Extension = System.IO.Path.GetExtension(entry.Path).TrimStart('.').ToUpperInvariant();
        Kind = FileKinds.KindOf(entry.Path);
        Title = entry.Title ?? System.IO.Path.GetFileNameWithoutExtension(entry.Path);
        RootPath = root is null ? "" : PathUtil.Normalize(root.Path);
        RootName = root?.Name ?? "";
        Folder = root is null ? (System.IO.Path.GetDirectoryName(entry.Path) ?? "") : PathUtil.Relative(System.IO.Path.GetDirectoryName(entry.Path) ?? RootPath, RootPath);
        var byline = string.Join(" · ", new[] { entry.Artist, entry.Album }.Where(s => !string.IsNullOrEmpty(s)));
        Subtitle = byline.Length > 0 ? byline : Folder.Length > 0 ? Folder : RootName;
        Duration = entry.Duration ?? 0;
        DurationText = entry.Duration is { } d ? Format.Duration(d) : "";
        Size = entry.Size;
        SizeText = Format.Size(entry.Size);
        Added = entry.Created;
        AddedText = entry.Created.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        Chapters = entry.Chapters;
        ChaptersText = entry.Chapters > 0 ? $"{entry.Chapters} faixas" : "";
        SearchKey = SearchText.Normalize(string.Join(" ", new[] { Title, entry.Artist, entry.Album, FileName, Folder, RootName }.Where(s => !string.IsNullOrEmpty(s))));
        ToolTip = entry.Path
            + (entry.Bitrate is { } b ? $"\n{b} kbps" : "")
            + (entry.Chapters > 0 ? $"\n{entry.Chapters} capítulos ID3" : "")
            + $"\nadicionado em {entry.Created.ToLocalTime():g}";
    }

    internal LibraryEntry Entry { get; }
    public string Path { get; }
    public string FileName { get; }
    public string Extension { get; }
    public FileKind Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string RootPath { get; }
    public string RootName { get; }
    public string Folder { get; }
    public double Duration { get; }
    public string DurationText { get; }
    public long Size { get; }
    public string SizeText { get; }
    public DateTime Added { get; }
    public string AddedText { get; }
    public int Chapters { get; }
    public string ChaptersText { get; }
    public bool HasChapters => Chapters > 0;
    public string SearchKey { get; }
    public string ToolTip { get; }
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
