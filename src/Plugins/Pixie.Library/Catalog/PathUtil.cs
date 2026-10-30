using System.IO;

namespace Pixie.Library.Catalog;

/// <summary>
/// The one place where "the same path" and "inside" are decided. Everything that depends on containment —
/// scan plan coverage, which root a file is attributed to, deduplication, the storage root, what a moved
/// folder carries — goes through these, never through a bare <c>StartsWith</c>:
/// <c>C:\Music</c> does not contain <c>C:\Music2</c>, and the separator is what says so.
/// <para>
/// Identity is the <b>canonical</b> form: the full path, <c>\\?\</c> stripped, trailing separator removed
/// (except a volume root), lower-cased with the invariant culture — NTFS is case-insensitive by default, and
/// per-directory case sensitivity is not supported. Short (8.3) names are not expanded and junctions are not
/// resolved: two spellings of one folder through a junction are two identities, by decision.
/// </para>
/// </summary>
internal static class PathUtil
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Full path without a trailing separator, case preserved (the display form). Absolute input expected.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(StripExtendedPrefix(path));
        var trimmed = full.TrimEnd(Separators);
        return trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':') ? full : trimmed;   // keep "C:\" whole
    }

    /// <summary>The identity form: <see cref="Normalize"/> lower-cased invariantly.</summary>
    public static string Canonical(string path) => Normalize(path).ToLowerInvariant();

    /// <summary>
    /// <see cref="Normalize"/> for input that may come from a file (a manifest, a block): refuses a relative path, a
    /// device path (<c>\\.\</c>) and invalid characters instead of guessing. <paramref name="reason"/> says why.
    /// </summary>
    public static bool TryNormalizeAbsolute(string? path, out string normalized, out string reason)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "caminho vazio";
            return false;
        }
        var candidate = StripExtendedPrefix(path.Trim());
        if (candidate.StartsWith(@"\\.\", StringComparison.Ordinal) || candidate.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            reason = "caminho de dispositivo";
            return false;
        }
        if (!Path.IsPathFullyQualified(candidate))
        {
            reason = "caminho relativo";
            return false;
        }
        if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            reason = "caractere inválido";
            return false;
        }
        try
        {
            normalized = Normalize(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = ex.Message;
            return false;
        }
        reason = "";
        return true;
    }

    public static bool IsSamePath(string a, string b)
        => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    /// <summary><paramref name="child"/> is <paramref name="parent"/> itself or somewhere below it.</summary>
    public static bool IsDescendantOrSame(string child, string parent)
    {
        var p = Canonical(parent);
        var c = Canonical(child);
        if (c.Equals(p, StringComparison.Ordinal))
            return true;
        var prefix = p.EndsWith('\\') ? p : p + '\\';
        return c.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Below <paramref name="parent"/>, and not <paramref name="parent"/> itself.</summary>
    public static bool IsProperDescendant(string child, string parent)
        => IsDescendantOrSame(child, parent) && !IsSamePath(child, parent);

    /// <summary>The older name of <see cref="IsDescendantOrSame"/>, kept for its callers.</summary>
    public static bool IsUnder(string path, string directory) => IsDescendantOrSame(path, directory);

    /// <summary>The path of <paramref name="path"/> relative to <paramref name="directory"/>, "" when it is the directory itself.</summary>
    public static string Relative(string path, string directory)
    {
        var rel = Path.GetRelativePath(Normalize(directory), Normalize(path));
        return rel == "." ? "" : rel;
    }

    /// <summary>The volume root (<c>C:\</c>, <c>\\server\share\</c>) of an absolute path, or null when there is none to name.</summary>
    public static string? VolumeRootOf(string path)
    {
        var root = Path.GetPathRoot(Normalize(path));
        return string.IsNullOrEmpty(root) ? null : root;
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 5 && path[5] == ':')
            return path[4..];
        return path;
    }
}
