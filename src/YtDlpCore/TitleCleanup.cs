using System.Text.RegularExpressions;

namespace YtDlpCore;

/// <summary>
/// A video uploaded straight from a file often keeps the file's extension in its title ("Aula 3.mp4"), and
/// the download comes out "Aula 3.mp4.mp3". Every download cuts the trailing media extension(s) from the
/// title (<see cref="YtDlpArgs"/>) before yt-dlp names the file and writes the title tag; the output preview
/// cuts them with the same pattern (<see cref="StripMediaExtension"/>), so the two can't drift apart.
/// </summary>
public static partial class TitleCleanup
{
    /// <summary>
    /// One or more media extensions at the very end of the title, with something that isn't blank before
    /// them (a title that is only ".mp4" is left alone). Written to mean the same as a .NET regex here and
    /// as a Python one in yt-dlp: inline <c>(?i)</c> first, a fixed-width lookbehind, nothing else exotic.
    /// </summary>
    public const string MediaExtensionPattern =
        @"(?i)(?<=\S)(?:\.(?:mp4|m4v|mkv|webm|mov|avi|wmv|flv|3gp|mpe?g|mp3|m4a|aac|wav|flac|ogg|oga|opus|wma))+\s*$";

    [GeneratedRegex(MediaExtensionPattern, RegexOptions.CultureInvariant)]
    private static partial Regex MediaExtensionRegex();

    /// <summary>The title as the download will use it.</summary>
    public static string StripMediaExtension(string title)
        => string.IsNullOrEmpty(title) ? title : MediaExtensionRegex().Replace(title, "");

    /// <summary>yt-dlp's own way to do the same to the <c>title</c> field, before the output template is expanded.</summary>
    public static IReadOnlyList<string> YtDlpArgs { get; } = ["--replace-in-metadata", "title", MediaExtensionPattern, ""];
}
