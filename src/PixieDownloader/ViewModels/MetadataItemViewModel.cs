using YtDlpCore;

namespace PixieDownloader.ViewModels;

/// <summary>
/// One embeddable metadata field of the previewed video: its value (shown inside an expander) and
/// whether it should be written into the output file. Toggling it is routed back to the owning
/// <see cref="MainViewModel"/>, which keeps the persisted exclusion list and the tri-state master in sync.
/// </summary>
public sealed class MetadataItemViewModel : ObservableObject
{
    private readonly Action<MetadataItemViewModel, bool> _onIncludedChanged;

    public MetadataItemViewModel(MetadataFieldDef field, string value, bool isIncluded, Action<MetadataItemViewModel, bool> onIncludedChanged)
    {
        Field = field;
        Value = value;
        _isIncluded = isIncluded;
        _onIncludedChanged = onIncludedChanged;
    }

    public MetadataFieldDef Field { get; }
    public string Key => Field.Key;
    public string Label => Field.Label;
    public string Value { get; }

    /// <summary>Short single-line preview shown next to the label (the expander reveals the full value).</summary>
    public string Preview
    {
        get
        {
            var oneLine = Value.ReplaceLineEndings(" ").Trim();
            return oneLine.Length <= 60 ? oneLine : oneLine[..57] + "…";
        }
    }

    private bool _isIncluded;
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (SetProperty(ref _isIncluded, value))
            {
                if (!value)
                    IsExpanded = false;   // an excluded field's expander is disabled — never leave it open
                _onIncludedChanged(this, value);
            }
        }
    }

    /// <summary>Silent update from the owner (master checkbox / settings) — no callback round-trip.</summary>
    public void SetIncludedSilently(bool value)
    {
        if (_isIncluded == value)
            return;
        _isIncluded = value;
        if (!value)
            IsExpanded = false;
        OnPropertyChanged(nameof(IsIncluded));
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}
