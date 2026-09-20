using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pixie.Library.Catalog;

/// <summary>What one look at a path found. <see cref="ProbeOutcome.Inaccessible"/> is "there is something, and it would not let me look".</summary>
internal enum ProbeOutcome
{
    Found,
    NotFound,
    Inaccessible,
}

/// <summary>
/// The result of <see cref="IFileProbe.Stat"/>: existence, kind, the identity the scanner and the locators use
/// (size, mtime, and the NTFS volume serial + 128-bit file id when the handle could be opened).
/// </summary>
internal sealed record ProbeResult(ProbeOutcome Outcome, bool IsFolder, long Size, DateTime Modified, string? VolumeSerial, string? FileId)
{
    public static readonly ProbeResult Missing = new(ProbeOutcome.NotFound, false, 0, default, null, null);
    public static readonly ProbeResult Denied = new(ProbeOutcome.Inaccessible, false, 0, default, null, null);
}

/// <summary>
/// Read-only questions to the file system, the only ones the reconciler asks:
/// nothing here writes, moves or deletes. A fake answers them in tests.
/// </summary>
internal interface IFileProbe
{
    ProbeResult Stat(string path);

    /// <summary>The volume with this serial is mounted right now. <paramref name="pathHint"/> is where it used to be, tried first.</summary>
    bool IsVolumeReady(string volumeSerial, string pathHint);

    /// <summary>The root of <paramref name="path"/> (drive letter, share) exists — for locators that never got a serial.</summary>
    bool PathRootExists(string path);

    /// <summary>Where the object with this id lives now on that volume — the same file or folder, renamed or moved — or null.</summary>
    string? FindByFileId(string volumeSerial, string fileId, string pathHint);
}

/// <summary>
/// The real thing, on Win32: <c>GetFileInformationByHandleEx(FileIdInfo)</c> for identity (a handle opened with no
/// access rights, so a file in use still answers) and <c>OpenFileById</c> + <c>GetFinalPathNameByHandle</c> to find
/// an object by id anywhere on its volume (the hint handle is the volume's root directory; no privilege needed).
/// Volume serials are mapped to roots once per instance — make one per reconciliation.
/// </summary>
internal sealed class WindowsFileProbe : IFileProbe
{
    private readonly Dictionary<string, string> _rootBySerial = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rootsTried = new(StringComparer.OrdinalIgnoreCase);

    public ProbeResult Stat(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Exists)
            {
                FileIdentity.TryGet(path, isFolder: true, out var serial, out var id);
                return new ProbeResult(ProbeOutcome.Found, true, 0, directory.LastWriteTimeUtc, serial, id);
            }
            var file = new FileInfo(path);
            if (file.Exists)
            {
                FileIdentity.TryGet(path, isFolder: false, out var serial, out var id);
                return new ProbeResult(ProbeOutcome.Found, false, file.Length, file.LastWriteTimeUtc, serial, id);
            }
            return ProbeResult.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ProbeResult.Denied;
        }
    }

    public bool PathRootExists(string path)
    {
        try
        {
            var root = PathUtil.VolumeRootOf(path);
            return root is not null && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public bool IsVolumeReady(string volumeSerial, string pathHint) => RootOfVolume(volumeSerial, pathHint) is not null;

    public string? FindByFileId(string volumeSerial, string fileId, string pathHint)
    {
        var root = RootOfVolume(volumeSerial, pathHint);
        return root is null ? null : FileIdentity.FindById(root, fileId);
    }

    /// <summary>The mounted root whose serial is <paramref name="volumeSerial"/>: the hint's root first, then every ready drive.</summary>
    private string? RootOfVolume(string volumeSerial, string pathHint)
    {
        if (_rootBySerial.TryGetValue(volumeSerial, out var known))
            return known;
        var candidates = new List<string>();
        try
        {
            if (PathUtil.VolumeRootOf(pathHint) is { } hintRoot)
                candidates.Add(hintRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // a hint that is not a path: nothing to try first
        }
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                    candidates.Add(drive.RootDirectory.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // enumeration failed: the hint alone, then
        }
        foreach (var candidate in candidates)
        {
            if (!_rootsTried.Add(candidate))
                continue;
            try
            {
                if (Directory.Exists(candidate) && FileIdentity.TryGet(candidate, isFolder: true, out var serial, out _) && serial is not null)
                    _rootBySerial.TryAdd(serial, candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a drive that is "ready" but refuses: not this one
            }
        }
        return _rootBySerial.TryGetValue(volumeSerial, out known) ? known : null;
    }
}

/// <summary>The Win32 calls behind <see cref="WindowsFileProbe"/>, also used by the scanner to stamp each listed file.</summary>
internal static class FileIdentity
{
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;              // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;         // needed to open a directory
    private const int FileIdInfoClass = 18;
    private const int ExtendedFileIdType = 2;

    /// <summary>Volume serial (16 hex) and file id (32 hex) of the object at <paramref name="path"/>; false when the handle cannot be opened.</summary>
    public static bool TryGet(string path, bool isFolder, out string? volumeSerial, out string? fileId)
    {
        volumeSerial = null;
        fileId = null;
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, isFolder ? FileFlagBackupSemantics : 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
            return false;
        volumeSerial = info.VolumeSerialNumber.ToString("x16");
        fileId = info.FileId.High.ToString("x16") + info.FileId.Low.ToString("x16");
        return true;
    }

    /// <summary>Opens the object by id on the volume <paramref name="volumeRoot"/> is on and asks Windows for its current path.</summary>
    public static string? FindById(string volumeRoot, string fileId)
    {
        if (fileId.Length != 32
            || !ulong.TryParse(fileId.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out var high)
            || !ulong.TryParse(fileId.AsSpan(16, 16), System.Globalization.NumberStyles.HexNumber, null, out var low))
            return null;
        using var volume = CreateFileW(volumeRoot, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (volume.IsInvalid)
            return null;
        var descriptor = new FileIdDescriptor
        {
            Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
            Type = ExtendedFileIdType,
            ExtendedFileId = new FileId128 { Low = low, High = high },
        };
        using var handle = OpenFileById(volume, ref descriptor, 0, FileShareAll, IntPtr.Zero, FileFlagBackupSemantics);
        if (handle.IsInvalid)
            return null;
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            return null;
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                return null;
        }
        try
        {
            return PathUtil.Normalize(buffer.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public int Type;
        public FileId128 ExtendedFileId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FileIdDescriptor fileId, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint flagsAndAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder filePath, uint length, uint flags);
}
