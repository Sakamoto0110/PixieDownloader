using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>Row in the playlist list: a video plus its selection state and lazily-loaded thumbnail.</summary>
public sealed class PlaylistItemViewModel : ObservableObject
{
    public VideoInfo Video { get; }

    /// <summary>1-based position in the original playlist (used for <c>--playlist-items</c>).</summary>
    public int Index { get; }

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private string? _thumbnailPath;
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetProperty(ref _thumbnailPath, value);
    }

    public PlaylistItemViewModel(VideoInfo video, int index)
    {
        Video = video;
        Index = index;
    }

    public string Id => Video.Id;
    public string Title => Video.Title;
    public string? Uploader => Video.Uploader;
    public TimeSpan? Duration => Video.Duration;
    public string? ThumbnailUrl => Video.ThumbnailUrl;
    public string WebpageUrl => Video.WebpageUrl;
    public string IndexLabel => Index.ToString("D2");
}
