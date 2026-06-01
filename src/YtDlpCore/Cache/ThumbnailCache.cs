namespace YtDlpCore;

/// <summary>
/// On-disk LRU thumbnail cache at <c>./cache/thumbnails/&lt;videoId&gt;.jpg</c>. Recency is tracked
/// via last-write-time (touched on every hit) so eviction works regardless of NTFS last-access
/// being disabled. Capped at <see cref="_maxBytes"/> (default 500MB).
/// </summary>
public sealed class ThumbnailCache
{
    private readonly HttpClient _http;
    private readonly Action<LogEntry>? _log;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _evictGate = new(1, 1);

    public string CacheDirectory { get; }

    public ThumbnailCache(HttpClient http, Action<LogEntry>? log = null, string? cacheDirectory = null, long maxBytes = 500L * 1024 * 1024)
    {
        _http = http;
        _log = log;
        _maxBytes = maxBytes;
        CacheDirectory = cacheDirectory ?? Path.Combine(AppContext.BaseDirectory, "cache", "thumbnails");
        Directory.CreateDirectory(CacheDirectory);
    }

    /// <summary>Returns the local cached path, downloading the thumbnail first if needed.</summary>
    public async Task<string> GetAsync(string videoId, string thumbnailUrl, CancellationToken ct)
    {
        var path = PathFor(videoId);

        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        var tmp = path + ".tmp";
        try
        {
            using var resp = await _http.GetAsync(thumbnailUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
            Touch(path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        _ = EvictIfNeededAsync();
        return path;
    }

    public string PathFor(string videoId) => Path.Combine(CacheDirectory, Sanitize(videoId) + ".jpg");

    private async Task EvictIfNeededAsync()
    {
        if (!await _evictGate.WaitAsync(0).ConfigureAwait(false))
            return; // an eviction pass is already running

        try
        {
            await Task.Run(() =>
            {
                var files = new DirectoryInfo(CacheDirectory).GetFiles("*.jpg");
                long total = files.Sum(f => f.Length);
                if (total <= _maxBytes)
                    return;

                foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= _maxBytes)
                        break;
                    try
                    {
                        total -= f.Length;
                        f.Delete();
                    }
                    catch { /* in use elsewhere — skip */ }
                }
                _log?.Invoke(LogEntry.Now(LogLevel.Debug, "Cache", "Thumbnail cache evicted to fit size limit."));
            }).ConfigureAwait(false);
        }
        finally
        {
            _evictGate.Release();
        }
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Sanitize(string videoId)
    {
        Span<char> buffer = stackalloc char[videoId.Length];
        var invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < videoId.Length; i++)
            buffer[i] = Array.IndexOf(invalid, videoId[i]) >= 0 ? '_' : videoId[i];
        var result = new string(buffer);
        return string.IsNullOrWhiteSpace(result) ? "thumb" : result;
    }
}
