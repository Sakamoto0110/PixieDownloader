using System.Diagnostics;
using System.Globalization;
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
        PrepareStagingDir();

        // When the video needs an ffmpeg pass, download into a private work folder first (see below).
        var needPass = VideoNeedsFfmpegPass(request.Video);
        string? workDir = needPass ? Path.Combine(GetStagingDir(), "proc-" + Guid.NewGuid().ToString("N")) : null;
        if (workDir is not null)
            Directory.CreateDirectory(workDir);

        var sw = Stopwatch.StartNew();
        var args = BuildDownloadArgs(request, workDir);
        Emit(LogLevel.Info, "Core", $"Download iniciado: {request.Url}", request.Url);

        var currentTitle = request.Url;
        var stage = DownloadStage.Downloading;
        double lastPercent = 0;
        string? destPath = null;
        string? lastError = null;
        // which Destination line is the final file
        var primaryExt = request.Video.ExtractGif ? ".gif" : request.Video.DownloadVideo ? ".mp4" : ".mp3";

        void OnStdout(string line)
        {
            var st = YtDlpOutputParser.DetectStage(line);
            if (st is not null)
                stage = st.Value;

            var dest = YtDlpOutputParser.TryParseDestination(line);
            if (dest is not null && (destPath is null || dest.EndsWith(primaryExt, StringComparison.OrdinalIgnoreCase)))
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
                // ffmpeg pass + deliver: locate the real files in the work folder, process, move to output.
                if (workDir is not null)
                    destPath = await PostProcessAndDeliverAsync(workDir, request, progress, currentTitle, ct).ConfigureAwait(false) ?? destPath;

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
        finally
        {
            if (workDir is not null)
            {
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
                catch { /* best-effort cleanup of the work folder */ }
            }
        }
    }

    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm", ".mov"];
    // Files moved from the work folder to the output dir: the produced video — mp4 after a successful pass,
    // or the original container if the pass failed / ffmpeg is missing — plus any separately-extracted mp3
    // or a converted .gif.
    private static readonly string[] DeliverExtensions = [".mp4", ".mkv", ".webm", ".mov", ".mp3", ".gif"];

    /// <summary>
    /// Runs after a successful download into <paramref name="workDir"/>. Locates the produced video file(s)
    /// on disk (robust against Unicode in titles — no log-path parsing), optionally extracts a separate MP3
    /// at normal speed, applies the speed change / audio strip via ffmpeg (always producing mp4), then moves
    /// the final mp4/mp3 to the real output dir preserving the template's subfolders. Returns the main mp4.
    /// </summary>
    private async Task<string?> PostProcessAndDeliverAsync(string workDir, DownloadRequest request, IProgress<DownloadProgress>? progress, string title, CancellationToken ct)
    {
        var ffmpeg = _binaries.ResolveFfmpegPath();
        var v = request.Video;
        var bitrate = request.Audio.Bitrate.ToUpperInvariant().Trim();
        progress?.Report(new DownloadProgress(title, 0, null, null, DownloadStage.Converting));
        void OnLine(string l) => Emit(LogLevel.Debug, "ffmpeg", l, request.Url);

        var videos = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        if (ffmpeg is null)
            Emit(LogLevel.Warning, "Core", "ffmpeg não encontrado — entregando o vídeo sem processar.", request.Url);
        else foreach (var src in videos)
        {
            // GIF mode: convert the (already-trimmed) clip via a two-pass palette conversion instead
            // of the speed/audio pass below — a .gif has no audio track, so this replaces the video.
            if (v.ExtractGif)
            {
                // Slow the clip down first (if requested) — setpts has no lower-bound restriction
                // (unlike atempo for audio, which needs chaining below 0.5x), so a single pass covers
                // the whole 0.25x..1.0x range; the gif has no audio track to worry about at all.
                var gifSource = src;
                string? speedTemp = null;
                if (SpeedChanged(v.GifSpeed))
                {
                    speedTemp = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + ".speed.mp4");
                    Emit(LogLevel.Info, "Core", $"Ajustando velocidade do GIF ({Math.Clamp(v.GifSpeed, 0.1, 8.0):0.##}x)...", request.Url);
                    var speedCode = await _runner.RunAsync(ffmpeg, BuildSpeedPassArgs(src, speedTemp, v.GifSpeed, includeAudio: false, audioBitrate: ""), OnLine, OnLine, workDir, ct).ConfigureAwait(false);
                    if (speedCode == 0 && File.Exists(speedTemp))
                        gifSource = speedTemp;
                    else
                        Emit(LogLevel.Warning, "Core", $"ffmpeg falhou ao ajustar a velocidade (código {speedCode}); GIF sairá na velocidade original.", request.Url);
                }

                var palette = Path.Combine(Path.GetDirectoryName(src)!, "palette.png");
                var outGif = Path.ChangeExtension(src, ".gif");

                Emit(LogLevel.Info, "Core", "Gerando paleta de cores do GIF...", request.Url);
                var paletteCode = await _runner.RunAsync(ffmpeg, BuildGifPaletteArgs(gifSource, palette), OnLine, OnLine, workDir, ct).ConfigureAwait(false);

                if (paletteCode == 0 && File.Exists(palette))
                {
                    Emit(LogLevel.Info, "Core", "Codificando GIF...", request.Url);
                    var encodeCode = await _runner.RunAsync(ffmpeg, BuildGifEncodeArgs(gifSource, palette, outGif), OnLine, OnLine, workDir, ct).ConfigureAwait(false);
                    Emit(encodeCode == 0 && File.Exists(outGif) ? LogLevel.Info : LogLevel.Warning,
                        "Core", encodeCode == 0 && File.Exists(outGif) ? "GIF gerado." : $"ffmpeg falhou ao codificar o GIF (código {encodeCode}).", request.Url);
                }
                else
                {
                    Emit(LogLevel.Warning, "Core", $"ffmpeg falhou ao gerar a paleta do GIF (código {paletteCode}).", request.Url);
                }

                try { File.Delete(src); } catch { /* ignore */ }
                if (speedTemp is not null)
                    try { if (File.Exists(speedTemp)) File.Delete(speedTemp); } catch { /* ignore */ }
                try { if (File.Exists(palette)) File.Delete(palette); } catch { /* ignore */ }
                continue;
            }

            // (a) Separate MP3 from the original (normal-speed) audio, before we re-encode the video.
            if (v.ExtractAudioSeparate)
            {
                var mp3 = Path.ChangeExtension(src, ".mp3");
                string[] mp3Args = ["-hide_banner", "-loglevel", "error", "-y", "-i", src, "-vn", "-c:a", "libmp3lame", "-b:a", bitrate, mp3];
                Emit(LogLevel.Info, "Core", "Extraindo áudio separado (mp3)...", request.Url);
                await _runner.RunAsync(ffmpeg, mp3Args, OnLine, OnLine, workDir, ct).ConfigureAwait(false);
            }

            // (b) Speed change / audio strip — always outputs mp4 (also normalizes webm/mkv -> mp4).
            var outMp4 = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + ".pixie.mp4");
            var passArgs = BuildSpeedPassArgs(src, outMp4, v.Speed, v.IncludeAudio, bitrate);
            Emit(LogLevel.Info, "Core", $"Processando vídeo (velocidade {Math.Clamp(v.Speed, 0.1, 8.0):0.##}x)...", request.Url);
            var code = await _runner.RunAsync(ffmpeg, passArgs, OnLine, OnLine, workDir, ct).ConfigureAwait(false);

            if (code == 0 && File.Exists(outMp4))
            {
                try { File.Delete(src); } catch { /* ignore */ }
                var finalMp4 = Path.ChangeExtension(src, ".mp4");
                try { if (File.Exists(finalMp4)) File.Delete(finalMp4); } catch { /* ignore */ }
                File.Move(outMp4, finalMp4);
                Emit(LogLevel.Info, "Core", "Velocidade aplicada.", request.Url);
            }
            else
            {
                try { if (File.Exists(outMp4)) File.Delete(outMp4); } catch { /* ignore */ }
                Emit(LogLevel.Warning, "Core", $"ffmpeg falhou (código {code}); entregando o vídeo sem processar.", request.Url);
            }
        }

        // Move the final outputs to the real output dir, preserving the template's relative subfolders.
        string? mainPath = null;
        foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
                     .Where(f => DeliverExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            var dest = Path.Combine(request.OutputDirectory, Path.GetRelativePath(workDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
            File.Move(file, dest);
            // The "main" delivered file is the gif (if any), else the video (prefer mp4 over a
            // fallback container); mp3 is secondary.
            var ext = Path.GetExtension(dest).ToLowerInvariant();
            if (ext == ".gif" || (VideoExtensions.Contains(ext) && (mainPath is null || ext == ".mp4")))
                mainPath = dest;
        }
        return mainPath;
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

                var req = new DownloadRequest(url, request.OutputDirectory, request.OutputTemplate, request.Audio, request.Advanced) { Video = request.Video };
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
    /// Hidden staging folder (next to the executable) where yt-dlp keeps all in-progress
    /// junk — <c>.part</c> fragments, raw <c>.webm</c>/<c>.webp</c>, pre-embed thumbnails. Only the
    /// finished file is moved out to the output directory.
    /// </summary>
    internal static string GetStagingDir()
        => Path.Combine(AppContext.BaseDirectory, ".~downloads");

    private void PrepareStagingDir()
    {
        try
        {
            var di = Directory.CreateDirectory(GetStagingDir());
            if (!di.Attributes.HasFlag(FileAttributes.Hidden))
                di.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Warning, "Core", $"Não foi possível preparar a pasta oculta de staging: {ex.Message}");
        }
    }

    private List<string> BuildDownloadArgs(DownloadRequest r, string? workDir = null)
    {
        var bitrate = r.Audio.Bitrate.ToUpperInvariant().Trim(); // "192k" -> "192K"
        var args = new List<string>();
        // When a post-download ffmpeg pass is needed, everything lands in our private work folder
        // (home == temp), so we can locate the real files on disk and deliver them ourselves.
        var homeDir = workDir ?? r.OutputDirectory;
        var tempDir = workDir ?? GetStagingDir();

        if (r.Video.DownloadVideo)
        {
            // We must download an audio stream if the video keeps it OR if we extract a separate MP3.
            var downloadAudio = r.Video.IncludeAudio || r.Video.ExtractAudioSeparate;

            // Prefer mp4-native (H.264/AAC) streams so the output plays everywhere without a re-encode.
            args.Add("-f"); args.Add(downloadAudio
                ? "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b"
                : "bv[ext=mp4]/bv/b");
            args.Add("--merge-output-format"); args.Add("mp4");

            // Speed changes and the "silent video + separate MP3" combo are applied by our own ffmpeg
            // pass after the download — yt-dlp's recode silently no-ops on an already-mp4 file, so its
            // filters never run. yt-dlp only extracts the separate MP3 itself when no post-pass is needed.
            if (!VideoNeedsFfmpegPass(r.Video) && r.Video.ExtractAudioSeparate)
            {
                args.Add("-x");
                args.Add("--audio-format"); args.Add("mp3");
                args.Add("--audio-quality"); args.Add(bitrate);
                args.Add("-k");   // keep the video file too
            }

            // Time-window trim: download only this section instead of the whole video.
            // --force-keyframes-at-cuts makes yt-dlp re-encode around the cut points so the
            // boundaries land on the exact requested time instead of the nearest keyframe.
            if (r.Video.StartTime is not null || r.Video.EndTime is not null)
            {
                var start = r.Video.StartTime?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? "0";
                var end = r.Video.EndTime?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) ?? "inf";
                args.Add("--download-sections"); args.Add($"*{start}-{end}");
                args.Add("--force-keyframes-at-cuts");
            }
        }
        else
        {
            // Audio-only: extract to MP3 (the app's original behavior).
            args.Add("-x");
            args.Add("--audio-format"); args.Add("mp3");
            args.Add("--audio-quality"); args.Add(bitrate);
            args.Add("-f"); args.Add("bestaudio/best");
        }

        args.AddRange(
        [
            "-o", r.OutputTemplate,                         // relative template (may include subfolders)
            "-P", $"home:{homeDir}",                        // final files land here...
            "-P", $"temp:{tempDir}",                        // ...intermediate junk stays hidden, next to the .exe
            "--newline",            // emit progress on its own lines (cleaner parsing)
            "--no-mtime",
            "--retries", r.Advanced.Retries.ToString(),
            "--socket-timeout", r.Advanced.TimeoutSeconds.ToString(),
        ]);

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

    /// <summary>True when the video download needs a post-download ffmpeg pass: a GIF conversion,
    /// a speed change, or the "silent video + separate MP3" combo where the audio must be stripped
    /// from the kept video.</summary>
    internal static bool VideoNeedsFfmpegPass(VideoOptions v)
        => v.DownloadVideo && (v.ExtractGif || SpeedChanged(v.Speed) || (v.ExtractAudioSeparate && !v.IncludeAudio));

    private static bool SpeedChanged(double speed) => Math.Abs(Math.Clamp(speed, 0.1, 8.0) - 1.0) > 0.001;

    /// <summary>
    /// Builds our own ffmpeg command (run after yt-dlp finishes) that rewrites the downloaded video.
    /// We don't rely on yt-dlp's recode — it no-ops when the file is already mp4, so its filters never run.
    /// We map only the first video/audio stream (ignoring any cover-art stream); a speed change uses
    /// <c>setpts</c> (video PTS multiplier = 1/speed) and <c>atempo</c> (audio tempo, chained for factors
    /// outside [0.5, 100], e.g. 0.1x = 0.5×0.5×0.4). When the video shouldn't keep audio we drop it with
    /// <c>-an</c>; with no speed change the video stream is copied losslessly.
    /// </summary>
    internal static string[] BuildSpeedPassArgs(string input, string output, double speed, bool includeAudio, string audioBitrate)
    {
        var speedChange = SpeedChanged(speed);
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-map_metadata", "0", "-map", "0:v:0" };

        if (includeAudio) { a.Add("-map"); a.Add("0:a:0?"); }
        else a.Add("-an");

        if (speedChange)
        {
            var pts = (1.0 / Math.Clamp(speed, 0.1, 8.0)).ToString("0.######", CultureInfo.InvariantCulture);
            a.Add("-filter:v"); a.Add($"setpts={pts}*PTS");
            a.Add("-c:v"); a.Add("libx264"); a.Add("-preset"); a.Add("veryfast"); a.Add("-crf"); a.Add("20");
        }
        else { a.Add("-c:v"); a.Add("copy"); }

        if (includeAudio)
        {
            if (speedChange) { a.Add("-filter:a"); a.Add(AtempoChain(Math.Clamp(speed, 0.1, 8.0))); a.Add("-c:a"); a.Add("aac"); a.Add("-b:a"); a.Add(audioBitrate); }
            else { a.Add("-c:a"); a.Add("copy"); }
        }

        a.Add(output);
        return [.. a];
    }

    /// <summary>
    /// Two-pass palette-based GIF conversion (ffmpeg's standard recipe for quality output): first
    /// builds an optimized color palette for the clip, then re-encodes using it. fps/width are fixed
    /// at sane defaults for a short animated clip. Audio is never mapped — a GIF has no audio track.
    /// </summary>
    internal static string[] BuildGifPaletteArgs(string input, string palettePath, int fps = 12, int width = 480)
        =>
        [
            "-hide_banner", "-loglevel", "error", "-y", "-i", input,
            "-vf", $"fps={fps},scale={width}:-1:flags=lanczos,palettegen",
            palettePath
        ];

    internal static string[] BuildGifEncodeArgs(string input, string palettePath, string output, int fps = 12, int width = 480)
        =>
        [
            "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-i", palettePath,
            "-filter_complex", $"fps={fps},scale={width}:-1:flags=lanczos[x];[x][1:v]paletteuse",
            output
        ];

    private static string AtempoChain(double speed)
    {
        var factors = new List<double>();
        var remaining = speed;
        while (remaining < 0.5) { factors.Add(0.5); remaining /= 0.5; }   // slowdowns below 0.5x
        while (remaining > 100.0) { factors.Add(100.0); remaining /= 100.0; }  // (unreachable here)
        factors.Add(remaining);
        return string.Join(",", factors.Select(f => "atempo=" + f.ToString("0.######", CultureInfo.InvariantCulture)));
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
