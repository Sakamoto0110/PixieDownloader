using System.IO;
using Microsoft.Win32;

namespace Pixie.Library.Player;

/// <summary>
/// Where a VLC installed on this machine would be: the key VLC's installer writes (<c>HKLM\SOFTWARE\VideoLAN\VLC</c>,
/// value <c>InstallDir</c> — and its 32-bit twin under <c>WOW6432Node</c>), then the two Program Files. The tab
/// asks once per session, when it is first opened; nothing about the answer is persisted, so a VLC installed or
/// removed while the app runs is seen at the next start.
/// </summary>
internal static class VlcLocator
{
    public static string? FindInstalled() => Find(File.Exists, Candidates());

    /// <summary>The first candidate that exists, in the order given.</summary>
    internal static string? Find(Func<string, bool> exists, IEnumerable<string> candidates)
        => candidates.FirstOrDefault(exists);

    /// <summary>Registry first (it says where the installer put it), then the conventional folders.</summary>
    internal static IEnumerable<string> Candidates()
    {
        foreach (var dir in RegistryInstallDirs())
            yield return Path.Combine(dir, "vlc.exe");
        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var programFiles = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "VideoLAN", "VLC", "vlc.exe");
        }
    }

    private static IEnumerable<string> RegistryInstallDirs()
    {
        var dirs = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var key in new[] { @"SOFTWARE\VideoLAN\VLC", @"SOFTWARE\WOW6432Node\VideoLAN\VLC" })
            {
                try
                {
                    using var sub = hive.OpenSubKey(key);
                    if (sub?.GetValue("InstallDir") is string dir && !string.IsNullOrWhiteSpace(dir))
                        dirs.Add(dir.Trim());
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
                {
                    // a key we cannot read is a key that does not point anywhere
                }
            }
        }
        return dirs;
    }
}
