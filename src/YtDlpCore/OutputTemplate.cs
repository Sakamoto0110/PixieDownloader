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

    /// <summary>
    /// The template one playlist item downloads with: the playlist's title and the item's position are
    /// baked in as literals, because each job fetches the video by its own URL and yt-dlp would know neither.
    /// </summary>
    public static string ForPlaylistItem(string template, string playlistTitle, int index)
        => SubstituteLiteral(SubstituteLiteral(template, "playlist_title", playlistTitle),
                             "playlist_index", index.ToString(CultureInfo.InvariantCulture));

    /// <summary>True when the template has at least one <c>%(field…)</c> for that field.</summary>
    public static bool UsesField(string template, string field)
        => FieldRegex().Matches(template ?? "")
            .Any(m => m.Value != "%%" && string.Equals(LeadingKey(m.Groups["expr"].Value), field, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"%%|%\((?<expr>[^)]*)\)(?<flags>[#0\-+ ]*)(?<width>\d*)(?:\.(?<prec>\d+))?(?<type>[a-zA-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"^\w+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingKeyRegex();

    private static string LeadingKey(string expr) => LeadingKeyRegex().Match(expr).Value;

    /// <summary>
    /// Expands a template the way yt-dlp would, for showing it before the download: each field's value is
    /// sanitized like yt-dlp sanitizes it (so a <c>/</c> in a title doesn't become a folder), <c>%%</c> becomes
    /// <c>%</c>, <c>0Nd</c> pads, <c>.Ns</c> truncates, <c>a,b</c> takes the first field that has a value,
    /// <c>&gt;%Y-%m-%d</c> formats a date (<c>YYYYMMDD</c>) or a Unix time, <c>&amp;text</c> replaces a present
    /// value and <c>|default</c> stands in for a missing one. A
    /// field with no value becomes <paramref name="missing"/>(first field). The result is split on <c>/</c> and <c>\</c>.
    /// </summary>
    public static RenderedPath Render(string template, IReadOnlyDictionary<string, string?> values, Func<string, string> missing)
    {
        var text = FieldRegex().Replace(template ?? "", m =>
        {
            if (m.Value == "%%")
                return "%";

            // yt-dlp's grammar: %(field>fmt,alt>fmt&replacement|default)…  — each alternative carries its own
            // date format; the replacement and the default apply to the whole expression.
            var expr = m.Groups["expr"].Value;
            string? fallback = null;
            var bar = expr.IndexOf('|');
            if (bar >= 0)
                (expr, fallback) = (expr[..bar], expr[(bar + 1)..]);
            string? replacement = null;
            var amp = expr.IndexOf('&');
            if (amp >= 0)
                (expr, replacement) = (expr[..amp], expr[(amp + 1)..]);

            string? value = null;
            string? dateFormat = null;
            var firstKey = "";
            foreach (var alternative in expr.Split(','))
            {
                var field = alternative.Trim();
                string? format = null;
                var gt = field.IndexOf('>');
                if (gt >= 0)
                    (field, format) = (field[..gt], field[(gt + 1)..]);
                var key = LeadingKey(field);
                if (firstKey.Length == 0)
                    firstKey = key;
                if (values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                {
                    (value, dateFormat) = (v, format);
                    break;
                }
            }
            if (value is null)
                return fallback ?? missing(firstKey);
            if (replacement is not null)
                return SanitizeSegment(replacement, trim: false);

            if (dateFormat is not null && TryParseDate(value, out var date))
                value = Strftime(date, dateFormat);

            if (m.Groups["type"].Value == "d" && m.Groups["flags"].Value.Contains('0') && m.Groups["width"].Value.Length > 0 &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                value = n.ToString("D" + m.Groups["width"].Value, CultureInfo.InvariantCulture);
            }
            else if (m.Groups["type"].Value == "s" && m.Groups["prec"].Success &&
                     int.TryParse(m.Groups["prec"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && value.Length > max)
            {
                value = value[..max];
            }
            return SanitizeSegment(value, trim: false);
        });

        // Windows drops trailing spaces and dots from every path component.
        var parts = text.Split('/', '\\')
            .Select(p => p.Trim().TrimEnd('.'))
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count == 0
            ? new RenderedPath([], "")
            : new RenderedPath(parts[..^1], parts[^1]);
    }

    /// <summary>yt-dlp's date fields are <c>YYYYMMDD</c>; <c>epoch</c>/<c>timestamp</c> are Unix seconds.</summary>
    private static bool TryParseDate(string value, out DateTime date)
    {
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 253402300800)
        {
            date = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            return true;
        }
        date = default;
        return false;
    }

    /// <summary>The strftime directives a file name realistically uses; anything else is left as written.</summary>
    private static string Strftime(DateTime date, string format)
    {
        var sb = new StringBuilder(format.Length + 8);
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 == format.Length)
            {
                sb.Append(format[i]);
                continue;
            }
            var directive = format[++i];
            sb.Append(directive switch
            {
                'Y' => date.ToString("yyyy", CultureInfo.InvariantCulture),
                'y' => date.ToString("yy", CultureInfo.InvariantCulture),
                'm' => date.ToString("MM", CultureInfo.InvariantCulture),
                'd' => date.ToString("dd", CultureInfo.InvariantCulture),
                'H' => date.ToString("HH", CultureInfo.InvariantCulture),
                'M' => date.ToString("mm", CultureInfo.InvariantCulture),
                'S' => date.ToString("ss", CultureInfo.InvariantCulture),
                '%' => "%",
                _ => "%" + directive,
            });
        }
        return sb.ToString();
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

/// <summary>A template expanded for display: the subfolders it creates, in order, and the file name (extension included).</summary>
public sealed record RenderedPath(IReadOnlyList<string> Folders, string FileName);
