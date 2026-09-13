using System.Globalization;
using System.Text.Json;

namespace YtDlpCore;

/// <summary>
/// One tag that yt-dlp's <c>--embed-metadata</c> writes into the output file.
/// <see cref="Key"/> is our stable id (also what gets persisted when the user excludes the field),
/// <see cref="FfmpegKeys"/> are the tag names yt-dlp sets for it (some fields set two, e.g.
/// description + synopsis), and <see cref="JsonSources"/> are the info-json fields it reads, in the
/// same priority order yt-dlp uses.
/// </summary>
public sealed record MetadataFieldDef(string Key, string Label, string[] FfmpegKeys, string[] JsonSources);

/// <summary>
/// The catalogue of metadata yt-dlp embeds (mirrors <c>FFmpegMetadataPP</c>'s mapping) plus the helper
/// that pulls their values out of a <c>--dump-single-json</c> document, so the UI can list exactly what
/// would land in the file and let the user opt out per field.
/// </summary>
public static class MetadataFields
{
    public static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public static readonly IReadOnlyList<MetadataFieldDef> Embeddable =
    [
        new("title",         "Título",            ["title"],                   ["track", "title"]),
        new("artist",        "Artista",           ["artist"],                  ["artist", "artists", "creator", "creators", "uploader", "uploader_id"]),
        new("date",          "Data",              ["date"],                    ["upload_date"]),
        new("description",   "Descrição",         ["description", "synopsis"], ["description"]),
        new("purl",          "Link (URL)",        ["purl", "comment"],         ["webpage_url"]),
        new("track",         "Nº da faixa",       ["track"],                   ["track_number"]),
        new("album",         "Álbum",             ["album"],                   ["album"]),
        new("album_artist",  "Artista do álbum",  ["album_artist"],            ["album_artist", "album_artists"]),
        new("genre",         "Gênero",            ["genre"],                   ["genre", "genres", "categories"]),
        new("composer",      "Compositor",        ["composer"],                ["composer", "composers"]),
        new("disc",          "Disco",             ["disc"],                    ["disc_number"]),
        new("show",          "Série / programa",  ["show"],                    ["series", "show"]),
        new("season_number", "Temporada",         ["season_number"],           ["season_number"]),
        new("episode_id",    "Episódio",          ["episode_id"],              ["episode", "episode_id"]),
        new("episode_sort",  "Nº do episódio",    ["episode_sort"],            ["episode_number"]),
    ];

    public static MetadataFieldDef? Find(string key)
        => Embeddable.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads every embeddable field that has a value in the info-json object, in catalogue order.</summary>
    public static IReadOnlyDictionary<string, string> Extract(JsonElement info)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (info.ValueKind != JsonValueKind.Object)
            return found;

        foreach (var def in Embeddable)
        {
            foreach (var source in def.JsonSources)
            {
                if (info.TryGetProperty(source, out var el) && Stringify(el) is { Length: > 0 } value)
                {
                    found[def.Key] = value;
                    break;
                }
            }
        }
        return found;
    }

    /// <summary>
    /// The ffmpeg tag names to blank for a set of excluded field keys — what
    /// <c>--parse-metadata ":(?P&lt;meta_TAG&gt;)"</c> needs, one per tag.
    /// </summary>
    public static IEnumerable<string> FfmpegKeysFor(IEnumerable<string> excludedKeys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in excludedKeys)
        {
            if (Find(key) is not { } def)
                continue;
            foreach (var tag in def.FfmpegKeys)
                if (seen.Add(tag))
                    yield return tag;
        }
    }

    private static string? Stringify(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString()?.Trim(),
        JsonValueKind.Number => el.TryGetInt64(out var l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : el.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(", ",
            el.EnumerateArray().Select(Stringify).Where(s => !string.IsNullOrEmpty(s))),
        _ => null,
    };
}
