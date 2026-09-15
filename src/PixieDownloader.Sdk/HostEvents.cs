using YtDlpCore;

namespace PixieDownloader.Sdk;

/// <summary>An analysis the user ran just finished — the same result the preview panel shows.</summary>
public sealed class AnalysisCompletedEventArgs(string url, UrlInfo info) : EventArgs
{
    /// <summary>What the user typed or pasted.</summary>
    public string Url { get; } = url;

    /// <summary>A <see cref="VideoUrlInfo"/> or a <see cref="PlaylistUrlInfo"/>.</summary>
    public UrlInfo Info { get; } = info;
}

/// <summary>A download job finished writing its final file (the ffmpeg pass included, when there was one).</summary>
public sealed class DownloadCompletedEventArgs(DownloadRequest request, DownloadResult result) : EventArgs
{
    public DownloadRequest Request { get; } = request;

    /// <summary>Always <see cref="DownloadResult.Success"/>; <see cref="DownloadResult.OutputFilePath"/> is the delivered file.</summary>
    public DownloadResult Result { get; } = result;
}
