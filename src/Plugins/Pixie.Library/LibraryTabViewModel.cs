using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pixie.Library.Catalog;
using Pixie.Library.Mvvm;
using Pixie.Library.Player;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Library;

/// <summary>A folder of the catalogue as a toggle on top of the tab — "Músicas · 1.284"; the first one is every folder, the second the storage root.</summary>
public sealed class RootOption : ObservableObject
{
    private readonly Action<RootOption> _select;
    private bool _isSelected;

    internal RootOption(SourceRef? root, int count, bool unavailable, Action<RootOption> select)
    {
        Root = root;
        Path = root?.Path;
        Name = root?.Name ?? "Todas";
        Count = count;
        Unavailable = unavailable;
        Label = $"{Name} · {Format.Count(count)}" + (unavailable ? " ⚠" : "");
        ToolTip = root is null ? "Tudo que está no catálogo"
            : root.Path + (root.IsStorageRoot ? "\nA pasta da biblioteca: pra onde Importar copia e onde Criar pasta cria" : "")
            + (unavailable ? "\nPasta não encontrada — disco desligado? O que já estava no catálogo continua listado." : "");
        _select = select;
    }

    internal SourceRef? Root { get; }
    public string? Path { get; }
    public string Name { get; }
    public int Count { get; }
    public bool Unavailable { get; }
    public string Label { get; }
    public string ToolTip { get; }
    public bool IsAll => Root is null;
    public bool IsStorageRoot => Root is { IsStorageRoot: true };

    /// <summary>Two-way from the toggle. Turning one on turns the others off; turning the current one off is ignored.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value)
                _select(this);
            else
                OnPropertyChanged();   // snap back
        }
    }

    internal void SetSelected(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));
}

/// <summary>A group as the "Adicionar a" submenu lists it: its path in the group tree, and whether the row already belongs to it.</summary>
public sealed record GroupChoice(string Id, string Label, bool Checked);

/// <summary>
/// The "Biblioteca" tab: one list, built by the projection from the reconciled model — "Tudo" over the storage
/// root and the other sources, then the groups, every container the same collapsible line — filtered by the
/// chips, the type and the search, sorted inside each container. The runner's and the service's events come in
/// on pool threads and are marshalled here; every operation goes through <see cref="LibraryOperations"/> and
/// the tab learns of its effect from the next model. Opening the tab shows what the index had and asks for an
/// incremental sync behind it.
/// </summary>
public sealed class LibraryTabViewModel : ObservableObject
{
    private readonly LibraryService _service;
    private readonly LibraryManifest _manifest;
    private readonly LibrarySync _sync;
    private readonly LibraryOperations _operations;
    private readonly IPluginHost _host;
    private readonly VlcInstaller? _vlc;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, LibraryRow> _byKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _filterTimer;
    private readonly List<FileNode> _early = [];
    private bool _opened;
    private DateTime _lastAutoCheck = DateTime.MinValue;
    private string? _progress;
    private LibraryModel? _model;
    private string? _fault;
    private IReadOnlyList<LibraryRow> _selection = [];
    private Action<string>? _pendingName;

    internal LibraryTabViewModel(LibraryService service, IPluginHost host, VlcInstaller? vlc = null)
    {
        _service = service;
        _manifest = service.Manifest;
        _sync = service.Sync;
        _operations = service.Operations;
        _host = host;
        _vlc = vlc;
        _dispatcher = Dispatcher.CurrentDispatcher;
        VlcIcon = vlc is null ? null : Player.VlcIcon.Load();

        _selectedSort = SortOptions[0];
        _selectedKind = KindOptions[0];
        _expanded.Add(LibraryProjection.TopKey);   // "Tudo" starts open: the library's own tree is the overview

        _filterTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); Reproject(); };

        SyncCommand = new RelayCommand(() => _sync.Request(SyncMode.Full), () => !IsSyncing);
        AddFolderCommand = new RelayCommand(AddFolder);
        AddFilesCommand = new RelayCommand(AddFiles);
        ImportFilesCommand = new RelayCommand(ImportFiles, () => !IsImporting);
        ImportFolderCommand = new RelayCommand(ImportFolder, () => !IsImporting);
        CreateFolderCommand = new RelayCommand(() => AskName("Nome da pasta nova (em " + _operations.StorageRoot + "):", name => Run(_operations.CreatePhysicalFolder(null, name))));
        CreateGroupCommand = new RelayCommand(() => AskName("Nome do grupo:", name => Run(_operations.CreateGroup(name))));
        ChangeStorageRootCommand = new RelayCommand(ChangeStorageRoot);
        NamePromptOkCommand = new RelayCommand(CommitName, () => !string.IsNullOrWhiteSpace(NamePromptText));
        NamePromptCancelCommand = new RelayCommand(() => { _pendingName = null; ShowNamePrompt = false; });
        RemoveRootCommand = new RelayCommand(RemoveSelectedRoot, () => SelectedRoot is { IsAll: false, IsStorageRoot: false });
        RenameRootCommand = new RelayCommand(RenameSelectedRoot, () => SelectedRoot is { IsAll: false, IsStorageRoot: false } && !string.IsNullOrWhiteSpace(RootNameEdit));
        OpenRootFolderCommand = new RelayCommand(() => OpenFolder(SelectedRoot?.Path), () => SelectedRoot is { IsAll: false, Unavailable: false });
        OpenCommand = new RelayCommand<LibraryRow>(Open);
        ShowInFolderCommand = new RelayCommand<LibraryRow>(ShowInFolder);
        ToggleExpandCommand = new RelayCommand<LibraryRow>(ToggleExpand);
        ChoosePlayerCommand = new RelayCommand(ChoosePlayer);
        UseDefaultPlayerCommand = new RelayCommand(() => SetPlayer(null), () => HasPlayer);
        UseVlcCommand = new RelayCommand(() => SetPlayer(AvailableVlc), () => AvailableVlc is { } vlc && !IsVlcBusy && !IsPlayer(vlc));
        DownloadVlcCommand = new RelayCommand(AskOrDownloadVlc, () => _vlc is not null && !IsVlcBusy && !ShowVlcPrompt);
        VlcPromptUseCommand = new RelayCommand(() => { ShowVlcPrompt = false; SetPlayer(AvailableVlc); });
        VlcPromptDownloadCommand = new RelayCommand(() => { ShowVlcPrompt = false; DownloadVlc(); });
        VlcPromptCancelCommand = new RelayCommand(() => ShowVlcPrompt = false);

        _sync.Progress += p => Post(() => OnProgress(p));
        _sync.Completed += result => Post(() => OnCompleted(result));
        _sync.Faulted += ex => Post(() => { _fault = ex.Message; UpdateStatus(); });
        _sync.RunStateChanged += () => Post(() =>
        {
            IsSyncing = _sync.IsRunning;
            if (!IsSyncing)
                _progress = null;
            UpdateStatus();
            CommandManager.InvalidateRequerySuggested();
        });
        _service.ModelChanged += model => Post(() => OnModel(model));

        RefreshRoots();
        UpdateStatus();
    }

    // ───── Lists and selection ─────

    public BulkObservableCollection<LibraryRow> Rows { get; } = [];
    public ObservableCollection<RootOption> Roots { get; } = [];
    public IReadOnlyList<SortOption> SortOptions { get; } = SortOption.All;
    public IReadOnlyList<KindOption> KindOptions { get; } = KindOption.All;

    private RootOption? _selectedRoot;
    public RootOption? SelectedRoot
    {
        get => _selectedRoot;
        private set
        {
            if (!SetProperty(ref _selectedRoot, value))
                return;
            RootNameEdit = value?.Root is { IsStorageRoot: false } r ? r.Name : "";
            OnPropertyChanged(nameof(HasSelectedRoot));
            OnPropertyChanged(nameof(HasSelectedStorageRoot));
            OnPropertyChanged(nameof(SelectedRootPath));
        }
    }

    /// <summary>A folder source is selected: it can be renamed, opened, taken out.</summary>
    public bool HasSelectedRoot => SelectedRoot is { IsAll: false, IsStorageRoot: false };

    /// <summary>The storage root is selected: its path, and the way to change it.</summary>
    public bool HasSelectedStorageRoot => SelectedRoot is { IsStorageRoot: true };
    public string SelectedRootPath => SelectedRoot?.Root?.Path ?? "";

    private string _rootNameEdit = "";
    public string RootNameEdit { get => _rootNameEdit; set => SetProperty(ref _rootNameEdit, value); }

    private SortOption _selectedSort;
    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || !SetProperty(ref _selectedSort, value))
                return;
            Reproject();
        }
    }

    private KindOption _selectedKind;
    public KindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (value is null || !SetProperty(ref _selectedKind, value))
                return;
            Reproject();
        }
    }

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value ?? ""))
                return;
            _filterTimer.Stop();
            _filterTimer.Start();
        }
    }

    /// <summary>
    /// Containers on (the default) or off. Persisted in the manifest: it is how the person likes the list, like
    /// the player. Off, the list is one flat run of files in the chosen order — no folders, no groups.
    /// </summary>
    public bool GroupByFolder
    {
        get => _manifest.GroupByFolder;
        set
        {
            if (value == _manifest.GroupByFolder || _manifest.ReadOnly)
                return;
            _manifest.Update(m => m.GroupByFolder = value);
            OnPropertyChanged();
            Reproject();
        }
    }

    /// <summary>The rows the list has selected right now, as the view reports them; what the context menu and a drag act on.</summary>
    internal IReadOnlyList<LibraryRow> Selection => _selection;

    public void SetSelection(IEnumerable<LibraryRow> rows) => _selection = rows.ToList();

    // ───── Status ─────

    private bool _isSyncing;
    public bool IsSyncing { get => _isSyncing; private set => SetProperty(ref _isSyncing, value); }

    private bool _isImporting;
    public bool IsImporting { get => _isImporting; private set { if (SetProperty(ref _isImporting, value)) CommandManager.InvalidateRequerySuggested(); } }

    private string _importStatusText = "";
    public string ImportStatusText { get => _importStatusText; private set => SetProperty(ref _importStatusText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _warningText = "";
    public string WarningText { get => _warningText; private set { if (SetProperty(ref _warningText, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => WarningText.Length > 0;

    private string _countText = "";
    public string CountText { get => _countText; private set => SetProperty(ref _countText, value); }

    private bool _showEmptyState;
    /// <summary>Nothing listed at all (as opposed to nothing passing the filter).</summary>
    public bool ShowEmptyState { get => _showEmptyState; private set => SetProperty(ref _showEmptyState, value); }

    private bool _showNoMatches;
    public bool ShowNoMatches { get => _showNoMatches; private set => SetProperty(ref _showNoMatches, value); }

    private bool _showSettings;
    public bool ShowSettings { get => _showSettings; set => SetProperty(ref _showSettings, value); }

    public bool IsReadOnly => _manifest.ReadOnly;

    // ───── The name band (a new folder, a new group, a renamed group) ─────

    private bool _showNamePrompt;
    public bool ShowNamePrompt { get => _showNamePrompt; private set => SetProperty(ref _showNamePrompt, value); }

    private string _namePromptTitle = "";
    public string NamePromptTitle { get => _namePromptTitle; private set => SetProperty(ref _namePromptTitle, value); }

    private string _namePromptText = "";
    public string NamePromptText { get => _namePromptText; set => SetProperty(ref _namePromptText, value); }

    private void AskName(string title, Action<string> commit, string initial = "")
    {
        NamePromptTitle = title;
        NamePromptText = initial;
        _pendingName = commit;
        ShowNamePrompt = true;
    }

    private void CommitName()
    {
        var name = NamePromptText.Trim();
        var commit = _pendingName;
        if (name.Length == 0 || commit is null)
            return;
        ShowNamePrompt = false;
        _pendingName = null;
        commit(name);
    }

    // ───── Settings (two-way with the manifest) ─────

    public bool AutoAddRoots
    {
        get => _manifest.AutoAddRoots;
        set { if (value != _manifest.AutoAddRoots) { _manifest.Update(m => m.AutoAddRoots = value); OnPropertyChanged(); } }
    }

    public bool AutoSync
    {
        get => _manifest.AutoSync;
        set { if (value != _manifest.AutoSync) { _manifest.Update(m => m.AutoSync = value); OnPropertyChanged(); } }
    }

    /// <summary>"gif, webp" — committed when the box loses focus; a change means a full scan, unchanged folders would not be relisted otherwise.</summary>
    public string ExtraExtensionsText
    {
        get => string.Join(", ", _manifest.ExtraExtensions);
        set
        {
            var parsed = (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(FileKinds.Normalize).Where(e => e is not null).Select(e => e!.TrimStart('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (parsed.SequenceEqual(_manifest.ExtraExtensions, StringComparer.OrdinalIgnoreCase))
            {
                OnPropertyChanged();
                return;
            }
            _manifest.Update(m => m.ExtraExtensions = parsed);
            OnPropertyChanged();
            _manifest.MarkPending();
            _sync.Request(SyncMode.Full);
        }
    }

    public string PlayerText => _manifest.Player is { } p ? Path.GetFileNameWithoutExtension(p) + "  (" + p + ")" : "padrão do Windows";
    public bool HasPlayer => _manifest.Player is not null;

    // ───── VLC ─────
    // The player the tab recommends, two buttons: "Usar VLC" picks the VLC this machine already has — the one
    // installed in Windows first (looked up once per session, when the tab first opens; never persisted), else
    // the portable one in tools\vlc — and the cone only downloads (VlcInstaller: the official zip into tools\vlc)
    // and then picks it. When a VLC is already here, the cone asks first, inline: use that one, download anyway,
    // or leave it — a second 80 MB for nothing is the mistake to prevent.

    public ImageSource? VlcIcon { get; }

    private string? _installedVlc;
    private bool _vlcChecked;

    /// <summary>The VLC installed in Windows, if the session's one look found one.</summary>
    public string? InstalledVlc => _installedVlc;

    /// <summary>The VLC "Usar VLC" would pick: the installed one, else the portable one, else nothing.</summary>
    public string? AvailableVlc => _installedVlc ?? (_vlc?.IsInstalled == true ? _vlc.ExePath : null);

    private bool IsPlayer(string? path)
        => path is not null && _manifest.Player is { } p && PathUtil.Normalize(p).Equals(PathUtil.Normalize(path), StringComparison.OrdinalIgnoreCase);

    public string UseVlcToolTip => AvailableVlc is { } vlc
        ? (IsPlayer(vlc) ? "Já é o player" : _installedVlc is not null ? $"Abre os arquivos com o VLC instalado no Windows ({Path.GetDirectoryName(vlc)})"
            : $"Abre os arquivos com o VLC portátil de {Path.GetDirectoryName(vlc)}{(_vlc?.InstalledVersion is { } v ? " (" + v + ")" : "")}")
        : "Nenhum VLC encontrado nesta máquina — o cone ao lado baixa o portátil";

    public string DownloadVlcToolTip => $"Baixar e usar o VLC portátil oficial (80 MB; fica em {_vlc?.Directory}, uns 140 MB, sem instalar nada no Windows)";

    private bool _isVlcBusy;
    public bool IsVlcBusy { get => _isVlcBusy; private set { if (SetProperty(ref _isVlcBusy, value)) NotifyVlc(); } }

    private string _vlcStatusText = "";
    /// <summary>Where the download is, shown next to the buttons while it runs.</summary>
    public string VlcStatusText { get => _vlcStatusText; private set => SetProperty(ref _vlcStatusText, value); }

    private bool _showVlcPrompt;
    public bool ShowVlcPrompt { get => _showVlcPrompt; private set { if (SetProperty(ref _showVlcPrompt, value)) NotifyVlc(); } }

    private string _vlcPromptText = "";
    public string VlcPromptText { get => _vlcPromptText; private set => SetProperty(ref _vlcPromptText, value); }

    private string _vlcPromptUseLabel = "";
    public string VlcPromptUseLabel { get => _vlcPromptUseLabel; private set => SetProperty(ref _vlcPromptUseLabel, value); }

    private string _vlcPromptDownloadLabel = "";
    public string VlcPromptDownloadLabel { get => _vlcPromptDownloadLabel; private set => SetProperty(ref _vlcPromptDownloadLabel, value); }

    private void NotifyVlc()
    {
        OnPropertyChanged(nameof(InstalledVlc));
        OnPropertyChanged(nameof(AvailableVlc));
        OnPropertyChanged(nameof(UseVlcToolTip));
        OnPropertyChanged(nameof(DownloadVlcToolTip));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The session's one look for a VLC installed in Windows. Silent; nothing is written anywhere.</summary>
    private void CheckInstalledVlc()
    {
        if (_vlcChecked || _vlc is null)
            return;
        _vlcChecked = true;
        _installedVlc = VlcLocator.FindInstalled();
        if (_installedVlc is not null)
            _host.Log(LogLevel.Debug, $"VLC instalado no Windows: {_installedVlc}");
        NotifyVlc();
    }

    // ───── Commands ─────

    public ICommand SyncCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand ImportFilesCommand { get; }
    public ICommand ImportFolderCommand { get; }
    public ICommand CreateFolderCommand { get; }
    public ICommand CreateGroupCommand { get; }
    public ICommand ChangeStorageRootCommand { get; }
    public ICommand NamePromptOkCommand { get; }
    public ICommand NamePromptCancelCommand { get; }
    public ICommand RemoveRootCommand { get; }
    public ICommand RenameRootCommand { get; }
    public ICommand OpenRootFolderCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand ToggleExpandCommand { get; }
    public ICommand ChoosePlayerCommand { get; }
    public ICommand UseDefaultPlayerCommand { get; }
    public ICommand UseVlcCommand { get; }
    public ICommand DownloadVlcCommand { get; }
    public ICommand VlcPromptUseCommand { get; }
    public ICommand VlcPromptDownloadCommand { get; }
    public ICommand VlcPromptCancelCommand { get; }

    // ───── Lifecycle ─────

    /// <summary>The view got shown (every time the tab is selected): show the model, then check the folders behind it.</summary>
    public void OnTabOpened()
    {
        if (!_opened)
        {
            _opened = true;
            _ = _sync.EnsureLoadedAsync();
        }
        CheckInstalledVlc();
        // The check is one stat per known folder — cheap, but not for free on a sleeping disk, so not on every tab flick.
        if (!_manifest.AutoSync || DateTime.UtcNow - _lastAutoCheck < TimeSpan.FromSeconds(5))
            return;
        _lastAutoCheck = DateTime.UtcNow;
        _sync.Request(SyncMode.Incremental);
    }

    // ───── The model and the rows ─────

    private void OnModel(LibraryModel model)
    {
        _model = model;
        _early.Clear();
        RefreshRoots();
        Reproject();
        UpdateStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The folders the rows are attributed to, the storage root first: what the chips show and what a row's "root" is.</summary>
    private List<SourceRef> SourceRefs()
    {
        var storage = _manifest.StorageRoot;
        var refs = new List<SourceRef> { new(null, LegacyMigration.NameFor(storage), storage) };
        foreach (var item in _manifest.Snapshot().Sources.Where(i => i.IsFolder))
        {
            if (ScanPlan.ResolvePath(item) is { } path)
                refs.Add(new SourceRef(item.Id, item.Source!.Name, path));
        }
        return refs;
    }

    /// <summary>Builds the rows again from the model and the tab's state, and folds them into the list.</summary>
    private void Reproject()
    {
        if (_model is null)
            return;
        var options = new ProjectionOptions(SelectedRoot?.Path, SelectedKind.Kind, FilterText, SelectedSort.Comparer, GroupByFolder, _expanded, _early.ToArray());
        var projection = LibraryProjection.Build(_model, SourceRefs(), options);
        _stats = (projection.Files, projection.Seconds, projection.Bytes);
        ApplyRows(projection.Rows);
    }

    private (int Files, double Seconds, long Bytes) _stats;

    /// <summary>
    /// Folds a row set into the list by key: new rows in, rows whose signature changed replaced in place, rows
    /// that went removed — and the order of the projection kept, so an opened container shows its children
    /// right under it. A small diff is applied row by row, so the list box keeps its scroll and selection; a big
    /// one (the first load, a search) is one reset — the list lays out once instead of a thousand times.
    /// </summary>
    private void ApplyRows(List<LibraryRow> rows)
    {
        var changes = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!seen.Add(row.Key))
                continue;
            if (!_byKey.TryGetValue(row.Key, out var existing) || existing.Signature != row.Signature)
                changes++;
        }
        changes += Rows.Count(r => !seen.Contains(r.Key));
        var unique = rows.Where(r => seen.Remove(r.Key)).ToList();

        if (changes > 200 || Rows.Count == 0 || unique.Count == 0)
        {
            Rows.ReplaceAll(unique);
        }
        else
        {
            // In order: walk the wanted rows, keeping the list aligned with them.
            for (var i = 0; i < unique.Count; i++)
            {
                var wanted = unique[i];
                if (i < Rows.Count && Rows[i].Key == wanted.Key)
                {
                    if (Rows[i].Signature != wanted.Signature)
                        Rows[i] = wanted;
                    continue;
                }
                var at = -1;
                for (var j = i + 1; j < Rows.Count; j++)
                {
                    if (Rows[j].Key == wanted.Key)
                    {
                        at = j;
                        break;
                    }
                }
                if (at >= 0)
                {
                    // rows between i and at are gone
                    for (var j = at - 1; j >= i; j--)
                        Rows.RemoveAt(j);
                    if (Rows[i].Signature != wanted.Signature)
                        Rows[i] = wanted;
                }
                else
                {
                    Rows.Insert(i, wanted);
                }
            }
            while (Rows.Count > unique.Count)
                Rows.RemoveAt(Rows.Count - 1);
        }
        _byKey.Clear();
        foreach (var row in unique)
            _byKey[row.Key] = row;
        UpdateCounts();
    }

    private void ToggleExpand(LibraryRow row)
    {
        if (!row.IsContainer)
            return;
        if (!string.IsNullOrWhiteSpace(FilterText))
            return;   // a search opens everything; the flags come back as they were when it is cleared
        if (!_expanded.Remove(row.Key))
            _expanded.Add(row.Key);
        Reproject();
    }

    /// <summary>The footer: the files the filters let through, open or not, once each — the same file under two containers is one file.</summary>
    private void UpdateCounts()
    {
        var (n, seconds, bytes) = _stats;
        var total = _model?.Index.Entries.Count ?? 0;
        var filtered = n != total;
        CountText = total == 0 && n == 0 ? "" : (filtered ? $"{Format.Count(n)} de {Format.Count(total)}" : Format.Count(n) + (n == 1 ? " arquivo" : " arquivos"))
            + (n > 0 ? $" · {Format.TotalDuration(seconds)} · {Format.Size(bytes)}" : "");
        ShowNoMatches = total > 0 && n == 0;
        ShowEmptyState = total == 0 && !IsSyncing && _model is not null;
    }

    // ───── Roots: the storage root and the folder sources ─────

    /// <summary>An operation's answer: a refusal goes to the warning line, a note to the log; a change reaches the tab through the runner and the model.</summary>
    private bool Run(OperationResult result)
    {
        if (!result.Ok)
        {
            WarningText = result.Message ?? "não deu";
            return false;
        }
        if (result.Message is { } note)
            _host.Log(LogLevel.Info, note);
        _sync.Request(SyncMode.Incremental);
        _service.RequestReconcile();
        return true;
    }

    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta pra entrar na Biblioteca (fica onde está)", Multiselect = false };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;
        var path = PathUtil.Normalize(dialog.FolderName);
        if (!Run(_operations.AddFolder(path)))
            return;
        RefreshRoots();
        Select(Roots.FirstOrDefault(o => o.Path is { } p && PathUtil.IsSamePath(p, path)) ?? Roots[0]);
    }

    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Arquivos pra entrar na Biblioteca (ficam onde estão)", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true)
            return;
        var notes = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            var result = _operations.AddFile(file);
            if (!result.Ok)
                notes.Add($"{Path.GetFileName(file)}: {result.Message}");
            else if (result.Message is { } note)
                notes.Add($"{Path.GetFileName(file)}: {note}");
        }
        WarningText = string.Join("\n", notes);
        _sync.Request(SyncMode.Incremental);
        _service.RequestReconcile();
    }

    private async void ImportFiles()
    {
        var files = new Microsoft.Win32.OpenFileDialog { Title = "Arquivos pra copiar pra biblioteca", Multiselect = true, CheckFileExists = true };
        if (files.ShowDialog(Application.Current?.MainWindow) != true || files.FileNames.Length == 0)
            return;
        var root = _operations.StorageRoot;
        var into = new Microsoft.Win32.OpenFolderDialog { Title = $"Pasta de destino (dentro de {root})", InitialDirectory = root, Multiselect = false };
        if (into.ShowDialog(Application.Current?.MainWindow) != true || string.IsNullOrWhiteSpace(into.FolderName))
            return;
        await RunImportAsync($"copiando {files.FileNames.Length} arquivo(s)", progress => _operations.ImportFilesAsync(files.FileNames, into.FolderName, progress, _host.ShutdownToken));
    }

    private async void ImportFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta pra copiar pra biblioteca (inteira, como está)", Multiselect = false };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;
        await RunImportAsync($"copiando {Path.GetFileName(PathUtil.Normalize(dialog.FolderName))}", progress => _operations.ImportFolderAsync(dialog.FolderName, progress, _host.ShutdownToken));
    }

    private async Task RunImportAsync(string what, Func<IProgress<double>, Task<OperationResult>> import)
    {
        if (IsImporting)
            return;
        IsImporting = true;
        ImportStatusText = what + "…";
        try
        {
            var progress = new Progress<double>(p => ImportStatusText = $"{what}… {p:0}%");
            Run(await import(progress));
        }
        catch (OperationCanceledException)
        {
            // closing: nothing to say
        }
        catch (Exception ex)
        {
            WarningText = "Não consegui importar: " + ex.Message;
            _host.Log(LogLevel.Warning, $"importar: {ex.Message}");
        }
        finally
        {
            IsImporting = false;
            ImportStatusText = "";
        }
    }

    private void ChangeStorageRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "A pasta da biblioteca: pra onde Importar copia e onde Criar pasta cria", InitialDirectory = _operations.StorageRoot, Multiselect = false };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;
        if (!Run(_operations.SetStorageRoot(dialog.FolderName)))
            return;
        RefreshRoots();
        OnPropertyChanged(nameof(SelectedRootPath));
    }

    private void RemoveSelectedRoot()
    {
        if (SelectedRoot is not { Root: { ItemId: { } id } })
            return;
        if (!Run(_operations.RemoveSource(id)))
            return;
        RefreshRoots();
        Select(Roots[0]);
    }

    private void RenameSelectedRoot()
    {
        if (SelectedRoot is not { Root: { ItemId: { } id } root } || string.IsNullOrWhiteSpace(RootNameEdit))
            return;
        var name = RootNameEdit.Trim();
        if (name == root.Name)
            return;
        var result = _operations.RenameSource(id, name);
        if (!result.Ok)
        {
            WarningText = result.Message ?? "não deu";
            return;
        }
        RefreshRoots();
        Select(Roots.FirstOrDefault(o => o.Path is { } p && PathUtil.IsSamePath(p, root.Path)) ?? Roots[0]);
        _service.RequestReconcile();   // the rows carry the name (subtitle, search): the next model rebuilds them
    }

    /// <summary>Folders the model could not observe (disk off, gone): the chip gets its ⚠.</summary>
    private HashSet<string> UnavailablePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_model is null)
            return paths;
        foreach (var problem in _model.Problems)
        {
            if (problem.Code is "sourceUnavailable" or "sourceBroken" or "storageRootUnknown" or "storageRootMissing" && problem.Path is { } path)
                paths.Add(PathUtil.Normalize(path));
        }
        return paths;
    }

    private void RefreshRoots()
    {
        var selectedPath = SelectedRoot?.Path;
        var roots = SourceRefs();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        if (_model is not null)
        {
            foreach (var file in _model.Index.Entries)
            {
                total++;
                var root = roots.Where(r => PathUtil.IsDescendantOrSame(file.FullPath, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
                if (root is not null)
                    counts[root.Path] = counts.GetValueOrDefault(root.Path) + 1;
            }
        }
        var unavailable = UnavailablePaths();

        Roots.Clear();
        Roots.Add(new RootOption(null, total, false, Select));
        foreach (var root in roots)
            Roots.Add(new RootOption(root, counts.GetValueOrDefault(root.Path), unavailable.Contains(root.Path), Select));
        Select(Roots.FirstOrDefault(o => o.Path is { } p && selectedPath is not null && PathUtil.IsSamePath(p, selectedPath)) ?? Roots[0]);
    }

    private void Select(RootOption option)
    {
        foreach (var o in Roots)
            o.SetSelected(ReferenceEquals(o, option));
        var filterChanged = SelectedRoot?.Path != option.Path;   // the options are rebuilt often; the path is what filters
        SelectedRoot = option;
        if (filterChanged)
            Reproject();
    }

    // ───── Rows: what the context menu and a drop do ─────

    /// <summary>The rows an action on <paramref name="row"/> applies to: the selection when the row is part of it, else the row alone.</summary>
    internal IReadOnlyList<LibraryRow> TargetsOf(LibraryRow row)
        => _selection.Any(r => r.Key == row.Key) ? _selection : [row];

    /// <summary>A folder inside the storage root: where Criar pasta can create.</summary>
    internal bool IsInsideStorageRoot(LibraryRow row)
        => row.Path.Length > 0 && PathUtil.IsDescendantOrSame(row.Path, _operations.StorageRoot);

    /// <summary>The groups the "Adicionar a" submenu offers, with their path in the tree; checked for the groups every target already belongs to.</summary>
    internal IReadOnlyList<GroupChoice> GroupChoicesFor(IReadOnlyList<LibraryRow> targets)
    {
        var choices = new List<GroupChoice>();
        if (_model is null)
            return choices;
        var itemIds = targets.Select(t => t.ItemId).Where(id => id is not null).ToList();
        void Walk(GroupView group, string prefix)
        {
            var label = prefix.Length == 0 ? group.Name : prefix + " › " + group.Name;
            var all = itemIds.Count == targets.Count && targets.Count > 0 && itemIds.All(id => group.Members.Any(m => m.Id == id));
            choices.Add(new GroupChoice(group.Id, label, all));
            foreach (var child in group.Children)
                Walk(child, label);
        }
        foreach (var group in _model.Groups)
            Walk(group, "");
        return choices;
    }

    /// <summary>"Adicionar a <grupo>" and a drop on a group: a membership per physical target; nothing moves.</summary>
    internal void AddToGroup(string groupId, IReadOnlyList<LibraryRow> rows)
    {
        var targets = rows.Where(r => r.IsPhysical && !r.IsTop).Select(r => new Target(r.Path, r.IsFolder)).ToList();
        if (targets.Count == 0)
            return;
        var result = _operations.AddToGroup(groupId, targets);
        if (!result.Ok)
        {
            WarningText = result.Message ?? "não deu";
            return;
        }
        if (_model?.FindGroup(groupId) is { } group)
            OpenGroup(group);
        _service.RequestReconcile();
    }

    /// <summary>The group and the chain above it open, so what was just put in it is on screen.</summary>
    private void OpenGroup(GroupView group)
    {
        var chain = new List<string>();
        for (var g = group; g is not null; g = g.Parent)
            chain.Insert(0, "g:" + g.Id);
        for (var i = 1; i <= chain.Count; i++)
            _expanded.Add(string.Join("|", chain.Take(i)));
    }

    internal void RemoveFromGroup(LibraryRow row)
    {
        if (row.InGroup is not { } group || row.ItemId is not { } itemId)
            return;
        var result = _operations.RemoveFromGroup(group.Id, itemId);
        if (!result.Ok)
            WarningText = result.Message ?? "não deu";
        _service.RequestReconcile();
    }

    /// <summary>A group dropped on another group (or on "Tudo" = the top): only the group tree changes.</summary>
    internal string? WhyNotMoveGroup(LibraryRow group, LibraryRow? onto)
    {
        if (group.GroupId is not { } id)
            return "não é um grupo";
        var newParent = onto is { IsGroup: true } ? onto.GroupId : null;
        if (onto is not null && !onto.IsGroup && !onto.IsTop)
            return "um grupo só entra em outro grupo";
        return _operations.WhyNotMoveGroup(id, newParent);
    }

    internal void MoveGroup(LibraryRow group, LibraryRow? onto)
    {
        if (group.GroupId is not { } id)
            return;
        var newParent = onto is { IsGroup: true } ? onto.GroupId : null;
        var result = _operations.MoveGroup(id, newParent);
        if (!result.Ok)
            WarningText = result.Message ?? "não deu";
        else if (onto?.Group is { } target)
            OpenGroup(target);
        _service.RequestReconcile();
    }

    internal void RenameGroup(LibraryRow row)
    {
        if (row.Group is not { } group)
            return;
        AskName("Nome do grupo:", name => Run(_operations.RenameGroup(group.Id, name)), group.Name);
    }

    internal void CreateSubgroup(LibraryRow row)
    {
        if (row.Group is not { } group)
            return;
        AskName($"Nome do grupo novo (em {group.Name}):", name =>
        {
            if (Run(_operations.CreateGroup(name, group.Id)))
                OpenGroup(group);
        });
    }

    internal void DeleteGroup(LibraryRow row)
    {
        if (row.Group is not { } group)
            return;
        Run(_operations.DeleteGroup(group.Id));
    }

    internal void CreateFolderIn(LibraryRow row)
    {
        var parent = row.IsTop ? null : row.Path;
        AskName($"Nome da pasta nova (em {(parent ?? _operations.StorageRoot)}):", name =>
        {
            if (Run(_operations.CreatePhysicalFolder(parent, name)))
                _expanded.Add(row.Key);
        });
    }

    /// <summary>"Mover fisicamente…": the destination asked for, then the move; the tree shows it after the scan.</summary>
    internal async void MovePhysically(IReadOnlyList<LibraryRow> rows)
    {
        var targets = rows.Where(r => r.IsPhysical && !r.IsTop).Select(r => new Target(r.Path, r.IsFolder)).ToList();
        if (targets.Count == 0)
            return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = targets.Count == 1 ? $"Mover \"{Path.GetFileName(targets[0].Path)}\" para…" : $"Mover {targets.Count} itens para…",
            InitialDirectory = _operations.StorageRoot,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;
        try
        {
            Run(await _operations.MovePhysicalAsync(targets, dialog.FolderName, _host.ShutdownToken));
        }
        catch (OperationCanceledException)
        {
            // closing
        }
        catch (Exception ex)
        {
            WarningText = "Não consegui mover: " + ex.Message;
            _host.Log(LogLevel.Warning, $"mover: {ex.Message}");
        }
    }

    /// <summary>"Localizar…": the person points at where the item is now.</summary>
    internal void Relocate(LibraryRow row)
    {
        if (row.Item is not { } item)
            return;
        string? path;
        if (item.IsFolder)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Onde está \"{row.Title}\" agora?", Multiselect = false };
            path = dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Onde está \"{row.Title}\" agora?", CheckFileExists = true };
            path = dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
        }
        if (string.IsNullOrWhiteSpace(path))
            return;
        Run(_operations.Relocate(item.Id, path));
    }

    internal void RemoveSource(LibraryRow row)
    {
        if (row.Item is not { IsSource: true } item)
            return;
        if (Run(_operations.RemoveSource(item.Id)))
            RefreshRoots();
    }

    // ───── Sync feedback ─────

    private void OnProgress(ScanProgress p)
    {
        _progress = p.Phase == "listando" ? $"listando pastas ({p.Done})" : $"{p.Phase} {p.Done}/{p.Total}";
        if (p.Ready is { Count: > 0 } ready)
        {
            _early.AddRange(ready);   // the just-downloaded files, shown before the scan is over
            Reproject();
        }
        UpdateStatus();
    }

    private void OnCompleted(ScanResult result)
    {
        _fault = null;
        if (result.Added + result.Changed + result.Removed > 0 || result.Full)
            _host.Log(LogLevel.Info, $"biblioteca sincronizada{(result.Full ? " (completa)" : "")}: +{result.Added} · ~{result.Changed} · −{result.Removed}"
                + (result.Unreadable > 0 ? $" · {result.Unreadable} sem tags legíveis" : "")
                + (result.InaccessibleDirectories.Count > 0 ? $" · {result.InaccessibleDirectories.Count} pasta(s) sem acesso (mantidas)" : "")
                + $" · {Format.Count(result.Index.Entries.Count)} no total");
    }

    private void UpdateStatus()
    {
        var roots = Roots.Count - 1;   // the chips minus "Todas": the storage root and the folder sources
        var files = _model?.Index.Entries.Count ?? 0;
        if (IsSyncing)
        {
            StatusText = "Sincronizando… " + (_progress ?? "");
        }
        else if (_manifest.ReadOnly)
        {
            StatusText = "Biblioteca de uma versão mais nova do plugin — só leitura. Atualize o plugin.";
        }
        else
        {
            var status = _manifest.Status;
            var when = _manifest.LastSync is { } t ? t.ToLocalTime() : (DateTime?)null;
            var last = when is null ? "ainda não sincronizada"
                : when.Value.Date == DateTime.Today ? $"sincronizada às {when:HH:mm}" : $"sincronizada em {when:d} {when:HH:mm}";
            var groups = _model?.AllGroups.Count ?? 0;
            StatusText = $"{Format.Count(files)} {(files == 1 ? "arquivo" : "arquivos")} em {Math.Max(roots, 1)} {(roots <= 1 ? "pasta" : "pastas")}"
                + (groups > 0 ? $" · {groups} {(groups == 1 ? "grupo" : "grupos")}" : "") + $" · {last}"
                + (status == SyncStatus.Pending ? " · há mudanças pra sincronizar" : status == SyncStatus.Failed ? " · a última sincronização não terminou" : "");
        }

        var warnings = new List<string>();
        if (_fault is not null)
            warnings.Add("A sincronização falhou: " + _fault);
        if (_model is not null)
        {
            foreach (var problem in _model.Problems.Where(p => p.Severity >= Severity.Warning))
                warnings.Add(problem.Message);
        }
        WarningText = string.Join("\n", warnings);
        ShowEmptyState = files == 0 && !IsSyncing && _model is not null;
    }

    // ───── Opening things ─────

    private void Open(LibraryRow row)
    {
        if (row.IsContainer)
        {
            ToggleExpand(row);
            return;
        }
        if (row.IsMissing || row.Path.Length == 0)
            return;
        try
        {
            if (_manifest.Player is { } player)
                Process.Start(new ProcessStartInfo(player) { ArgumentList = { row.Path }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WarningText = $"Não consegui abrir {row.FileName}: {ex.Message}";
            _host.Log(LogLevel.Warning, $"abrir {row.Path}: {ex.Message}");
        }
    }

    private void ShowInFolder(LibraryRow row)
    {
        if (row.IsGroup || row.Path.Length == 0)
            return;
        try
        {
            if (row.IsFolder)
                OpenFolder(row.Path);
            else if (File.Exists(row.Path))
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{row.Path}\"", UseShellExecute = false });
            else
                OpenFolder(Path.GetDirectoryName(row.Path));
        }
        catch (Exception ex)
        {
            WarningText = $"Não consegui abrir a pasta: {ex.Message}";
        }
    }

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{path}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            WarningText = $"Não consegui abrir a pasta: {ex.Message}";
        }
    }

    private void ChoosePlayer()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Programa que abre os arquivos (VLC, foobar2000, mpv…)",
            Filter = "Programas (*.exe)|*.exe|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true || string.IsNullOrWhiteSpace(dialog.FileName))
            return;
        SetPlayer(dialog.FileName);
    }

    private void SetPlayer(string? path)
    {
        _manifest.Update(m => m.Player = path);
        OnPropertyChanged(nameof(PlayerText));
        OnPropertyChanged(nameof(HasPlayer));
        NotifyVlc();
    }

    /// <summary>The cone. A VLC already here (installed, or the portable one) gets the question first; none → straight to the download.</summary>
    private void AskOrDownloadVlc()
    {
        if (_vlc is null || IsVlcBusy)
            return;
        CheckInstalledVlc();
        if (_installedVlc is { } installed)
        {
            VlcPromptText = $"Já existe um VLC instalado no Windows ({Path.GetDirectoryName(installed)}). Usar esse, ou baixar o portátil (80 MB) mesmo assim?";
            VlcPromptUseLabel = "Usar o instalado";
            VlcPromptDownloadLabel = "Baixar mesmo assim";
            ShowVlcPrompt = true;
        }
        else if (_vlc.IsInstalled)
        {
            VlcPromptText = $"O VLC portátil já está em {_vlc.Directory}{(_vlc.InstalledVersion is { } v ? " (" + v + ")" : "")}. Usar esse, ou baixar de novo?";
            VlcPromptUseLabel = "Usar esse";
            VlcPromptDownloadLabel = "Baixar de novo";
            ShowVlcPrompt = true;
        }
        else
        {
            DownloadVlc();
        }
    }

    /// <summary>The download runs on the pool; the status text next to the buttons shows where it is and the tab keeps working. Done → it is the player.</summary>
    private async void DownloadVlc()
    {
        if (_vlc is null || IsVlcBusy)
            return;
        VlcStatusText = "";
        IsVlcBusy = true;
        try
        {
            var progress = new Progress<VlcProgress>(p => VlcStatusText = "VLC: " + p);
            var version = await _vlc.InstallAsync(progress, _host.ShutdownToken);
            _host.Log(LogLevel.Info, $"VLC {version} instalado em {_vlc.Directory}");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            WarningText = "Não consegui baixar o VLC: " + ex.Message;
            _host.Log(LogLevel.Warning, $"baixar o VLC: {ex.Message}");
            return;
        }
        finally
        {
            IsVlcBusy = false;
            VlcStatusText = "";
        }
        SetPlayer(_vlc.ExePath);
    }

    /// <summary>Runs on the UI thread. A bug here must not take the app down: an unhandled exception in a dispatcher callback is fatal.</summary>
    private void Post(Action action)
    {
        void Guarded()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _host.Log(LogLevel.Error, $"erro na aba Biblioteca: {ex.Message}", ex);
            }
        }

        if (_dispatcher.CheckAccess())
            Guarded();
        else
            _dispatcher.BeginInvoke(Guarded, DispatcherPriority.Background);
    }
}

/// <summary>An <see cref="ObservableCollection{T}"/> that can swap its whole content with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
