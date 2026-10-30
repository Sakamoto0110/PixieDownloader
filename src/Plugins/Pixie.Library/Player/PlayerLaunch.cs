using System.Diagnostics;
using System.IO;

namespace Pixie.Library.Player;

/// <summary>
/// How a file goes to the chosen player. With VLC, a VLC already running plays it, whichever VLC that is:
/// <list type="bullet">
/// <item>one in one-instance mode (<see cref="VlcInstances"/>) is handed the file through VLC's own IPC: its own
/// exe is started with <c>--one-instance</c>, finds the running one (the IPC is per version, so it has to be the
/// same exe, not the player's), passes the file and exits;</item>
/// <item>one without that mode cannot take a file from outside, so it is closed the way its close button does
/// (its playlist goes with it — the owner's call, 2026-10-02) and its exe is started again with the file and
/// <c>--one-instance</c>, which makes it the one the next file goes to. If it does not close in
/// <see cref="CloseTimeout"/>, a second VLC opens;</item>
/// <item>none running: the player, with <c>--one-instance</c>.</item>
/// </list>
/// <c>--no-playlist-enqueue</c> beats the preference that would queue the file instead of playing it. Any other
/// program gets the path and nothing else.
/// </summary>
internal static class PlayerLaunch
{
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    public static ProcessStartInfo For(string player, string file)
    {
        var start = new ProcessStartInfo(player) { UseShellExecute = false };
        if (IsVlc(player))
        {
            start.ArgumentList.Add("--one-instance");
            start.ArgumentList.Add("--no-playlist-enqueue");
        }
        start.ArgumentList.Add(file);
        return start;
    }

    internal static bool IsVlc(string player) => Path.GetFileName(player).Equals("vlc.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Which exe to start, and which running VLC to close first (null: none). Pure; see the class.</summary>
    internal static (string Exe, RunningVlc? Close) Plan(string player, IReadOnlyList<RunningVlc> running)
    {
        var newestFirst = running.OrderByDescending(r => r.StartedAt).ToList();
        if (newestFirst.FirstOrDefault(r => r.AcceptsFiles) is { } open)
            return (open.ExePath ?? player, null);
        if (newestFirst.FirstOrDefault() is { } closed)
            return (closed.ExePath ?? player, closed);
        return (player, null);
    }

    /// <summary>Opens <paramref name="file"/> in VLC as <see cref="Plan"/> says. Waits up to <see cref="CloseTimeout"/>: off the UI thread. Returns a line for the log.</summary>
    public static string OpenInVlc(string player, string file)
    {
        var running = VlcInstances.FindRunning();
        var (exe, close) = Plan(player, running);
        string what;
        if (close is not null)
        {
            what = VlcInstances.TryClose(close.ProcessId, CloseTimeout)
                ? $"VLC {close.ProcessId} sem one-instance: fechado e reaberto com o arquivo"
                : $"VLC {close.ProcessId} sem one-instance não fechou em {CloseTimeout.TotalSeconds:0} s: abre outro";
        }
        else
        {
            what = running.Count > 0 ? $"entregue ao VLC aberto ({exe})" : $"VLC novo ({exe})";
        }
        Process.Start(For(exe, file))?.Dispose();
        return what;
    }
}
