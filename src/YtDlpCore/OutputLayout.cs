using System.Text.Json.Serialization;

namespace YtDlpCore;

/// <summary>
/// The subfolder a download goes to, inside the output folder ("Subpasta"). The default comes first: an
/// unknown value in settings.json reads as it (<see cref="LenientEnumConverter{T}"/>). The UI order is the app's, not this.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<OutputFolder>))]
public enum OutputFolder
{
    Playlist,
    None,
    Channel,
    ChannelPlaylist,
}

/// <summary>How the file is named ("Nome"); the extension is always appended, never part of it. Default first, as above.</summary>
[JsonConverter(typeof(LenientEnumConverter<OutputName>))]
public enum OutputName
{
    IndexTitle,
    Title,
    DateTitle,
    ChannelTitle,
}

/// <summary>
/// The output layout as two independent choices — where (<see cref="OutputFolder"/>) and how it's named
/// (<see cref="OutputName"/>) — or a hand-written yt-dlp template. Either way the result is one yt-dlp
/// template that ends in <see cref="Extension"/>; the per-download substitutions live in <see cref="OutputTemplate"/>.
/// </summary>
public static class OutputLayout
{
    /// <summary>Appended to every template, simple or custom, so the extension can't be forgotten or doubled.</summary>
    public const string Extension = ".%(ext)s";

    public static string FolderPart(OutputFolder folder) => folder switch
    {
        OutputFolder.Playlist => "%(playlist_title)s/",
        OutputFolder.Channel => "%(uploader)s/",
        OutputFolder.ChannelPlaylist => "%(uploader)s/%(playlist_title)s/",
        _ => "",
    };

    public static string NamePart(OutputName name) => name switch
    {
        OutputName.IndexTitle => "%(playlist_index)02d - %(title)s",
        OutputName.DateTitle => "%(upload_date)s - %(title)s",
        OutputName.ChannelTitle => "%(uploader)s - %(title)s",
        _ => "%(title)s",
    };

    /// <summary>The template the two selectors make.</summary>
    public static string Build(OutputFolder folder, OutputName name) => FolderPart(folder) + NamePart(name) + Extension;

    /// <summary>
    /// The template a hand-written one makes: a trailing <c>.%(ext)s</c> and trailing slashes are dropped
    /// before the extension goes back on, and a blank one is just the title.
    /// </summary>
    public static string FromCustom(string? custom)
    {
        var body = WithoutExtension((custom ?? "").Trim()).TrimEnd('/', '\\').Trim();
        return (body.Length == 0 ? "%(title)s" : body) + Extension;
    }

    /// <summary>The template the settings describe: the custom one when it's on, the selectors' otherwise.</summary>
    public static string Compose(OutputFolder folder, OutputName name, bool useCustom, string? custom)
        => useCustom ? FromCustom(custom) : Build(folder, name);

    /// <summary>The template without its trailing <c>.%(ext)s</c> — what the "Personalizar" field shows.</summary>
    public static string WithoutExtension(string template)
        => template.EndsWith(Extension, StringComparison.Ordinal) ? template[..^Extension.Length] : template;

    /// <summary>
    /// Reads an old single-string template (settings from before the two selectors) back into them.
    /// False = no combination makes it, so it stays a custom template.
    /// </summary>
    public static bool TryMatch(string? template, out OutputFolder folder, out OutputName name)
    {
        foreach (var f in Enum.GetValues<OutputFolder>())
        {
            foreach (var n in Enum.GetValues<OutputName>())
            {
                if (string.Equals(Build(f, n), template, StringComparison.Ordinal))
                {
                    (folder, name) = (f, n);
                    return true;
                }
            }
        }
        (folder, name) = (OutputFolder.Playlist, OutputName.IndexTitle);
        return false;
    }
}
