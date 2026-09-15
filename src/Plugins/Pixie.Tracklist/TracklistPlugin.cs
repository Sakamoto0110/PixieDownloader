using System.IO;
using System.Windows;
using Pixie.Tracklist.Detection;
using Pixie.Tracklist.Model;
using Pixie.Tracklist.Tagging;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Tracklist;

/// <summary>
/// Module 1 of the roadmap. Every single-video analysis the app runs goes through the heuristic detector
/// (description → pinned comment → uploader's comment → most liked → yt-dlp chapters as fallback); the
/// "Tracklist" tab shows the candidates with their counts and lets the user pick one; and when that video
/// downloads as an MP3, the chosen list goes into the file as ID3 chapters plus the full tree. The plugin
/// never touches the host beyond <see cref="IPluginHost"/> and knows no other plugin.
/// </summary>
public sealed class TracklistPlugin : IPixiePlugin, IUiContribution
{
    private IPluginHost _host = null!;
    private readonly TracklistTabViewModel _tab = new();

    // What was found per analysed video, keyed by the URL the download request will carry (the video's
    // webpage URL). A later download of the same video finds its list here.
    private readonly Dictionary<string, AnalysedVideo> _analysed = new(StringComparer.OrdinalIgnoreCase);

    public void Configure(IPluginHost host)
    {
        _host = host;
        host.RequireAnalysisComments();   // the pinned comment is where a tracklist lives when it is not in the description
        host.AnalysisCompleted += OnAnalysisCompleted;
        host.DownloadCompleted += OnDownloadCompleted;
    }

    public string TabHeader => "Tracklist";

    public FrameworkElement CreateView() => new TracklistView { DataContext = _tab };

    private void OnAnalysisCompleted(object? sender, AnalysisCompletedEventArgs e)
    {
        if (e.Info is not VideoUrlInfo { Video: var video })
        {
            _tab.ShowPlaylist();
            return;
        }

        var report = TracklistExtractor.Analyze(video);
        var analysed = new AnalysedVideo(video, report);
        _analysed[video.WebpageUrl] = analysed;
        _tab.Show(analysed);

        _host.Log(LogLevel.Info, report.Best is { } best
            ? $"{best.Entries.Count} faixas — {TracklistReportFormatter.Describe(best)}"
            : $"nenhuma tracklist em \"{video.Title}\"");
    }

    private void OnDownloadCompleted(object? sender, DownloadCompletedEventArgs e)
    {
        if (!_analysed.TryGetValue(e.Request.Url, out var analysed) || !analysed.WriteToFile || analysed.Selected is not { } list)
            return;
        var path = e.Result.OutputFilePath;
        if (path is null)
            return;
        if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            _host.Log(LogLevel.Info, $"tracklist não gravada: por enquanto só em MP3 ({Path.GetFileName(path)})");
            return;
        }

        var payload = TracklistPayload.From(analysed.Video, list);
        var duration = analysed.Video.Duration;
        var token = _host.ShutdownToken;
        // The file is ours to touch now (yt-dlp is done), but tagging a big MP3 can take a moment — off the UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var chapters = Id3ChapterWriter.Write(path, payload, duration);
                _host.Log(LogLevel.Info, chapters > 0
                    ? $"{chapters} capítulos gravados em {Path.GetFileName(path)}"
                    : $"lista gravada em {Path.GetFileName(path)} (sem tempos, sem capítulos)");
            }
            catch (OperationCanceledException)
            {
                // disabled while writing: leave the file as it is
            }
            catch (Exception ex)
            {
                _host.Log(LogLevel.Error, $"não consegui gravar a tracklist em {Path.GetFileName(path)}: {ex.Message}", ex);
            }
        }, CancellationToken.None);
    }
}
