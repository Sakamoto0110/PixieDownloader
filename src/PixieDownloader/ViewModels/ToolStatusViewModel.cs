using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>Status-bar indicator for a single external binary (yt-dlp / ffmpeg).</summary>
public sealed class ToolStatusViewModel : ObservableObject
{
    public string Name { get; }

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set
        {
            if (SetProperty(ref _installed, value))
            {
                OnPropertyChanged(nameof(GlyphColorKey));
                OnPropertyChanged(nameof(Tooltip));
            }
        }
    }

    private string? _version;
    public string? Version
    {
        get => _version;
        set
        {
            if (SetProperty(ref _version, value))
                OnPropertyChanged(nameof(Tooltip));
        }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(Tooltip));
        }
    }

    private bool _isWorking;
    public bool IsWorking
    {
        get => _isWorking;
        set => SetProperty(ref _isWorking, value);
    }

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
