using System.Collections.ObjectModel;
using Pixie.TrackTracer.Detection;
using YtDlpCore;

namespace Pixie.TrackTracer;

/// <summary>
/// What the plugin knows about one analysed video: the detector's report, which block the user picked
/// (the best one until they say otherwise) and whether it goes into the file when this video downloads.
/// Kept per video, keyed by URL, so a download that starts later still finds its list.
/// </summary>
public sealed class AnalysedVideo(VideoInfo video, TracklistReport report)
{
    public VideoInfo Video { get; } = video;
    public TracklistReport Report { get; } = report;
    public Tracklist? Selected { get; set; } = report.Best;
    public bool WriteToFile { get; set; } = report.Best is not null;
}

/// <summary>One of the blocks the detector found, as a selectable source on top of the tab — "Descrição · 24".</summary>
public sealed class SourceOption : ObservableObject
{
    private readonly Action<SourceOption> _select;
    private bool _isSelected;

    public SourceOption(Tracklist list, Action<SourceOption> select)
    {
        List = list;
        _select = select;
        Label = $"{Where(list)} · {list.Entries.Count}";
    }

    public Tracklist List { get; }
    public string Label { get; }
    public string ToolTip => TracklistReportFormatter.Describe(List);

    /// <summary>Two-way from the toggle. Turning one on turns the others off; turning the current one off is ignored (there is always a source).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value)
                _select(this);
            else
                OnPropertyChanged();   // snap the toggle back
        }
    }

    internal void SetSelected(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));

    private static string Where(Tracklist t) => t.Source switch
    {
        TracklistSource.Description => "Descrição",
        TracklistSource.PinnedComment => "Comentário fixado",
        TracklistSource.UploaderComment => "Comentário do canal",
        TracklistSource.TopComment => "Comentário",
        TracklistSource.Chapters => "Capítulos",
        _ => t.Source.ToString(),
    };
}

/// <summary>One track as the tab shows it.</summary>
public sealed record EntryRow(string Number, string Time, string Text, string? Link)
{
    public bool HasLink => Link is not null;
    public bool HasTime => Time.Length > 0;
}

/// <summary>
/// The "Tracklist" tab. The block carries the provenance, not the item: the sources sit on top as toggles
/// with their counts ("Descrição · 24 / Comentário fixado · 18") and switching one swaps the whole list.
/// Problem items stay visible — a track without a title shows as such — because hiding them would make
/// the extraction look cleaner than it was.
/// </summary>
public sealed class TracklistTabViewModel : ObservableObject
{
    private AnalysedVideo? _current;

    public ObservableCollection<SourceOption> Sources { get; } = [];
    public ObservableCollection<EntryRow> Entries { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    private bool _hasVideo;
    public bool HasVideo { get => _hasVideo; private set { if (SetProperty(ref _hasVideo, value)) OnPropertyChanged(nameof(ShowMessage)); } }
    public bool ShowMessage => !HasVideo;

    private string _message = "Analise um vídeo (não uma playlist) e a tracklist da descrição ou dos comentários aparece aqui.";
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    private string _title = "";
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    private string _url = "";
    public string Url { get => _url; private set => SetProperty(ref _url, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private bool _hasList;
    public bool HasList { get => _hasList; private set => SetProperty(ref _hasList, value); }

    private string _reportText = "";
    public string ReportText { get => _reportText; private set => SetProperty(ref _reportText, value); }

    /// <summary>Two-way with the checkbox; remembered on the analysed video so the download hook sees it.</summary>
    public bool WriteToFile
    {
        get => _current?.WriteToFile ?? false;
        set
        {
            if (_current is null || _current.WriteToFile == value)
                return;
            _current.WriteToFile = value;
            OnPropertyChanged();
        }
    }

    /// <summary>A playlist was analysed: nothing to detect (each item would need its own analysis).</summary>
    public void ShowPlaylist()
    {
        _current = null;
        Clear();
        Message = "Playlist analisada — a detecção é por vídeo. Analise um vídeo sozinho pra ver a tracklist dele.";
        HasVideo = false;
    }

    public void Show(AnalysedVideo analysed)
    {
        _current = analysed;
        Clear();
        Title = analysed.Video.Title;
        Url = analysed.Video.WebpageUrl;
        ReportText = TracklistReportFormatter.Format(analysed.Video, analysed.Report);
        foreach (var note in analysed.Report.Notes)
            Notes.Add(note);
        foreach (var candidate in analysed.Report.Candidates)
            Sources.Add(new SourceOption(candidate, Select));

        HasVideo = true;
        var selected = Sources.FirstOrDefault(s => ReferenceEquals(s.List, analysed.Selected)) ?? Sources.FirstOrDefault();
        if (selected is null)
        {
            HasList = false;
            Summary = "Nenhuma tracklist na descrição nem nos comentários.";
            analysed.Selected = null;
        }
        else
        {
            Select(selected);
        }
        OnPropertyChanged(nameof(WriteToFile));
    }

    private void Select(SourceOption option)
    {
        foreach (var s in Sources)
            s.SetSelected(ReferenceEquals(s, option));
        if (_current is not null)
            _current.Selected = option.List;

        Entries.Clear();
        foreach (var e in option.List.Entries)
        {
            var text = string.IsNullOrWhiteSpace(e.Text) ? "(sem título)" : e.Text;
            Entries.Add(new EntryRow(e.Number.ToString("00"), e.Start is { } t ? TracklistExtractor.Format(t) : "", text, e.Link));
        }
        var untitled = option.List.Entries.Count(e => string.IsNullOrWhiteSpace(e.Text));
        var timed = option.List.Entries.Count(e => e.Start is not null);
        Summary = TracklistReportFormatter.Describe(option.List)
            + (timed == 0 ? " · sem tempos: só a lista vai pro arquivo, sem capítulos" : timed < option.List.Entries.Count ? $" · {option.List.Entries.Count - timed} sem tempo" : "")
            + (untitled > 0 ? $" · {untitled} sem título" : "");
        HasList = true;
    }

    private void Clear()
    {
        Sources.Clear();
        Entries.Clear();
        Notes.Clear();
        Title = Url = Summary = ReportText = "";
        HasList = false;
    }
}
