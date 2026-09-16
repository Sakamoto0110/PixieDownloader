using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Pixie.TrackTracer.Detection;
using Pixie.TrackTracer.Expansion;
using Pixie.TrackTracer.Model;
using YtDlpCore;

namespace Pixie.TrackTracer;

/// <summary>
/// What the plugin knows about one analysed video: the detector's report, which block the user picked
/// (the best one until they say otherwise), whether it goes into the file when this video downloads, and
/// the tree built from each block — kept per block, so expanding links survives switching sources and is
/// what reaches the file. Kept per video, keyed by URL, so a download that starts later still finds its list.
/// </summary>
public sealed class AnalysedVideo(VideoInfo video, TracklistReport report)
{
    private readonly Dictionary<Tracklist, TracklistPayload> _payloads = new(ReferenceEqualityComparer.Instance);

    public VideoInfo Video { get; } = video;
    public TracklistReport Report { get; } = report;
    public Tracklist? Selected { get; set; } = report.Best;
    public bool WriteToFile { get; set; } = report.Best is not null;

    /// <summary>The tree for the selected block, with whatever the user expanded; null when no block is selected.</summary>
    public TracklistPayload? Payload => Selected is { } list ? PayloadFor(list) : null;

    public TracklistPayload PayloadFor(Tracklist list)
    {
        if (!_payloads.TryGetValue(list, out var payload))
            _payloads[list] = payload = TracklistPayload.From(Video, list);
        return payload;
    }
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

/// <summary>
/// One node of the tree as the tab shows it: the track's number, time and text, its link, and — for a linked
/// one — where its expansion stands. Rows wrap the payload's nodes, so expanding a row mutates the tree that
/// goes into the file, and the row rebuilds its children from what the expander left.
/// </summary>
public sealed class NodeRow : ObservableObject
{
    private readonly TracklistTabViewModel _owner;
    private bool _isBusy;
    private bool _isCollapsed;

    internal NodeRow(TracklistNode node, int depth, NodeRow? parent, int index, TracklistTabViewModel owner)
    {
        Node = node;
        Depth = depth;
        Parent = parent;
        _owner = owner;
        Number = index.ToString("00");
        Time = node.TimestampMs is { } ms ? TracklistExtractor.Format(TimeSpan.FromMilliseconds(ms)) : "";
        Text = string.IsNullOrWhiteSpace(node.Title) ? "(sem título)" : node.Title;
        Indent = new Thickness(depth > 1 ? (depth - 1) * 26 : 0, 0, 0, 0);
        ExpandCommand = new RowCommand(() => _ = _owner.ExpandAsync(this), () => CanExpand && !IsBusy);
        ToggleCollapseCommand = new RowCommand(() => IsCollapsed = !IsCollapsed, () => HasChildren);
        Rebuild();
    }

    public TracklistNode Node { get; }
    public int Depth { get; }
    public NodeRow? Parent { get; }
    public string Number { get; }
    public string Time { get; }
    public string Text { get; }
    public Thickness Indent { get; }
    public string? Link => Node.Url;
    public bool HasLink => Node.Url is not null;
    public bool HasTime => Time.Length > 0;
    public ObservableCollection<NodeRow> Children { get; } = [];
    public ICommand ExpandCommand { get; }
    public ICommand ToggleCollapseCommand { get; }

    public NodeState State => Node.State;
    public bool HasChildren => Children.Count > 0;
    public bool CanExpand => _owner.CanExpand(this);
    public bool BeyondLimit => Node.Url is not null && Node.State == NodeState.Unresolved && !_owner.WithinLimit(this);

    /// <summary>The link is being analysed right now.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseStateChanged();
        }
    }

    /// <summary>Children hidden by the user; the tree underneath is untouched.</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (SetProperty(ref _isCollapsed, value))
            {
                OnPropertyChanged(nameof(ShowChildren));
                OnPropertyChanged(nameof(CollapseGlyph));
            }
        }
    }

    public bool ShowChildren => HasChildren && !IsCollapsed;
    public string CollapseGlyph => IsCollapsed ? "▸" : "▾";
    public bool ShowExpand => CanExpand && !IsBusy && State == NodeState.Unresolved;
    public bool ShowRetry => CanExpand && !IsBusy && State == NodeState.Failed;
    public bool IsFailed => State == NodeState.Failed;
    public bool ShowState => StateText.Length > 0;

    /// <summary>What happened to this link, in words — empty for a plain track and for a link never touched.</summary>
    public string StateText => IsBusy ? "analisando…" : State switch
    {
        NodeState.Resolved => Children.Count == 1 ? "1 faixa" : $"{Children.Count} faixas",
        NodeState.Empty when Node.Url is not null => "nada encontrado nesse link",
        NodeState.Failed => Node.Error ?? "falhou",
        NodeState.Unresolved when BeyondLimit => $"além do limite de profundidade ({_owner.MaxDepth})",
        _ => "",
    };

    /// <summary>Rebuilds the child rows from the node and refreshes every derived property.</summary>
    internal void Rebuild()
    {
        Children.Clear();
        var i = 1;
        foreach (var child in Node.Children)
            Children.Add(new NodeRow(child, Depth + 1, this, i++, _owner));
        RaiseStateChanged();
    }

    internal void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ShowState));
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(ShowChildren));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(BeyondLimit));
        OnPropertyChanged(nameof(ShowExpand));
        OnPropertyChanged(nameof(ShowRetry));
        OnPropertyChanged(nameof(IsFailed));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The nodes from the root down to this row's parent — what the expander checks for cycles.</summary>
    internal List<TracklistNode> AncestorNodes()
    {
        var path = new List<TracklistNode>();
        for (var p = Parent; p is not null; p = p.Parent)
            path.Insert(0, p.Node);
        path.Insert(0, _owner.RootNode!);
        return path;
    }

    internal IEnumerable<NodeRow> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grandchild in child.Descendants())
                yield return grandchild;
        }
    }
}

/// <summary>A minimal ICommand for the rows (the app's RelayCommand is not visible to a plugin).</summary>
internal sealed class RowCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>
/// The "Tracklist" tab. The block carries the provenance, not the item: the sources sit on top as toggles
/// with their counts ("Descrição · 24 / Comentário fixado · 18") and switching one swaps the whole list.
/// Problem items stay visible — a track without a title shows as such — because hiding them would make
/// the extraction look cleaner than it was. A linked track expands on a click (or all of them, one at a
/// time, with "Expandir tudo"), down to the depth the selector allows; what it finds is nested under it.
/// </summary>
public sealed class TracklistTabViewModel : ObservableObject
{
    private readonly TracklistExpander _expander;
    private readonly TrackTracerSettings _settings;
    private readonly Action<LogLevel, string> _log;
    private readonly CancellationToken _shutdown;
    private AnalysedVideo? _current;
    private TracklistPayload? _payload;
    private CancellationTokenSource? _expandAll;

    public TracklistTabViewModel(TracklistExpander expander, TrackTracerSettings settings, Action<LogLevel, string> log, CancellationToken shutdown)
    {
        _expander = expander;
        _settings = settings;
        _log = log;
        _shutdown = shutdown;
        _expander.MaxDepth = settings.MaxDepth;
        ExpandAllCommand = new RowCommand(() => _ = ExpandAllAsync(), () => HasList && !IsExpandingAll && ExpandableCount > 0);
        StopExpandCommand = new RowCommand(() => _expandAll?.Cancel(), () => IsExpandingAll);
    }

    public ObservableCollection<SourceOption> Sources { get; } = [];
    public ObservableCollection<NodeRow> Entries { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public IReadOnlyList<int> DepthOptions { get; } = Enumerable.Range(TrackTracerSettings.MinDepth, TrackTracerSettings.MaxDepthLimit - TrackTracerSettings.MinDepth + 1).ToList();
    public ICommand ExpandAllCommand { get; }
    public ICommand StopExpandCommand { get; }

    /// <summary>The root of the tree being shown — the analysed video; null before any analysis.</summary>
    internal TracklistNode? RootNode => _payload?.Root;

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

    private bool _isExpandingAll;
    public bool IsExpandingAll { get => _isExpandingAll; private set { if (SetProperty(ref _isExpandingAll, value)) OnPropertyChanged(nameof(ExpandAllLabel)); } }

    private int _expandableCount;
    /// <summary>How many links "Expandir tudo" would analyse now — the button's number.</summary>
    public int ExpandableCount { get => _expandableCount; private set { if (SetProperty(ref _expandableCount, value)) { OnPropertyChanged(nameof(ExpandAllLabel)); OnPropertyChanged(nameof(HasExpandable)); } } }
    public bool HasExpandable => ExpandableCount > 0 || IsExpandingAll;
    public string ExpandAllLabel => IsExpandingAll ? "Expandindo…" : $"Expandir tudo ({ExpandableCount})";

    /// <summary>Two-way with the selector; persisted by the plugin. Rows beyond the new limit lose their button, nothing is discarded.</summary>
    public int MaxDepth
    {
        get => _expander.MaxDepth;
        set
        {
            var clamped = Math.Clamp(value, TrackTracerSettings.MinDepth, TrackTracerSettings.MaxDepthLimit);
            if (clamped == _expander.MaxDepth)
                return;
            _expander.MaxDepth = clamped;
            _settings.MaxDepth = clamped;
            _settings.Save();
            OnPropertyChanged();
            foreach (var row in AllRows())
                row.RaiseStateChanged();
            RefreshCounts();
        }
    }

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
        _expandAll?.Cancel();
        _current = null;
        Clear();
        Message = "Playlist analisada — a detecção é por vídeo. Analise um vídeo sozinho pra ver a tracklist dele.";
        HasVideo = false;
    }

    public void Show(AnalysedVideo analysed)
    {
        _expandAll?.Cancel();
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

    // ───── Expansion ─────

    internal bool WithinLimit(NodeRow row) => row.Depth < _expander.MaxDepth;

    internal bool CanExpand(NodeRow row) => _expander.CanExpand(row.Node, row.Depth);

    /// <summary>One click: analyse this link and nest what it finds under the row.</summary>
    internal async Task ExpandAsync(NodeRow row)
    {
        if (row.IsBusy || !CanExpand(row))
            return;
        row.IsBusy = true;
        try
        {
            var outcome = await _expander.ExpandAsync(row.Node, row.Depth, row.AncestorNodes(), _shutdown);
            row.IsBusy = false;
            row.Rebuild();
            Report(row, outcome);
        }
        catch (OperationCanceledException)
        {
            row.IsBusy = false;
            row.RaiseStateChanged();
        }
        finally
        {
            RefreshCounts();
        }
    }

    /// <summary>Every expandable link under the root, one at a time, until the limit or "Parar".</summary>
    private async Task ExpandAllAsync()
    {
        if (IsExpandingAll || _payload is null)
            return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
        _expandAll = cts;
        IsExpandingAll = true;
        CommandManager.InvalidateRequerySuggested();
        NodeRow? busy = null;
        try
        {
            var rowsByNode = new Dictionary<TracklistNode, NodeRow>(ReferenceEqualityComparer.Instance);
            foreach (var r in AllRows())
                rowsByNode[r.Node] = r;
            var count = await _expander.ExpandAllAsync(_payload.Root,
                node =>
                {
                    if (rowsByNode.TryGetValue(node, out var row))
                        (busy = row).IsBusy = true;
                },
                (node, outcome) =>
                {
                    if (!rowsByNode.TryGetValue(node, out var row))
                        return;
                    busy = null;
                    row.IsBusy = false;
                    row.Rebuild();
                    foreach (var child in row.Children)
                        rowsByNode[child.Node] = child;
                    Report(row, outcome);
                },
                cts.Token);
            _log(LogLevel.Info, count == 0 ? "nada pra expandir" : $"{count} link(s) expandidos");
        }
        catch (OperationCanceledException)
        {
            _log(LogLevel.Info, "expansão interrompida");
        }
        finally
        {
            if (busy is not null)
                busy.IsBusy = false;   // the one cut short by "Parar" or the shutdown
            IsExpandingAll = false;
            _expandAll = null;
            RefreshCounts();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Report(NodeRow row, ExpandOutcome outcome)
    {
        var what = outcome switch
        {
            ExpandOutcome.Resolved => $"{row.Children.Count} faixas em \"{row.Text}\"",
            ExpandOutcome.Empty => $"nada em \"{row.Text}\"",
            ExpandOutcome.Cycle => $"\"{row.Text}\" já está na árvore acima — não expandido",
            ExpandOutcome.Failed => $"\"{row.Text}\" falhou: {row.Node.Error}",
            _ => null,
        };
        if (what is not null)
            _log(outcome == ExpandOutcome.Failed ? LogLevel.Warning : LogLevel.Info, what);
    }

    private void RefreshCounts()
    {
        ExpandableCount = _payload is null ? 0 : _expander.CountExpandable(_payload.Root);
        if (_current?.Selected is { } list)
            Summary = Describe(list);
        CommandManager.InvalidateRequerySuggested();
    }

    private IEnumerable<NodeRow> AllRows()
    {
        foreach (var row in Entries)
        {
            yield return row;
            foreach (var d in row.Descendants())
                yield return d;
        }
    }

    // ───── Sources ─────

    private void Select(SourceOption option)
    {
        _expandAll?.Cancel();
        foreach (var s in Sources)
            s.SetSelected(ReferenceEquals(s, option));
        if (_current is null)
            return;
        _current.Selected = option.List;
        _payload = _current.PayloadFor(option.List);

        Entries.Clear();
        var i = 1;
        foreach (var child in _payload.Root.Children)
            Entries.Add(new NodeRow(child, 1, null, i++, this));
        HasList = true;
        RefreshCounts();
    }

    private string Describe(Tracklist list)
    {
        var untitled = list.Entries.Count(e => string.IsNullOrWhiteSpace(e.Text));
        var timed = list.Entries.Count(e => e.Start is not null);
        var expanded = _payload is null ? 0 : CountResolved(_payload.Root);
        return TracklistReportFormatter.Describe(list)
            + (timed == 0 ? " · sem tempos: só a lista vai pro arquivo, sem capítulos" : timed < list.Entries.Count ? $" · {list.Entries.Count - timed} sem tempo" : "")
            + (untitled > 0 ? $" · {untitled} sem título" : "")
            + (expanded > 0 ? $" · {expanded} link(s) expandido(s)" : "");

        static int CountResolved(TracklistNode node) => node.Children.Sum(c => (c.State == NodeState.Resolved ? 1 : 0) + CountResolved(c));
    }

    private void Clear()
    {
        Sources.Clear();
        Entries.Clear();
        Notes.Clear();
        Title = Url = Summary = ReportText = "";
        HasList = false;
        _payload = null;
        ExpandableCount = 0;
    }
}
