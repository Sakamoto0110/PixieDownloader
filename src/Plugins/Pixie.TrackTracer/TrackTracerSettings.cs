using System.IO;
using System.Text.Json;

namespace Pixie.TrackTracer;

/// <summary>
/// The plugin's own knobs, in <c>data/tracktracer/settings.json</c> — the host has no settings API for
/// plugins and one number does not justify asking for one. Missing or unreadable file = defaults; every
/// change is written whole (temp + rename).
/// </summary>
public sealed class TrackTracerSettings
{
    public const int DefaultMaxDepth = 3;
    public const int MinDepth = 1;
    public const int MaxDepthLimit = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string? _path;

    /// <summary>How deep the tracklist tree may be expanded (root = 0). Clamped to 1..6.</summary>
    public int MaxDepth { get; set; } = DefaultMaxDepth;

    public static TrackTracerSettings Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "settings.json");
        TrackTracerSettings? settings = null;
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<TrackTracerSettings>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // defaults; the next Save replaces the file
        }
        settings ??= new TrackTracerSettings();
        settings.MaxDepth = Math.Clamp(settings.MaxDepth, MinDepth, MaxDepthLimit);
        settings._path = path;
        return settings;
    }

    public void Save()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a setting that did not persist is a setting for this session only
        }
    }
}
