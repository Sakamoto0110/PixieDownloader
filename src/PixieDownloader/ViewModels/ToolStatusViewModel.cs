using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>Status-bar indicator for a single external binary (yt-dlp / ffmpeg).</summary>
public sealed partial class ToolStatusViewModel : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlyphColorKey))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _installed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string? _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string? _error;

    [ObservableProperty]
    private bool _isWorking;

    public ToolStatusViewModel(string name) => Name = name;

    public string GlyphColorKey => Installed ? "Brush.Success" : "Brush.Error";

    public string Tooltip => Installed
        ? $"{Name} {Version}".Trim()
        : $"{Name}: não instalado{(string.IsNullOrEmpty(Error) ? "" : $" — {Error}")}";

    public void Update(ToolStatus status)
    {
        // Green only when the binary exists and reported a usable version.
        Installed = status.Installed && status.Version is not null;
        Version = status.Version;
        Error = status.Error;
    }
}
