using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YtDlpCore;

/// <summary>
/// Helpers for yt-dlp output templates (<c>%(field)s</c>, <c>%(playlist_index)02d</c>, ...).
/// Used to turn a playlist-wide template into a per-video one: each queue job downloads a single video
/// by its own URL, so the playlist tokens are baked in as literals instead of asking yt-dlp to
/// re-resolve the playlist for every item.
/// </summary>
public static partial class OutputTemplate
{
    [GeneratedRegex(@"%\((?<k>\w+)\)(?<pad>0\d+)?(?<type>[sd])", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Replaces every <c>%(field)…</c> token of one field with a literal value, honoring the
    /// <c>0Nd</c> zero-padding on integer tokens. The literal is sanitized like yt-dlp sanitizes
    /// field values (path-illegal characters become their full-width look-alikes) and <c>%</c> is
    /// escaped so it can't be read back as a template field.
    /// </summary>
    public static string SubstituteLiteral(string template, string field, string literal)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        return TokenRegex().Replace(template, m =>
        {
            if (!string.Equals(m.Groups["k"].Value, field, StringComparison.OrdinalIgnoreCase))
                return m.Value;

            var value = literal;
            if (m.Groups["type"].Value == "d" && m.Groups["pad"].Success &&
                int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                value = n.ToString("D" + int.Parse(m.Groups["pad"].Value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            return EscapeLiteral(SanitizeSegment(value));
        });
    }

    /// <summary>Prefixes the file-name part of the template (the text after the last <c>/</c>) with a literal.</summary>
    public static string PrefixFileName(string template, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return template;
        var escaped = EscapeLiteral(SanitizeSegment(prefix, trim: false));   // "1 - " keeps its trailing space
        var slash = template.LastIndexOf('/');
        return slash < 0 ? escaped + template : template[..(slash + 1)] + escaped + template[(slash + 1)..];
    }

    /// <summary>
    /// Makes a playlist-flavoured template usable for a lone video: the playlist tokens are dropped and
    /// the separators they left dangling are tidied, so "%(playlist_title)s/%(playlist_index)02d - %(title)s.%(ext)s"
    /// becomes "%(title)s.%(ext)s" instead of yt-dlp's "NA/NA - title.mp3".
    /// </summary>
    public static string WithoutPlaylistTokens(string template)
    {
        var stripped = SubstituteLiteral(SubstituteLiteral(template, "playlist_title", ""), "playlist_index", "");
        var segments = stripped.Split('/')
            .Select(seg => seg.TrimStart(' ', '-', '_', '.').TrimEnd(' ', '-', '_'))
            .Where(seg => seg.Length > 0)
            .ToList();
        return segments.Count == 0 ? "%(title)s.%(ext)s" : string.Join("/", segments);
    }

    /// <summary>Doubles <c>%</c> so the literal survives yt-dlp's template expansion untouched.</summary>
    public static string EscapeLiteral(string literal) => literal.Replace("%", "%%");

    /// <summary>
    /// Makes a value safe as a single path segment on Windows, the way yt-dlp does for field values:
    /// each illegal character is swapped for its full-width twin (<c>/</c> → <c>⧸</c>, <c>:</c> → <c>：</c>, ...)
    /// so titles stay readable instead of collapsing to underscores.
    /// </summary>
    public static string SanitizeSegment(string value, bool trim = true)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '/' => '⧸',
                '\\' => '⧹',
                ':' => '：',
                '*' => '＊',
                '?' => '？',
                '"' => '＂',
                '<' => '＜',
                '>' => '＞',
                '|' => '｜',
                _ when c < ' ' => '_',
                _ => c,
            });
        }
        return trim ? sb.ToString().Trim().TrimEnd('.') : sb.ToString();
    }
}
