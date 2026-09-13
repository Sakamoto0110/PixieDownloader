using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>Which half of a request a queue row represents.</summary>
public enum JobKind
{
    /// <summary>The yt-dlp download (plus its own MP3 conversion / metadata embedding — one operation).</summary>
    Download,
    /// <summary>Our own ffmpeg pass after the download (speed change, audio strip, GIF encode).</summary>
    Processing
}

public enum JobStatus
{
    Queued,
    Active,
    Done,
    Failed,
    Cancelled
}

/// <summary>
/// One row of the download queue. A request that needs an ffmpeg pass is shown as two linked rows
/// (download → processing) that share a <see cref="RequestId"/> and always move together; a plain
/// MP3/MP4 download is a single row whose status text walks through the yt-dlp stages.
/// </summary>
public sealed class DownloadJobViewModel : ObservableObject
{
    public DownloadJobViewModel(Guid requestId, JobKind kind, string title, DownloadRequest request, bool isRestored = false)
    {
        RequestId = requestId;
        Kind = kind;
        Title = title;
        Request = request;
        IsRestored = isRestored;
        _statusText = kind == JobKind.Processing ? "Aguardando o download" : isRestored ? "Pendente da sessão anterior" : "Na fila";
    }

    public Guid RequestId { get; }
    public JobKind Kind { get; }
    public string Title { get; }
    public DownloadRequest Request { get; }
    public string Url => Request.Url;

    /// <summary>Came from <c>pending-downloads.txt</c>: sits in the list until the user presses "Continuar".</summary>
    public bool IsRestored { get; }

    /// <summary>The other half of the pair (null for a request without an ffmpeg pass).</summary>
    public DownloadJobViewModel? Partner { get; set; }

    /// <summary>Finished in an earlier run of the queue — kept in the list but left out of the overall progress.</summary>
    public bool IsSettled { get; set; }

    /// <summary>
    /// The job folder holding the downloaded video — known once the ffmpeg pass starts (reported with its
    /// progress) or restored from the pending file. It is what a processing cut off by a shutdown resumes from.
    /// </summary>
    public string? WorkDirectory { get; set; }

    /// <summary>Set on both rows of a request the app shutdown cancelled, so the pending file knows it was cut off rather than dropped by the user.</summary>
    public bool InterruptedByShutdown { get; set; }

    /// <summary>
    /// A restored request whose download is already on disk: the download row is done and the processing
    /// row is waiting to start — the runner starts it straight at the ffmpeg pass.
    /// </summary>
    public bool IsResumableProcessing
        => Kind == JobKind.Download && Request.ResumeWorkDirectory is not null && Status == JobStatus.Done && Partner is { IsQueued: true };

    private JobStatus _status = JobStatus.Queued;
    public JobStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsQueued));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(ShowProgressBar));
                OnPropertyChanged(nameof(StatusBrushKey));
                NotifyActionChanged();
                Partner?.NotifyActionChanged();   // "Cancelar" vs "Remover" depends on the pair, not just this row
            }
        }
    }

    private void NotifyActionChanged()
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionTooltip));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(IsRequestActive));
    }

    private string _statusText;
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private double _percent;
    public double Percent
    {
        get => _percent;
        set
        {
            if (SetProperty(ref _percent, value))
                OnPropertyChanged(nameof(PercentText));
        }
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set
        {
            if (SetProperty(ref _isIndeterminate, value))
                OnPropertyChanged(nameof(PercentText));
        }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    private string? _speedText;
    public string? SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    private string? _etaText;
    public string? EtaText
    {
        get => _etaText;
        set => SetProperty(ref _etaText, value);
    }

    public bool IsActive => Status == JobStatus.Active;
    public bool IsQueued => Status == JobStatus.Queued;
    public bool IsFinished => Status is JobStatus.Done or JobStatus.Failed or JobStatus.Cancelled;
    public bool ShowProgressBar => Status == JobStatus.Active;

    /// <summary>"Tentar de novo" is offered on a request that failed or was cancelled (either row) and isn't running.</summary>
    public bool CanRetry => !IsRequestActive && (Status is JobStatus.Failed or JobStatus.Cancelled || Partner is { Status: JobStatus.Failed or JobStatus.Cancelled });
    public string PercentText => IsIndeterminate ? "" : $"{Percent:0}%";

    public string KindLabel => Kind == JobKind.Download ? "Download" : "Processamento (ffmpeg)";

    /// <summary>Row accent: purple for downloads, teal for ffmpeg processing — the same hue whatever the status.</summary>
    public string KindBrushKey => Kind == JobKind.Download ? "Brush.Accent.Primary" : "Brush.Info";

    public string StatusBrushKey => Status switch
    {
        JobStatus.Done => "Brush.Success",
        JobStatus.Failed => "Brush.Error",
        JobStatus.Cancelled => "Brush.Text.Tertiary",
        JobStatus.Active => KindBrushKey,
        _ => "Brush.Text.Secondary",
    };

    /// <summary>True while this request is running — either row of the pair is active.</summary>
    public bool IsRequestActive => IsActive || Partner is { IsActive: true };

    /// <summary>The per-row button: cancels a running request, removes anything else.</summary>
    public string ActionLabel => IsRequestActive ? "Cancelar" : "Remover";
    public string ActionTooltip => IsRequestActive
        ? "Interrompe este download (as duas etapas) e apaga seus arquivos temporários"
        : "Tira este item da lista";

    // ───── State transitions (always on the UI thread) ─────

    public void MarkActive(string statusText)
    {
        Status = JobStatus.Active;
        StatusText = statusText;
    }

    public void MarkDone(string statusText = "Concluído")
    {
        Status = JobStatus.Done;
        StatusText = statusText;
        Percent = 100;
        IsIndeterminate = false;
    }

    public void MarkFailed(string? error)
    {
        Status = JobStatus.Failed;
        ErrorMessage = error;
        StatusText = string.IsNullOrWhiteSpace(error) ? "Falhou" : $"Falhou: {error}";
        IsIndeterminate = false;
    }

    public void MarkCancelled(string statusText = "Cancelado")
    {
        Status = JobStatus.Cancelled;
        StatusText = statusText;
        IsIndeterminate = false;
    }

    /// <summary>Back to the starting line for "Tentar de novo": queued, clean progress, part of the next run.</summary>
    public void ResetForRetry()
    {
        Percent = 0;
        IsIndeterminate = false;
        SpeedText = null;
        EtaText = null;
        ErrorMessage = null;
        IsSettled = false;
        InterruptedByShutdown = false;
        Status = JobStatus.Queued;
        StatusText = Kind == JobKind.Processing ? "Aguardando o download" : "Na fila (nova tentativa)";
    }
}
