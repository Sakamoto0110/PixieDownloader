namespace YtDlpCore;

public record RawCommandResult(
    string[] Args,
    int ExitCode,
    string Stdout,
    string Stderr)
{
    public bool Success => ExitCode == 0;
}
