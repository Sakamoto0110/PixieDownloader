using System.Text.Json;

namespace YtDlpCore;

/// <summary>Compares the installed yt-dlp version against the latest GitHub release.</summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string ReleaseTagBase = "https://github.com/yt-dlp/yt-dlp/releases/tag/";

    private readonly HttpClient _http;
    private readonly BinaryManager _binaries;

    public UpdateChecker(HttpClient http, BinaryManager binaries)
    {
        _http = http;
        _binaries = binaries;
    }

    public async Task<UpdateInfo> CheckYtDlpUpdateAsync(CancellationToken ct = default)
    {
        var current = (await _binaries.CheckYtDlpAsync(ct).ConfigureAwait(false)).Version ?? "—";

        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var releaseUrl = string.IsNullOrEmpty(tag) ? ReleaseTagBase : ReleaseTagBase + tag;

        bool updateAvailable = !string.IsNullOrEmpty(tag)
            && !string.Equals(current.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase);

        return new UpdateInfo(current, tag, updateAvailable, releaseUrl);
    }
}
