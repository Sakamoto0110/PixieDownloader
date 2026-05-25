using System.Diagnostics;
using System.Globalization;
using YtDlpCore;

// PixieDownloader smoke-test harness.
//   A) Offline: generate a synthetic clip with ffmpeg, run the REAL speed pass (YtDlpService.BuildSpeedPassArgs),
//      and verify the output duration / audio presence with ffprobe. No network, deterministic.
//   B) Online: if test.txt (repo root) holds a URL, run the full YtDlpService.DownloadAsync at 0.5x and verify
//      the delivered mp4's duration ≈ original / speed. Skipped when test.txt is empty.
// Exit code 0 = all passed, 1 = a check failed, 2 = setup error (no ffmpeg/repo).

var inv = CultureInfo.InvariantCulture;
int failures = 0;
void Pass(string m) => Console.WriteLine($"  ✓ {m}");
void Fail(string m) { Console.WriteLine($"  ✗ {m}"); failures++; }

var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
if (repoRoot is null) { Console.Error.WriteLine("ERRO: não achei a raiz do repo (PixieDownloader.slnx)."); return 2; }

var toolsDir = Path.Combine(repoRoot, "src", "PixieDownloader", "bin", "Debug", "net10.0-windows", "tools");
var ffmpeg = Path.Combine(toolsDir, "ffmpeg.exe");
var ffprobe = Path.Combine(toolsDir, "ffprobe.exe");
if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
{
    Console.Error.WriteLine($"ERRO: ffmpeg/ffprobe não encontrados em {toolsDir}. Instale pelo app primeiro.");
    return 2;
}

var work = Path.Combine(Path.GetTempPath(), "pixie-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    Console.WriteLine("=== A) Offline — passo de velocidade (ffmpeg) ===");
    await RunOfflineAsync();

    Console.WriteLine();
    Console.WriteLine("=== B) Online — download real + slowmo ===");
    await RunOnlineAsync();
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch { /* best-effort */ }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "RESULTADO: TODOS PASSARAM" : $"RESULTADO: {failures} FALHA(S)");
return failures == 0 ? 0 : 1;


async Task RunOfflineAsync()
{
    const double baseSeconds = 6.0;
    var src = Path.Combine(work, "src.mp4");
    string[] gen =
    [
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=30:duration={baseSeconds.ToString(inv)}",
        "-f", "lavfi", "-i", $"sine=frequency=440:duration={baseSeconds.ToString(inv)}",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", src,
    ];
    if (await Run(ffmpeg, gen) != 0 || !File.Exists(src)) { Fail("não consegui gerar o vídeo sintético"); return; }
    var baseDur = await ProbeDuration(src);
    Console.WriteLine($"  vídeo base: {baseDur.ToString("0.00", inv)}s");

    foreach (var (speed, audio) in new[] { (0.25, true), (2.0, true), (0.5, false) })
    {
        var label = $"speed={speed.ToString("0.##", inv)}x audio={audio}";
        var outp = Path.Combine(work, $"out_{speed.ToString("0.##", inv)}_{(audio ? "a" : "na")}.mp4");

        // The REAL production arg builder.
        var args = YtDlpService.BuildSpeedPassArgs(src, outp, speed, audio, "192k");
        var code = await Run(ffmpeg, args);
        if (code != 0 || !File.Exists(outp)) { Fail($"{label}: ffmpeg falhou (code {code})"); continue; }

        var dur = await ProbeDuration(outp);
        var expected = baseDur / speed;
        var tol = Math.Max(0.6, expected * 0.05);
        if (Math.Abs(dur - expected) <= tol)
            Pass($"{label}: duração {dur.ToString("0.00", inv)}s ≈ esperado {expected.ToString("0.00", inv)}s");
        else
            Fail($"{label}: duração {dur.ToString("0.00", inv)}s ≠ esperado {expected.ToString("0.00", inv)}s");

        var hasAudio = await HasAudio(outp);
        if (hasAudio == audio) Pass($"{label}: áudio presente={hasAudio} (ok)");
        else Fail($"{label}: áudio presente={hasAudio}, esperado {audio}");
    }
}

async Task RunOnlineAsync()
{
    var testFile = Path.Combine(repoRoot, "test.txt");
    var url = File.Exists(testFile)
        ? File.ReadAllLines(testFile).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
        : null;
    if (string.IsNullOrWhiteSpace(url))
    {
        Console.WriteLine("  (pulado) coloque uma URL de vídeo curta em test.txt para habilitar o teste online.");
        return;
    }

    using var svc = new YtDlpService(logger: null, toolsDirectory: toolsDir, cacheDirectory: Path.Combine(work, "cache"));
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

    double? origDur = null;
    try
    {
        var info = await svc.AnalyzeUrlAsync(url, treatAsPlaylist: false, cts.Token);
        if (info is VideoUrlInfo v)
        {
            origDur = v.Video.Duration?.TotalSeconds;
            Console.WriteLine($"  vídeo: {v.Video.Title} ({origDur?.ToString("0.0", inv) ?? "?"}s)");
        }
    }
    catch (Exception ex) { Fail($"análise da URL falhou: {ex.Message}"); return; }

    const double speed = 0.5;
    var outDir = Path.Combine(work, "out");
    Directory.CreateDirectory(outDir);
    var req = new DownloadRequest(url, outDir, "%(title)s.%(ext)s",
        new AudioOptions(Bitrate: "192k", EmbedThumbnail: false, EmbedMetadata: false),
        new AdvancedOptions())
    {
        Video = new VideoOptions(DownloadVideo: true, IncludeAudio: true, ExtractAudioSeparate: false, Speed: speed),
    };

    DownloadResult res;
    try { res = await svc.DownloadAsync(req, progress: null, cts.Token); }
    catch (Exception ex) { Fail($"download lançou exceção: {ex.Message}"); return; }

    if (!res.Success) { Fail($"download falhou: {res.ErrorMessage}"); return; }

    var mp4 = Directory.EnumerateFiles(outDir, "*.mp4", SearchOption.AllDirectories).FirstOrDefault();
    if (mp4 is null) { Fail("nenhum .mp4 entregue no diretório de saída"); return; }
    Pass($"entregue: {Path.GetFileName(mp4)}");

    var dur = await ProbeDuration(mp4);
    if (origDur is > 0)
    {
        var expected = origDur.Value / speed;
        var tol = Math.Max(1.5, expected * 0.08);
        if (Math.Abs(dur - expected) <= tol)
            Pass($"slowmo {speed.ToString("0.##", inv)}x: duração {dur.ToString("0.0", inv)}s ≈ esperado {expected.ToString("0.0", inv)}s");
        else
            Fail($"slowmo {speed.ToString("0.##", inv)}x: duração {dur.ToString("0.0", inv)}s ≠ esperado {expected.ToString("0.0", inv)}s (orig {origDur.Value.ToString("0.0", inv)}s)");
    }
    else if (dur > 0)
        Pass($"mp4 válido, duração {dur.ToString("0.0", inv)}s (duração original desconhecida — sem comparação de slowmo)");
    else
        Fail("mp4 entregue não tem duração válida");
}

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "PixieDownloader.slnx")))
            return dir.FullName;
    return null;
}

static async Task<int> Run(string exe, string[] args)
{
    var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stderr = p.StandardError.ReadToEndAsync();
    _ = p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    var err = (await stderr).Trim();
    if (p.ExitCode != 0 && err.Length > 0)
        Console.WriteLine("    " + err.Split('\n')[^1].Trim());
    return p.ExitCode;
}

async Task<double> ProbeDuration(string file)
{
    var outp = await Capture(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", file]);
    return double.TryParse(outp, NumberStyles.Float, inv, out var d) ? d : -1;
}

async Task<bool> HasAudio(string file)
{
    var outp = await Capture(ffprobe, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", file]);
    return outp.Length > 0;
}

static async Task<string> Capture(string exe, string[] args)
{
    var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    _ = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    return (await stdout).Trim();
}
