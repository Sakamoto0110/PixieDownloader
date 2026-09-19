using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using YtDlpCore;

namespace Pixie.Library.Player;

/// <summary>Where the install is, for the button: "baixando 37%", "conferindo", "extraindo 80%", "preparando".</summary>
internal readonly record struct VlcProgress(string Phase, double? Percent)
{
    public override string ToString() => Percent is { } p ? $"{Phase} {p:0}%" : Phase;
}

/// <summary>
/// Fetches the portable VLC into <c>tools\vlc\</c> next to the executable — the same <c>tools\</c> the app keeps
/// yt-dlp and ffmpeg in. "Portable" is VideoLAN's own zip of the release (no installer, nothing written to the
/// registry or Program Files): <c>vlc.exe</c> plus <c>libvlc</c> and the <c>plugins\</c> folder it cannot run
/// without, so there is no single-exe VLC to fetch instead. The version comes from the endpoint VLC itself asks
/// (<see cref="StatusUrl"/>), the zip from download.videolan.org with its published SHA-256 checked, and what
/// lands on disk is the archive minus what this machine will never use (<see cref="Keep"/>): 80 MB down, about
/// 140 of the 183 MB on disk. Extracted next to the target and swapped in by rename, so a failed or cancelled
/// install leaves whatever was there before.
/// </summary>
internal sealed class VlcInstaller
{
    /// <summary>What VLC itself asks to learn the current version: the first line is the version, the second the installer's URL.</summary>
    public const string StatusUrl = "https://update.videolan.org/vlc/status-win-x64";

    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+){1,3}$", RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Action<LogLevel, string> _log;

    public VlcInstaller(HttpClient http, string toolsDirectory, Action<LogLevel, string> log)
    {
        _http = http;
        ToolsDirectory = toolsDirectory;
        _log = log;
    }

    public string ToolsDirectory { get; }
    public string Directory => Path.Combine(ToolsDirectory, "vlc");
    public string ExePath => Path.Combine(Directory, "vlc.exe");
    public bool IsInstalled => File.Exists(ExePath);

    /// <summary>The UI language VLC's translations are kept for — Windows' one; a test sets it.</summary>
    public CultureInfo UiCulture { get; set; } = CultureInfo.CurrentUICulture;

    /// <summary>"3.0.23" from the exe's version resource, or <see langword="null"/> when there is none to read.</summary>
    public string? InstalledVersion
    {
        get
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(ExePath);
                var v = info.ProductVersion ?? info.FileVersion;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static Uri ArchiveUrl(string version) => new($"https://download.videolan.org/pub/videolan/vlc/{version}/win64/vlc-{version}-win64.zip");

    /// <summary>Downloads, checks, extracts and swaps the new folder in. Returns the version installed.</summary>
    public async Task<string> InstallAsync(IProgress<VlcProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("procurando a versão", null));
        var version = ParseVersion(await _http.GetStringAsync(StatusUrl, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException("não entendi a resposta do update.videolan.org");
        var archive = ArchiveUrl(version);
        var expected = ParseSha256(await _http.GetStringAsync(archive + ".sha256", ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException($"o checksum de vlc-{version}-win64.zip não veio como esperado");
        _log(LogLevel.Info, $"baixando o VLC {version} ({archive})");

        System.IO.Directory.CreateDirectory(ToolsDirectory);
        foreach (var stale in System.IO.Directory.GetDirectories(ToolsDirectory, ".~vlc-*"))
            TryDeleteDirectory(stale);   // a previous attempt the app did not outlive
        var work = Path.Combine(ToolsDirectory, ".~vlc-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(work);
            var zip = Path.Combine(work, "vlc.zip");
            var actual = await DownloadAsync(archive, zip, progress, ct).ConfigureAwait(false);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"o SHA-256 do zip não bate com o publicado (esperado {expected[..12]}…, veio {actual[..12]}…)");

            var extracted = Path.Combine(work, "vlc");
            await Task.Run(() => Extract(zip, extracted, UiCulture, progress, ct), ct).ConfigureAwait(false);
            File.Delete(zip);

            ct.ThrowIfCancellationRequested();
            progress?.Report(new("preparando", null));
            await Task.Run(() => GeneratePluginCache(extracted), ct).ConfigureAwait(false);
            Swap(extracted);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
        return version;
    }

    /// <summary>The version on the first line of the status text, or <see langword="null"/> if it does not look like one.</summary>
    internal static string? ParseVersion(string status)
    {
        var first = status.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return first is not null && VersionPattern.IsMatch(first) ? first : null;
    }

    /// <summary>The 64 hex digits of a <c>.sha256</c> file ("&lt;hash&gt; *&lt;name&gt;"), or <see langword="null"/>.</summary>
    internal static string? ParseSha256(string text)
    {
        var m = Sha256Pattern.Match(text);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Streams the archive to disk, hashing as it goes; a connection that goes quiet for a minute is given up on.</summary>
    private async Task<string> DownloadAsync(Uri url, string destination, IProgress<VlcProgress>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total is > 0)
                {
                    var percent = (int)(done * 100 / total.Value);
                    if (percent != lastReport)
                    {
                        lastReport = percent;
                        progress?.Report(new("baixando", percent));
                    }
                }
                else
                {
                    progress?.Report(new($"baixando {done >> 20} MB", null));
                }
            }
            progress?.Report(new("conferindo", null));
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("o download parou de responder");
        }
    }

    /// <summary>
    /// Unpacks the archive's single top folder into <paramref name="destination"/>, keeping what <see cref="Keep"/>
    /// says. Refuses an archive that is not shaped like VLC's (no single top folder, an entry escaping it, or
    /// no <c>vlc.exe</c>/<c>libvlc.dll</c>/<c>plugins\</c> at the end).
    /// </summary>
    internal static void Extract(string zipPath, string destination, CultureInfo uiCulture, IProgress<VlcProgress>? progress, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        var tops = files.Select(e => e.FullName.Replace('\\', '/').Split('/', 2)).Select(p => p.Length == 2 ? p[0] : "").Distinct().ToList();
        if (tops.Count != 1 || tops[0].Length == 0)
            throw new InvalidOperationException("o zip não tem a forma do VLC (uma pasta só na raiz)");
        var top = tops[0] + "/";

        System.IO.Directory.CreateDirectory(destination);
        var full = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var done = 0;
        var lastReport = -1;
        foreach (var entry in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/')[top.Length..];
            if (Keep(relative, uiCulture))
            {
                var target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"o zip tem uma entrada fora da pasta ({entry.FullName})");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            done++;
            var percent = done * 100 / files.Count;
            if (percent != lastReport)
            {
                lastReport = percent;
                progress?.Report(new("extraindo", percent));
            }
        }

        foreach (var required in new[] { "vlc.exe", "libvlc.dll", "libvlccore.dll" })
        {
            if (!File.Exists(Path.Combine(destination, required)))
                throw new InvalidOperationException($"o zip não trouxe {required}");
        }
        if (!System.IO.Directory.Exists(Path.Combine(destination, "plugins")))
            throw new InvalidOperationException("o zip não trouxe a pasta plugins");
    }

    /// <summary>
    /// What of the archive lands on disk. Out: the MSI helpers, the browser plugins (ActiveX, NPAPI — nothing
    /// loads those any more) and the translations of every other language: VLC's UI follows Windows, so this
    /// machine's language (<c>pt_BR</c>, plus a bare <c>pt</c> when the archive has one) is all it will read,
    /// and the rest is 40 of the 183 MB.
    /// </summary>
    internal static bool Keep(string relativePath, CultureInfo uiCulture)
    {
        var parts = relativePath.Split('/', '\\');
        var first = parts[0];
        if (parts.Length == 1)
            return !first.Equals("axvlc.dll", StringComparison.OrdinalIgnoreCase) && !first.Equals("npvlc.dll", StringComparison.OrdinalIgnoreCase);
        if (first.Equals("msi", StringComparison.OrdinalIgnoreCase))
            return false;
        if (first.Equals("locale", StringComparison.OrdinalIgnoreCase))
            return LocaleNames(uiCulture).Contains(parts[1], StringComparer.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>gettext folder names for a culture: "pt_BR" and "pt" for pt-BR; nothing for the invariant culture (VLC's own language is English).</summary>
    internal static IReadOnlyList<string> LocaleNames(CultureInfo culture)
    {
        if (string.IsNullOrEmpty(culture.Name))
            return [];
        var names = new List<string> { culture.Name.Replace('-', '_') };
        if (!culture.IsNeutralCulture && !string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
            names.Add(culture.TwoLetterISOLanguageName);
        return names;
    }

    /// <summary>
    /// What VLC's installer does at the end: writes <c>plugins\plugins.dat</c> so vlc.exe does not scan its 400
    /// plugin DLLs on every start. Runs the release's own <c>vlc-cache-gen.exe</c> (checksum verified with the
    /// rest); a failure only costs that second at each start, so it is logged, not fatal.
    /// </summary>
    private void GeneratePluginCache(string directory)
    {
        var tool = Path.Combine(directory, "vlc-cache-gen.exe");
        if (!File.Exists(tool))
            return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool)
            {
                ArgumentList = { Path.Combine(directory, "plugins") },
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return;
            if (!process.WaitForExit(90_000))
            {
                process.Kill();
                _log(LogLevel.Debug, "vlc-cache-gen não terminou em 90 s; o VLC vai montar o cache sozinho");
            }
            else if (process.ExitCode != 0)
            {
                _log(LogLevel.Debug, $"vlc-cache-gen saiu com {process.ExitCode}; o VLC vai montar o cache sozinho");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log(LogLevel.Debug, $"vlc-cache-gen não rodou ({ex.Message}); o VLC vai montar o cache sozinho");
        }
    }

    /// <summary>The new folder takes the old one's place by rename; the old one goes when nothing holds it.</summary>
    private void Swap(string extracted)
    {
        string? old = null;
        if (System.IO.Directory.Exists(Directory))
        {
            old = Path.Combine(ToolsDirectory, ".~vlc-old-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.Move(Directory, old);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"a pasta {Directory} está em uso — feche o VLC e tente de novo ({ex.Message})");
            }
        }
        System.IO.Directory.Move(extracted, Directory);
        if (old is not null)
            TryDeleteDirectory(old);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next install to try again; it is inside tools\ and starts with ".~".
        }
    }
}
