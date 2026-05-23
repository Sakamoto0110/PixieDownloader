using System.Diagnostics;
using System.Text;

namespace YtDlpCore;

/// <summary>
/// Async wrapper around <see cref="Process"/>. Streams stdout/stderr line by line
/// (splitting on both <c>\n</c> and <c>\r</c> so yt-dlp progress updates surface live),
/// and kills the entire child process tree on cancellation.
/// </summary>
public sealed class YtDlpProcessRunner
{
    /// <summary>
    /// Runs <paramref name="exePath"/> with the given args. Output is delivered through the
    /// callbacks as soon as a line terminator is seen. Returns the process exit code.
    /// Throws <see cref="OperationCanceledException"/> if cancelled (process is killed first).
    /// </summary>
    public async Task<int> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string>? onStdout,
        Action<string>? onStderr,
        string? workingDirectory,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? AppContext.BaseDirectory : workingDirectory,
        };

        // ArgumentList escapes each argument individually — no shell, no metachar injection.
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {exePath}");

        // Cancellation immediately kills the whole tree (yt-dlp may spawn ffmpeg).
        await using var killReg = ct.Register(static state =>
        {
            var p = (Process)state!;
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
        }, process);

        // Pump with CancellationToken.None: on cancel the kill above causes EOF, draining naturally.
        var stdoutTask = PumpAsync(process.StandardOutput, onStdout);
        var stderrTask = PumpAsync(process.StandardError, onStderr);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { /* swallow pump faults during teardown */ }
        }

        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>Runs the process and captures full stdout/stderr into strings.</summary>
    public async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string exePath,
        IReadOnlyList<string> args,
        CancellationToken ct,
        string? workingDirectory = null)
    {
        var so = new StringBuilder();
        var se = new StringBuilder();
        var soLock = new object();
        var seLock = new object();

        int code = await RunAsync(
            exePath,
            args,
            line => { lock (soLock) so.AppendLine(line); },
            line => { lock (seLock) se.AppendLine(line); },
            workingDirectory,
            ct).ConfigureAwait(false);

        return (code, so.ToString(), se.ToString());
    }

    private static async Task PumpAsync(TextReader reader, Action<string>? onLine)
    {
        if (onLine is null)
        {
            // Still drain to avoid blocking the child on a full pipe buffer.
            var sink = new char[8192];
            while (await reader.ReadAsync(sink.AsMemory()).ConfigureAwait(false) > 0) { }
            return;
        }

        var buffer = new char[8192];
        var sb = new StringBuilder(256);
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                char c = buffer[i];
                if (c == '\n' || c == '\r')
                {
                    if (sb.Length > 0)
                    {
                        onLine(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        if (sb.Length > 0)
            onLine(sb.ToString());
    }
}
