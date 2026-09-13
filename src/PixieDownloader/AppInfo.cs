using System.Reflection;

namespace PixieDownloader;

/// <summary>
/// What the app knows about itself: the version compiled in (the <c>&lt;Version&gt;</c> of
/// PixieDownloader.csproj, read back from the assembly so the header, tooltips and the yt-dlp service's
/// User-Agent all agree without a second copy) and where the source lives.
/// </summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/Sakamoto0110/PixieDownloader";

    /// <summary>"1.4.0+d65edb7…" — the informational version, commit hash included when the build had one.</summary>
    public static string FullVersion { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>"1.4.0" — what the UI shows and the User-Agent carries.</summary>
    public static string Version { get; } = FullVersion.Split('+', 2)[0];

    /// <summary>"v1.4.0" for the header badge.</summary>
    public static string VersionLabel { get; } = "v" + Version;

    /// <summary>"PixieDownloader 1.4.0" for the badge's tooltip.</summary>
    public static string VersionDetail { get; } = $"PixieDownloader {Version}";
}
