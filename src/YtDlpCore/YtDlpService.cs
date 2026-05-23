using System.Diagnostics;
using System.Text.Json;

namespace YtDlpCore;

/// <summary>
/// Concrete <see cref="IYtDlpService"/>. Owns the shared <see cref="HttpClient"/> and wires together
/// the process runner, binary manager, updater and thumbnail cache. All work is async/cancellable;
/// every log line is both persisted (if a logger was supplied) and surfaced via <see cref="LogEmitted"/>.
/// </summary>
public sealed class YtDlpService : IYtDlpService, IDisposable
{
    private readonly HttpClient _http;
    private readonly YtDlpProcessRunner _runner;
    private readonly BinaryManager _binaries;
    private readonly UpdateChecker _updater;
    private readonly ThumbnailCache _thumbnails;
    private readonly SessionLogger? _logger;

    public event EventHandler<LogEntry>? LogEmitted;

    public YtDlpService(SessionLogger? logger = null, string? toolsDirectory = null, string? cacheDirectory = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PixieDownloader/1.0");

        _runner = new YtDlpProcessRunner();
        _binaries = new BinaryManager(_http, _runner, EmitEntry, toolsDirectory);
        _updater = new UpdateChecker(_http, _binaries);
        _thumbnails = new ThumbnailCache(_http, EmitEntry, cacheDirectory);
    }

    /// <summary>Resolved tools directory (./tools), exposed so the UI can offer "open folder".</summary>
    public string ToolsDirectory => _binaries.ToolsDirectory;
    public string ThumbnailCacheDirectory => _thumbnails.CacheDirectory;

    // ───────────────────────── Binary management ─────────────────────────

    public Task<ToolStatus> CheckYtDlpAsync(CancellationToken ct = default) => _binaries.CheckYtDlpAsync(ct);
    public Task<ToolStatus> CheckFfmpegAsync(CancellationToken ct = default) => _binaries.CheckFfmpegAsync(ct);
    public Task DownloadYtDlpAsync(IProgress<double>? progress, CancellationToken ct) => _binaries.DownloadYtDlpAsync(progress, ct);
    public Task DownloadFfmpegAsync(FfmpegInstallKind kind, IProgress<double>? progress, CancellationToken ct) => _binaries.DownloadFfmpegAsync(kind, progress, ct);
    public Task<UpdateInfo> CheckYtDlpUpdateAsync(CancellationToken ct = default) => _updater.CheckYtDlpUpdateAsync(ct);
    public Task UpdateYtDlpAsync(IProgress<double>? progress, CancellationToken ct) => _binaries.UpdateYtDlpAsync(progress, ct);
    public Task<string> GetThumbnailAsync(string videoId, string thumbnailUrl, CancellationToken ct) => _thumbnails.GetAsync(videoId, thumbnailUrl, ct);

    // ───────────────────────── URL analysis ─────────────────────────

    public async Task<UrlInfo> AnalyzeUrlAsync(string url, bool treatAsPlaylist, CancellationToken ct)
    {
        var ytDlp = RequireYtDlp();
        Emit(LogLevel.Info, "Core", $"Analisando URL ({(treatAsPlaylist ? "playlist" : "vídeo único")})...", url);

        string[] args = treatAsPlaylist
            ? ["--dump-single-json", "--flat-playlist", "--no-warnings", url]
            : ["--dump-single-json", "--no-playlist", "--no-warnings", url];
        var (code, stdout, stderr) = await _runner.RunCapturedAsync(ytDlp, args, ct).ConfigureAwait(false);
        LogStderr(stderr, url);

        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            var msg = FirstError(stderr) ?? $"yt-dlp saiu com código {code} ao analisar a URL.";
            throw new InvalidOperationException(msg);
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var type = GetString(root, "_type");

        if (string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            var playlist = ParsePlaylist(root, url);
            Emit(LogLevel.Info, "Core", $"Playlist '{playlist.Title}' com {playlist.Items.Count} itens.", url);
            return new PlaylistUrlInfo { OriginalUrl = url, Playlist = playlist };
        }

        var video = ParseVideo(root, url);
        Emit(LogLevel.Info, "Core", $"Vídeo '{video.Title}'.", url);
        return new VideoUrlInfo { OriginalUrl = url, Video = video };
    }

    // ───────────────────────── Download ─────────────────────────

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var ytDlp = RequireYtDlp();
        Directory.CreateDirectory(request.OutputDirectory);
        PrepareStagingDir(request.OutputDirectory);

        var sw = Stopwatch.StartNew();
        var args = BuildDownloadArgs(request);
        Emit(LogLevel.Info, "Core", $"Download iniciado: {request.Url}", request.Url);

        var currentTitle = request.Url;
        var stage = DownloadStage.Downloading;
        double lastPercent = 0;
        string? destPath = null;
        string? lastError = null;

        void OnStdout(string line)
        {
            var st = YtDlpOutputParser.DetectStage(line);
            if (st is not null)
                stage = st.Value;

            var dest = YtDlpOutputParser.TryParseDestination(line);
            if (dest is not null && (destPath is null || dest.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                destPath = dest;

            var p = YtDlpOutputParser.TryParseProgress(line);
            if (p is not null)
            {
                lastPercent = p.Value.Percent;
                progress?.Report(new DownloadProgress(currentTitle, lastPercent, p.Value.Speed, p.Value.Eta, stage));
            }
            else if (st is not null)
            {
                progress?.Report(new DownloadProgress(currentTitle, lastPercent, null, null, stage));
            }

            Emit(LogLevel.Debug, "YtDlp", line, request.Url);
        }

        void OnStderr(string line)
        {
            var (level, message) = YtDlpOutputParser.ClassifyStderr(line);
            if (level == LogLevel.Error)
                lastError = message;
            Emit(level, "YtDlp", message, request.Url);
        }

        try
        {
            int code = await _runner.RunAsync(ytDlp, args, OnStdout, OnStderr, request.OutputDirectory, ct).ConfigureAwait(false);
            sw.Stop();

            if (code == 0)
            {
                progress?.Report(new DownloadProgress(currentTitle, 100, null, null, DownloadStage.Done));
                Emit(LogLevel.Info, "Core", $"Concluído: {destPath ?? request.Url}", request.Url);
                return new DownloadResult(request.Url, true, destPath, null, sw.Elapsed);
            }

            var err = lastError ?? $"yt-dlp saiu com código {code}.";
            progress?.Report(new DownloadProgress(currentTitle, lastPercent, null, null, DownloadStage.Failed));
            Emit(LogLevel.Error, "Core", $"Falha: {err}", request.Url);
            return new DownloadResult(request.Url, false, destPath, err, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            progress?.Report(new DownloadProgress(currentTitle, lastPercent, null, null, DownloadStage.Cancelled));
            Emit(LogLevel.Warning, "Core", "Download cancelado.", request.Url);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Emit(LogLevel.Error, "Core", ex.Message, request.Url, ex);
            return new DownloadResult(request.Url, false, destPath, ex.Message, sw.Elapsed);
        }
    }

    public async Task<IReadOnlyList<DownloadResult>> DownloadBatchAsync(BatchDownloadRequest request, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var total = request.Urls.Count;
        var results = new DownloadResult[total];
        var maxParallel = Math.Clamp(request.MaxParallel, 1, 10);

        using var sem = new SemaphoreSlim(maxParallel);
        var stateLock = new object();
        int completed = 0, success = 0, failure = 0;

        Emit(LogLevel.Info, "Core", $"Batch iniciado: {total} itens, {maxParallel} em paralelo.");

        var tasks = request.Urls.Select(async (url, index) =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var itemProgress = new Progress<DownloadProgress>(dp =>
                {
                    lock (stateLock)
                        progress?.Report(new BatchProgress(completed + 1, total, dp, success, failure));
                });

                var req = new DownloadRequest(url, request.OutputDirectory, request.OutputTemplate, request.Audio, request.Advanced);
                var res = await DownloadAsync(req, itemProgress, ct).ConfigureAwait(false);
                results[index] = res;

                lock (stateLock)
                {
                    completed++;
                    if (res.Success) success++; else failure++;
                    progress?.Report(new BatchProgress(completed, total, null, success, failure));
                }
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        Emit(LogLevel.Info, "Core", $"Batch concluído: {success} ok, {failure} falhas.");
        return results;
    }

    // ───────────────────────── Debug ─────────────────────────

    public async Task<RawCommandResult> RunRawAsync(string[] args, CancellationToken ct)
    {
        var ytDlp = RequireYtDlp();
        Emit(LogLevel.Info, "YtDlp", $"raw: {string.Join(' ', args)}");
        var (code, stdout, stderr) = await _runner.RunCapturedAsync(ytDlp, args, ct).ConfigureAwait(false);
        return new RawCommandResult(args, code, stdout, stderr);
    }

    // ───────────────────────── Argument building ─────────────────────────

    /// <summary>
    /// Hidden staging folder (under the output directory) where yt-dlp keeps all in-progress
    /// junk — <c>.part</c> fragments, raw <c>.webm</c>/<c>.webp</c>, pre-embed thumbnails. Only the
    /// finished file is moved out to the output directory. Same volume → the move is atomic.
    /// </summary>
    internal static string GetStagingDir(string outputDirectory)
        => Path.Combine(outputDirectory, ".~downloads");

    private void PrepareStagingDir(string outputDirectory)
    {
        try
        {
            var di = Directory.CreateDirectory(GetStagingDir(outputDirectory));
            if (!di.Attributes.HasFlag(FileAttributes.Hidden))
                di.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Warning, "Core", $"Não foi possível preparar a pasta oculta de staging: {ex.Message}");
        }
    }

    private List<string> BuildDownloadArgs(DownloadRequest r)
    {
        var bitrate = r.Audio.Bitrate.ToUpperInvariant().Trim(); // "192k" -> "192K"

        var args = new List<string>
        {
            "-x",
            "--audio-format", "mp3",
            "--audio-quality", bitrate,
            "-f", "bestaudio/best",
            "-o", r.OutputTemplate,                         // relative template (may include subfolders)
            "-P", $"home:{r.OutputDirectory}",              // final files land here...
            "-P", $"temp:{GetStagingDir(r.OutputDirectory)}", // ...intermediate junk stays hidden here
            "--newline",            // emit progress on its own lines (cleaner parsing)
            "--no-mtime",
            "--retries", r.Advanced.Retries.ToString(),
            "--socket-timeout", r.Advanced.TimeoutSeconds.ToString(),
        };

        if (string.IsNullOrWhiteSpace(r.PlaylistItems))
        {
            args.Add("--no-playlist");
        }
        else
        {
            args.Add("--yes-playlist");
            args.Add("--playlist-items");
            args.Add(r.PlaylistItems);
        }

        var ffmpegPath = _binaries.ResolveFfmpegPath();
        var ffmpegDir = ffmpegPath is null ? null : Path.GetDirectoryName(ffmpegPath);
        if (!string.IsNullOrEmpty(ffmpegDir))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegDir);
        }

        if (r.Audio.EmbedThumbnail)
            args.Add("--embed-thumbnail");

        if (r.Audio.EmbedMetadata)
            args.Add("--embed-metadata");

        if (r.Advanced.MaxDuration is { } max)
        {
            args.Add("--match-filter");
            args.Add($"duration < {(int)max.TotalSeconds}");
        }

        if (!string.IsNullOrWhiteSpace(r.Advanced.CookiesFile))
        {
            args.Add("--cookies");
            args.Add(r.Advanced.CookiesFile);
        }

        args.Add(r.Url);
        return args;
    }

    // ───────────────────────── JSON parsing ─────────────────────────

    private static PlaylistInfo ParsePlaylist(JsonElement root, string originalUrl)
    {
        var id = GetString(root, "id") ?? "";
        var title = GetString(root, "title") ?? "(playlist sem título)";
        var uploader = GetString(root, "uploader") ?? GetString(root, "channel");

        var items = new List<VideoInfo>();
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue; // null entry = unavailable/private item
                items.Add(ParseVideo(entry, originalUrl));
            }
        }

        return new PlaylistInfo(id, title, uploader, items);
    }

    private static VideoInfo ParseVideo(JsonElement e, string originalUrl)
    {
        var id = GetString(e, "id") ?? "";
        var title = GetString(e, "title") ?? "(sem título)";
        var uploader = GetString(e, "uploader") ?? GetString(e, "channel") ?? GetString(e, "uploader_id");

        TimeSpan? duration = null;
        if (e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
            duration = TimeSpan.FromSeconds(d.GetDouble());

        var thumbnail = GetString(e, "thumbnail") ?? PickBestThumbnail(e) ?? YouTubeThumbnail(id);

        var webpage = GetString(e, "webpage_url") ?? NormalizeEntryUrl(GetString(e, "url"), id) ?? originalUrl;

        return new VideoInfo(id, title, uploader, duration, thumbnail, webpage);
    }

    private static string? PickBestThumbnail(JsonElement e)
    {
        if (!e.TryGetProperty("thumbnails", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        foreach (var t in arr.EnumerateArray())
        {
            var u = GetString(t, "url");
            if (!string.IsNullOrEmpty(u))
                best = u; // yt-dlp orders ascending in quality — keep the last
        }
        return best;
    }

    private static string? YouTubeThumbnail(string id)
        => id.Length == 11 ? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg" : null;

    private static string? NormalizeEntryUrl(string? url, string id)
    {
        if (!string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return url;
        return id.Length == 11 ? $"https://www.youtube.com/watch?v={id}" : null;
    }

    private static string? GetString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ───────────────────────── Logging helpers ─────────────────────────

    private void LogStderr(string stderr, string url)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return;
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (level, message) = YtDlpOutputParser.ClassifyStderr(line);
            Emit(level, "YtDlp", message, url);
        }
    }

    private static string? FirstError(string stderr)
    {
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (level, message) = YtDlpOutputParser.ClassifyStderr(line);
            if (level == LogLevel.Error)
                return message;
        }
        return null;
    }

    private string RequireYtDlp()
        => _binaries.ResolveYtDlpPath()
           ?? throw new InvalidOperationException("yt-dlp não está instalado. Instale-o em ./tools/ ou no PATH.");

    private void Emit(LogLevel level, string source, string message, string? url = null, Exception? ex = null)
        => EmitEntry(LogEntry.Now(level, source, message, url, ex));

    private void EmitEntry(LogEntry entry)
    {
        _logger?.Log(entry);
        LogEmitted?.Invoke(this, entry);
    }

    public void Dispose() => _http.Dispose();
}
