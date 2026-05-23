using CommunityToolkit.Mvvm.ComponentModel;
using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>Row in the playlist list: a video plus its selection state and lazily-loaded thumbnail.</summary>
public sealed partial class PlaylistItemViewModel : ObservableObject
{
    public VideoInfo Video { get; }

    /// <summary>1-based position in the original playlist (used for <c>--playlist-items</c>).</summary>
    public int Index { get; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string? _thumbnailPath;

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
