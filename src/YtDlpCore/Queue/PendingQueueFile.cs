using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpCore;

/// <summary>
/// One download that survives an app restart: everything needed to rebuild its
/// <see cref="DownloadRequest"/> (the advanced options come from the current settings on resume).
/// </summary>
public sealed record PendingDownload(
    string Url,
    string Title,
    string? OutputDirectory,
    string? OutputTemplate,
    AudioOptions? Audio,
    VideoOptions? Video,
    double? SourceDurationSeconds,
    string? WorkDirectory = null);   // set when the download finished and only the ffmpeg pass is pending

/// <summary>
/// Reads/writes <c>pending-downloads.txt</c>: a comment header followed by one line per download.
/// Each line is a small JSON object so every option round-trips exactly, but a line holding just a
/// URL is accepted too — the file stays hand-editable ("drop some links in, they get queued on start").
/// </summary>
public static class PendingQueueFile
{
    public const string FileName = "pending-downloads.txt";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static string Format(IEnumerable<PendingDownload> entries)
    {
        var lines = new List<string>
        {
            "# PixieDownloader — downloads pendentes. Gerado automaticamente; uma linha por download (JSON).",
            "# Linhas começando com # são ignoradas. Uma linha contendo só uma URL também é aceita.",
        };
        foreach (var e in entries)
            lines.Add(JsonSerializer.Serialize(e, Json));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static IReadOnlyList<PendingDownload> Parse(IEnumerable<string> lines)
    {
        var result = new List<PendingDownload>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('{'))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<PendingDownload>(line, Json);
                    if (parsed is { Url.Length: > 0 })
                        result.Add(parsed);
                }
                catch (JsonException)
                {
                    // A corrupt line is skipped rather than losing the whole queue.
                }
                continue;
            }

            // Bare URL (hand-added): everything else comes from the current settings on resume.
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                result.Add(new PendingDownload(line, line, null, null, null, null, null));   // WorkDirectory: none
            }
        }
        return result;
    }

    public static async Task<IReadOnlyList<PendingDownload>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return [];
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        return Parse(lines);
    }

    /// <summary>Writes the entries, or deletes the file when there is nothing left to resume.</summary>
    public static async Task SaveAsync(string path, IReadOnlyList<PendingDownload> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            Delete(path);
            return;
        }
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, Format(entries), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
