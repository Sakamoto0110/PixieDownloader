using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pixie.Library.Player;

/// <summary>A vlc.exe running now: where its exe is (null when Windows would not say) and whether it takes files from outside.</summary>
internal sealed record RunningVlc(int ProcessId, string? ExePath, bool AcceptsFiles, DateTime StartedAt);

/// <summary>
/// The VLCs running on this desktop. One that takes files from outside is one in one-instance mode: it owns the
/// hidden window VLC's IPC looks for, titled <c>VLC ipc &lt;version&gt;</c>. A VLC started without that mode
/// (from the Start menu, or by an older build of this tab) has none, and nothing else in VLC takes a file from
/// another process.
/// </summary>
internal static class VlcInstances
{
    public const string IpcWindowPrefix = "VLC ipc ";

    public static IReadOnlyList<RunningVlc> FindRunning()
    {
        var processes = Process.GetProcessesByName("vlc");
        try
        {
            var withIpc = WithIpcWindow(processes.Select(p => p.Id).ToHashSet());
            var running = new List<RunningVlc>();
            foreach (var process in processes)
            {
                DateTime started;
                try
                {
                    started = process.StartTime;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    started = DateTime.MinValue;   // an elevated VLC keeps its start time to itself
                }
                running.Add(new RunningVlc(process.Id, ExePath(process.Id), withIpc.Contains(process.Id), started));
            }
            return running;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>Which of <paramref name="processIds"/> own a top-level window titled like VLC's IPC one.</summary>
    internal static HashSet<int> WithIpcWindow(IReadOnlySet<int> processIds)
    {
        var found = new HashSet<int>();
        if (processIds.Count == 0)
            return found;
        var title = new StringBuilder(64);
        EnumWindowsProc visit = (window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processIds.Contains((int)processId))
            {
                title.Clear();
                if (GetWindowTextW(window, title, title.Capacity) > 0 && title.ToString().StartsWith(IpcWindowPrefix, StringComparison.Ordinal))
                    found.Add((int)processId);
            }
            return true;
        };
        EnumWindows(visit, IntPtr.Zero);
        GC.KeepAlive(visit);
        return found;
    }

    /// <summary>The exe of a process, with the least access that answers (an elevated VLC included), or null.</summary>
    internal static string? ExePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var path = new StringBuilder(1024);
            var length = path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref length) ? path.ToString(0, length) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Asks the VLC to quit the way its close button does, and waits. False: still there (a dialog open, only a tray icon).</summary>
    public static bool TryClose(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.CloseMainWindow() && process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;   // it went before we asked
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
