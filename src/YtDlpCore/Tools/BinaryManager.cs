using System.IO.Compression;
using System.Security.Cryptography;

namespace YtDlpCore;

/// <summary>
/// Detects, downloads and updates the yt-dlp and ffmpeg binaries. Everything lives in
/// <c>./tools/</c> relative to <see cref="AppContext.BaseDirectory"/> — no hardcoded paths.
/// </summary>
public sealed class BinaryManager
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpChecksumUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private const string FfmpegFullUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string FfmpegMinimalUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-lgpl-shared.zip";

    private readonly HttpClient _http;
    private readonly YtDlpProcessRunner _runner;
    private readonly Action<LogEntry>? _log;

    public BinaryManager(HttpClient http, YtDlpProcessRunner runner, Action<LogEntry>? log = null, string? toolsDirectory = null)
    {
        _http = http;
        _runner = runner;
        _log = log;
        ToolsDirectory = toolsDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools");
    }

    public string ToolsDirectory { get; }
    public string YtDlpPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(ToolsDirectory, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(ToolsDirectory, "ffprobe.exe");

    /// <summary>Returns the resolved yt-dlp path (./tools first, then PATH), or null.</summary>
    public string? ResolveYtDlpPath() => File.Exists(YtDlpPath) ? YtDlpPath : FindInPath("yt-dlp.exe");

    /// <summary>Returns the resolved ffmpeg path (./tools first, then PATH), or null.</summary>
    public string? ResolveFfmpegPath() => File.Exists(FfmpegPath) ? FfmpegPath : FindInPath("ffmpeg.exe");

    public async Task<ToolStatus> CheckYtDlpAsync(CancellationToken ct = default)
    {
        var path = ResolveYtDlpPath();
        if (path is null)
            return ToolStatus.Missing("yt-dlp.exe não encontrado em ./tools ou no PATH.");
        try
        {
            var (code, so, se) = await _runner.RunCapturedAsync(path, ["--version"], ct).ConfigureAwait(false);
            return code == 0
                ? new ToolStatus(true, path, so.Trim(), null)
                : new ToolStatus(true, path, null, se.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolStatus(true, path, null, ex.Message);
        }
    }

    public async Task<ToolStatus> CheckFfmpegAsync(CancellationToken ct = default)
    {
        var path = ResolveFfmpegPath();
        if (path is null)
            return ToolStatus.Missing("ffmpeg.exe não encontrado em ./tools ou no PATH.");
        try
        {
            var (code, so, se) = await _runner.RunCapturedAsync(path, ["-version"], ct).ConfigureAwait(false);
            var line = (so + "\n" + se)
                .Split('\n')
                .FirstOrDefault(l => l.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase))?.Trim();
            string? version = line;
            const string prefix = "ffmpeg version ";
            if (line is not null && line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                version = line[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return new ToolStatus(true, path, version, code == 0 ? null : se.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolStatus(true, path, null, ex.Message);
        }
    }

    public async Task DownloadYtDlpAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var tmp = YtDlpPath + ".download";
        Log(LogLevel.Info, "Baixando yt-dlp...");
        await DownloadFileWithProgressAsync(YtDlpUrl, tmp, progress, ct).ConfigureAwait(false);

        if (!await VerifyYtDlpChecksumAsync(tmp, ct).ConfigureAwait(false))
        {
            TryDelete(tmp);
            throw new InvalidOperationException("Checksum do yt-dlp.exe não confere — download abortado.");
        }

        File.Move(tmp, YtDlpPath, overwrite: true);
        Log(LogLevel.Info, $"yt-dlp instalado em {YtDlpPath}");
    }

    /// <summary>Runtime update: download to a temp file then atomic-replace the in-place binary.</summary>
    public Task UpdateYtDlpAsync(IProgress<double>? progress, CancellationToken ct)
        => DownloadYtDlpAsync(progress, ct);

    public async Task DownloadFfmpegAsync(FfmpegInstallKind kind, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var url = kind == FfmpegInstallKind.Full ? FfmpegFullUrl : FfmpegMinimalUrl;
        var tmpZip = Path.Combine(ToolsDirectory, "ffmpeg-download.zip");
        Log(LogLevel.Info, $"Baixando ffmpeg ({kind})...");

        // Download is the bulk of the work: map to 0..90%, extraction 90..100%.
        IProgress<double>? dlProgress = progress is null ? null : new Progress<double>(p => progress.Report(p * 0.9));
        await DownloadFileWithProgressAsync(url, tmpZip, dlProgress, ct).ConfigureAwait(false);

        await ExtractBinFolderAsync(tmpZip, ToolsDirectory, ct).ConfigureAwait(false);
        TryDelete(tmpZip);
        progress?.Report(100);
        Log(LogLevel.Info, $"ffmpeg instalado em {ToolsDirectory}");
    }

    private async Task DownloadFileWithProgressAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;
            if (total is > 0)
                progress?.Report(readTotal * 100.0 / total.Value);
        }
        progress?.Report(100);
    }

    /// <summary>
    /// Extracts every file found inside a <c>.../bin/</c> folder of the archive into <paramref name="toolsDir"/>
    /// (flattened). Covers both the full essentials build and the shared LGPL build (which needs its DLLs).
    /// </summary>
    private static async Task ExtractBinFolderAsync(string zipPath, string toolsDir, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        bool extractedExe = false;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var norm = entry.FullName.Replace('\\', '/');
            if (norm.EndsWith('/'))
                continue; // directory entry

            if (norm.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) < 0)
                continue; // only the bin/ payload

            var name = Path.GetFileName(norm);
            if (string.IsNullOrEmpty(name))
                continue;

            var dest = Path.Combine(toolsDir, name);
            await using (var es = entry.Open())
            await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await es.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            if (name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                extractedExe = true;
        }

        if (!extractedExe)
            throw new InvalidOperationException("O arquivo do ffmpeg não continha ffmpeg.exe em uma pasta bin/.");
    }

    private async Task<bool> VerifyYtDlpChecksumAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var sums = await _http.GetStringAsync(YtDlpChecksumUrl, ct).ConfigureAwait(false);
            string? expected = null;
            foreach (var raw in sums.Split('\n'))
            {
                var line = raw.Trim();
                if (line.EndsWith("yt-dlp.exe", StringComparison.OrdinalIgnoreCase))
                {
                    expected = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    break;
                }
            }
            if (string.IsNullOrEmpty(expected))
                return true; // no published entry — accept

            await using var fs = File.OpenRead(filePath);
            var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            var actual = Convert.ToHexStringLower(hash);
            var ok = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            if (!ok)
                Log(LogLevel.Warning, "Checksum do yt-dlp não confere.");
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(LogLevel.Warning, $"Não foi possível verificar checksum do yt-dlp: {ex.Message}");
            return true; // best-effort: don't block install on a checksum fetch failure
        }
    }

    private static string? FindInPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full))
                    return full;
            }
            catch { /* malformed PATH segment */ }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void Log(LogLevel level, string message) => _log?.Invoke(LogEntry.Now(level, "Core", message));
}
