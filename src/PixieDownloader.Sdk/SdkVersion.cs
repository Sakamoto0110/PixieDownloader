namespace PixieDownloader.Sdk;

/// <summary>
/// The version of this contract — the <c>&lt;Version&gt;</c> of <c>PixieDownloader.Sdk.csproj</c>, which is also the
/// version of the <c>.nupkg</c> — kept apart from the app's version. It covers the public surface of
/// <c>YtDlpCore</c> as well: plugins compile against the copy in the package and bind to the host's at run
/// time, so a change there is a change here even when this assembly is untouched.
/// <para>
/// What a minor may add depends on who implements the type. The host implements <see cref="IPluginHost"/>
/// (and <c>IYtDlpService</c>), so those may grow: a plugin built against 1.0 never calls what 1.1 added. The
/// plugin implements <see cref="IPixiePlugin"/> and <see cref="IUiContribution"/>, so an abstract member added
/// there would fail every existing plugin the moment it is instantiated — the plugin side only grows through a
/// new optional interface (the way <see cref="IUiContribution"/> sits next to <see cref="IPixiePlugin"/>) or a
/// member with a default implementation. Changing or removing anything public, on either side or in the core,
/// is a major.
/// </para>
/// A plugin declares in its manifest the version it was built against (<see cref="PluginManifest.ApiVersion"/>),
/// and the host refuses it before loading any code when the two do not fit.
/// </summary>
public static class SdkVersion
{
    /// <summary>Major.minor of the Sdk this host was built with — read back from the assembly, no second copy.</summary>
    public static Version Current { get; } = ReadCurrent();

    /// <summary>
    /// Same major and a minor no newer than <see cref="Current"/>: a plugin built against 1.0 runs on a 1.2 host,
    /// one built against 1.3 does not, and 2.x never mixes with 1.x.
    /// </summary>
    public static bool IsCompatible(Version apiVersion) =>
        apiVersion.Major == Current.Major && apiVersion.Minor <= Current.Minor;

    /// <summary>
    /// <see cref="IsCompatible(Version)"/> for the manifest's string form. Anything <see cref="Version.TryParse(string?, out Version?)"/>
    /// does not read (<c>"1"</c>, <c>"v1.0"</c>, blank) is incompatible rather than an exception.
    /// </summary>
    public static bool IsCompatible(string? apiVersion) =>
        Version.TryParse(apiVersion, out var parsed) && IsCompatible(parsed);

    private static Version ReadCurrent()
    {
        var v = typeof(SdkVersion).Assembly.GetName().Version ?? new Version(0, 0);
        return new Version(v.Major, v.Minor);
    }
}
