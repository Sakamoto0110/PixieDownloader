using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Pixie.Library.Catalog;
using Pixie.Library.Mvvm;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Library;

/// <summary>A folder of the catalogue as a toggle on top of the tab — "Músicas · 1.284"; the first one is every folder.</summary>
public sealed class RootOption : ObservableObject
{
    private readonly Action<RootOption> _select;
    private bool _isSelected;

    internal RootOption(LibraryRoot? root, int count, bool unavailable, Action<RootOption> select)
    {
        Root = root;
        Path = root is null ? null : PathUtil.Normalize(root.Path);
        Name = root?.Name ?? "Todas";
        Count = count;
        Unavailable = unavailable;
        Label = $"{Name} · {Format.Count(count)}" + (unavailable ? " ⚠" : "");
        ToolTip = root is null ? "Tudo que está no catálogo" : root.Path + (unavailable ? "\nPasta não encontrada — disco desligado? O que já estava no catálogo continua listado." : "");
        _select = select;
    }

    internal LibraryRoot? Root { get; }
    public string? Path { get; }
    public string Name { get; }
    public int Count { get; }
    public bool Unavailable { get; }
    public string Label { get; }
    public string ToolTip { get; }
    public bool IsAll => Root is null;

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

/// <summary>One entry of the sort combo.</summary>
public sealed record SortOption(string Label, System.Collections.IComparer Comparer)
{
    public override string ToString() => Label;
}

/// <summary>One entry of the type combo.</summary>
public sealed record KindOption(string Label, FileKind? Kind)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Biblioteca" tab. The runner's events come in on pool threads and are marshalled here; the rows are a
/// flat observable list under a <see cref="ListCollectionView"/> that does the filtering (folder, type, search)
/// and the sorting, so the list box virtualises over the whole catalogue. Opening the tab shows what the index
/// had and asks for an incremental sync behind it.
/// </summary>
public sealed class LibraryTabViewModel : ObservableObject
{
    private readonly LibraryManifest _manifest;
    private readonly LibrarySync _sync;
    private readonly IPluginHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, LibraryRow> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _filterTimer;
    private readonly ListCollectionView _view;
    private bool _opened;
    private DateTime _lastAutoCheck = DateTime.MinValue;
    private string? _progress;
    private IReadOnlyList<string> _unavailable = [];
    private string? _fault;

    internal LibraryTabViewModel(LibraryManifest manifest, LibrarySync sync, IPluginHost host)
    {
        _manifest = manifest;
        _sync = sync;
        _host = host;
        _dispatcher = Dispatcher.CurrentDispatcher;

        SortOptions =
        [
            new SortOption("Recentes", Comparer<LibraryRow>.Create((a, b) => b.Added.CompareTo(a.Added) is var c && c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase))),
            new SortOption("Título", Comparer<LibraryRow>.Create((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase))),
            new SortOption("Artista", Comparer<LibraryRow>.Create((a, b) => string.Compare(a.Subtitle, b.Subtitle, StringComparison.CurrentCultureIgnoreCase) is var c && c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase))),
            new SortOption("Duração", Comparer<LibraryRow>.Create((a, b) => b.Duration.CompareTo(a.Duration))),
            new SortOption("Tamanho", Comparer<LibraryRow>.Create((a, b) => b.Size.CompareTo(a.Size))),
            new SortOption("Pasta", Comparer<LibraryRow>.Create((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase))),
        ];
        _selectedSort = SortOptions[0];
        KindOptions = [new KindOption("Tudo", null), new KindOption("Áudio", FileKind.Audio), new KindOption("Vídeo", FileKind.Video), new KindOption("Outros", FileKind.Other)];
        _selectedKind = KindOptions[0];

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        _view.CustomSort = _selectedSort.Comparer;
        _view.Filter = o => o is LibraryRow row && Passes(row);

        _filterTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); RefreshView(); };

        SyncCommand = new RelayCommand(() => _sync.Request(SyncMode.Full), () => HasRoots && !IsSyncing);
        AddRootCommand = new RelayCommand(AddRoot);
        RemoveRootCommand = new RelayCommand(RemoveSelectedRoot, () => SelectedRoot is { IsAll: false });
        RenameRootCommand = new RelayCommand(RenameSelectedRoot, () => SelectedRoot is { IsAll: false } && !string.IsNullOrWhiteSpace(RootNameEdit));
        OpenRootFolderCommand = new RelayCommand(() => OpenFolder(SelectedRoot?.Path), () => SelectedRoot is { IsAll: false, Unavailable: false });
        OpenCommand = new RelayCommand<LibraryRow>(Open);
        ShowInFolderCommand = new RelayCommand<LibraryRow>(ShowInFolder);
        ChoosePlayerCommand = new RelayCommand(ChoosePlayer);
        UseDefaultPlayerCommand = new RelayCommand(() => { _manifest.Update(m => m.Player = null); OnPropertyChanged(nameof(PlayerText)); OnPropertyChanged(nameof(HasPlayer)); }, () => HasPlayer);

        // The rows are built where the event arrives (a pool thread: they are immutable) and folded in on the UI thread.
        _sync.IndexLoaded += index => { var rows = BuildRows(index); Post(() => ApplyRows(rows)); };
        _sync.Progress += p => Post(() => OnProgress(p));
        _sync.Completed += result => { var rows = BuildRows(result.Index); Post(() => OnCompleted(result, rows)); };
        _sync.Faulted += ex => Post(() => { _fault = ex.Message; UpdateStatus(); });
        _sync.RunStateChanged += () => Post(() =>
        {
            IsSyncing = _sync.IsRunning;
            if (!IsSyncing)
                _progress = null;
            UpdateStatus();
            CommandManager.InvalidateRequerySuggested();
        });

        RefreshRoots();
        UpdateStatus();
    }

    // ───── Lists and selection ─────

    public BulkObservableCollection<LibraryRow> Rows { get; } = [];
    public ICollectionView RowsView => _view;
    public ObservableCollection<RootOption> Roots { get; } = [];
    public IReadOnlyList<SortOption> SortOptions { get; }
    public IReadOnlyList<KindOption> KindOptions { get; }

    private RootOption? _selectedRoot;
    public RootOption? SelectedRoot
    {
        get => _selectedRoot;
        private set
        {
            if (!SetProperty(ref _selectedRoot, value))
                return;
            RootNameEdit = value?.Root?.Name ?? "";
            OnPropertyChanged(nameof(HasSelectedRoot));
            OnPropertyChanged(nameof(SelectedRootPath));
        }
    }

    public bool HasSelectedRoot => SelectedRoot is { IsAll: false };
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
            _view.CustomSort = value.Comparer;
            UpdateCounts();
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
            RefreshView();
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

    // ───── Status ─────

    private bool _isSyncing;
    public bool IsSyncing { get => _isSyncing; private set => SetProperty(ref _isSyncing, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _warningText = "";
    public string WarningText { get => _warningText; private set { if (SetProperty(ref _warningText, value)) OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => WarningText.Length > 0;

    private string _countText = "";
    public string CountText { get => _countText; private set => SetProperty(ref _countText, value); }

    private bool _hasRoots;
    public bool HasRoots { get => _hasRoots; private set { if (SetProperty(ref _hasRoots, value)) OnPropertyChanged(nameof(ShowEmptyState)); } }
    public bool ShowEmptyState => !HasRoots;

    private bool _showNoMatches;
    public bool ShowNoMatches { get => _showNoMatches; private set => SetProperty(ref _showNoMatches, value); }

    private bool _showSettings;
    public bool ShowSettings { get => _showSettings; set => SetProperty(ref _showSettings, value); }

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
            if (HasRoots)
            {
                _manifest.MarkPending();
                _sync.Request(SyncMode.Full);
            }
        }
    }

    public string PlayerText => _manifest.Player is { } p ? Path.GetFileNameWithoutExtension(p) + "  (" + p + ")" : "padrão do Windows";
    public bool HasPlayer => _manifest.Player is not null;

    // ───── Commands ─────

    public ICommand SyncCommand { get; }
    public ICommand AddRootCommand { get; }
    public ICommand RemoveRootCommand { get; }
    public ICommand RenameRootCommand { get; }
    public ICommand OpenRootFolderCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand ChoosePlayerCommand { get; }
    public ICommand UseDefaultPlayerCommand { get; }

    // ───── Lifecycle ─────

    /// <summary>The view got shown (every time the tab is selected): show the index, then check the folders behind it.</summary>
    public void OnTabOpened()
    {
        if (!_opened)
        {
            _opened = true;
            _ = _sync.EnsureLoadedAsync();
        }
        // The check is one stat per known folder — cheap, but not for free on a sleeping disk, so not on every tab flick.
        if (!_manifest.AutoSync || _manifest.Roots.Count == 0 || DateTime.UtcNow - _lastAutoCheck < TimeSpan.FromSeconds(5))
            return;
        _lastAutoCheck = DateTime.UtcNow;
        _sync.Request(SyncMode.Incremental);
    }

    /// <summary>The plugin added a root on its own (a download landed outside every folder).</summary>
    internal void RootsChanged() => Post(() => { RefreshRoots(); UpdateStatus(); });

    // ───── Roots ─────

    private void AddRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta pra entrar na Biblioteca", Multiselect = false };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;
        var path = PathUtil.Normalize(dialog.FolderName);
        if (_manifest.Roots.Any(r => PathUtil.Normalize(r.Path).Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            WarningText = "Essa pasta já está no catálogo.";
            return;
        }
        var root = new LibraryRoot(UniqueRootName(RootNameFor(path)), path);
        _manifest.Update(m => m.Roots.Add(root));
        _manifest.MarkPending();
        _host.Log(LogLevel.Info, $"pasta \"{root.Name}\" ({root.Path}) entrou no catálogo");
        RefreshRoots();
        Select(Roots.FirstOrDefault(o => o.Path == path) ?? Roots[0]);
        _sync.Request(SyncMode.Incremental);
    }

    private void RemoveSelectedRoot()
    {
        if (SelectedRoot is not { Root: { } root })
            return;
        _manifest.Update(m => m.Roots.RemoveAll(r => r.Path == root.Path));
        _manifest.MarkPending();
        _host.Log(LogLevel.Info, $"pasta \"{root.Name}\" saiu do catálogo (os arquivos ficam onde estão)");
        RefreshRoots();
        Select(Roots[0]);
        _sync.Request(SyncMode.Incremental);
    }

    private void RenameSelectedRoot()
    {
        if (SelectedRoot is not { Root: { } root } || string.IsNullOrWhiteSpace(RootNameEdit))
            return;
        var name = RootNameEdit.Trim();
        if (name == root.Name)
            return;
        _manifest.Update(m =>
        {
            var i = m.Roots.FindIndex(r => r.Path == root.Path);
            if (i >= 0)
                m.Roots[i] = root with { Name = name };
        });
        RefreshRoots();
        Select(Roots.FirstOrDefault(o => o.Path == PathUtil.Normalize(root.Path)) ?? Roots[0]);
        // The rows carry the root name (subtitle, search): rebuild them from the index we have.
        if (_sync.Index is { } index)
            ApplyRows(BuildRows(index));
    }

    internal static string RootNameFor(string path)
    {
        var name = Path.GetFileName(PathUtil.Normalize(path));
        return string.IsNullOrEmpty(name) ? path.TrimEnd(Path.DirectorySeparatorChar) : name;
    }

    private string UniqueRootName(string name)
    {
        var taken = new HashSet<string>(_manifest.Roots.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (var i = 2; ; i++)
        {
            if (!taken.Contains($"{name} ({i})"))
                return $"{name} ({i})";
        }
    }

    private void RefreshRoots()
    {
        var selectedPath = SelectedRoot?.Path;
        var roots = _manifest.Roots;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
            counts[row.RootPath] = counts.GetValueOrDefault(row.RootPath) + 1;
        var unavailable = new HashSet<string>(_unavailable.Select(PathUtil.Normalize), StringComparer.OrdinalIgnoreCase);

        Roots.Clear();
        Roots.Add(new RootOption(null, Rows.Count, false, Select));
        foreach (var root in roots)
        {
            var path = PathUtil.Normalize(root.Path);
            Roots.Add(new RootOption(root, counts.GetValueOrDefault(path), unavailable.Contains(path), Select));
        }
        HasRoots = roots.Count > 0;
        Select(Roots.FirstOrDefault(o => o.Path == selectedPath) ?? Roots[0]);
    }

    private void Select(RootOption option)
    {
        foreach (var o in Roots)
            o.SetSelected(ReferenceEquals(o, option));
        var filterChanged = SelectedRoot?.Path != option.Path;   // the options are rebuilt often; the path is what filters
        SelectedRoot = option;
        if (filterChanged)
            RefreshView();
    }

    // ───── Rows ─────

    private List<LibraryRow> BuildRows(IndexData index)
    {
        var roots = _manifest.Roots;
        return index.Entries.Select(e => new LibraryRow(e, RootOf(roots, e.Path))).ToList();
    }

    /// <summary>
    /// Folds a whole index (as rows) into the list by path: new rows in, rows whose file or folder changed
    /// replaced in place, rows of files that went removed. A small diff is applied row by row, so the list box
    /// keeps its scroll and selection across a sync; a big one (the first load, a full re-tag) is one reset —
    /// the view sorts once instead of inserting thousands of times. (The source cannot change under a
    /// DeferRefresh: the view throws.)
    /// </summary>
    private void ApplyRows(List<LibraryRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<(LibraryRow? Old, LibraryRow New)>();
        foreach (var row in rows)
        {
            seen.Add(row.Path);
            if (_byPath.TryGetValue(row.Path, out var existing))
            {
                if (existing.Entry == row.Entry && existing.RootName == row.RootName && existing.RootPath == row.RootPath)
                    continue;
                changes.Add((existing, row));
            }
            else
            {
                changes.Add((null, row));
            }
        }
        var gone = Rows.Where(r => !seen.Contains(r.Path)).ToList();
        if (changes.Count + gone.Count == 0)
        {
            RefreshRoots();
            UpdateCounts();
            UpdateStatus();
            return;
        }

        if (changes.Count + gone.Count > 200 || Rows.Count == 0)
        {
            Rows.ReplaceAll(rows);
            _byPath.Clear();
            foreach (var row in rows)
                _byPath[row.Path] = row;
        }
        else
        {
            foreach (var (old, row) in changes)
            {
                if (old is null)
                    Rows.Add(row);
                else
                    Rows[Rows.IndexOf(old)] = row;
                _byPath[row.Path] = row;
            }
            foreach (var row in gone)
            {
                Rows.Remove(row);
                _byPath.Remove(row.Path);
            }
        }
        RefreshRoots();
        UpdateCounts();
        UpdateStatus();
    }

    /// <summary>The just-downloaded files, shown before the scan is over.</summary>
    private void Upsert(IReadOnlyList<LibraryEntry> entries)
    {
        var roots = _manifest.Roots;
        foreach (var entry in entries)
        {
            var row = new LibraryRow(entry, RootOf(roots, entry.Path));
            if (_byPath.TryGetValue(entry.Path, out var existing))
                Rows[Rows.IndexOf(existing)] = row;
            else
                Rows.Add(row);
            _byPath[entry.Path] = row;
        }
        RefreshRoots();
        UpdateCounts();
    }

    private static LibraryRoot? RootOf(IReadOnlyList<LibraryRoot> roots, string path)
        => roots.Where(r => PathUtil.IsUnder(path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();

    private bool Passes(LibraryRow row)
        => (SelectedRoot is not { Path: { } rootPath } || row.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
           && (SelectedKind.Kind is not { } kind || row.Kind == kind)
           && SearchText.Matches(row.SearchKey, FilterText);

    private void RefreshView()
    {
        _view.Refresh();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var n = 0;
        var seconds = 0d;
        var bytes = 0L;
        foreach (var o in _view)
        {
            if (o is not LibraryRow row)
                continue;
            n++;
            seconds += row.Duration;
            bytes += row.Size;
        }
        var filtered = n != Rows.Count;
        CountText = Rows.Count == 0 ? "" : (filtered ? $"{Format.Count(n)} de {Format.Count(Rows.Count)}" : Format.Count(n) + (n == 1 ? " arquivo" : " arquivos"))
            + (n > 0 ? $" · {Format.TotalDuration(seconds)} · {Format.Size(bytes)}" : "");
        ShowNoMatches = HasRoots && Rows.Count > 0 && n == 0;
    }

    // ───── Sync feedback ─────

    private void OnProgress(ScanProgress p)
    {
        _progress = p.Phase == "listando" ? $"listando pastas ({p.Done})" : $"{p.Phase} {p.Done}/{p.Total}";
        if (p.Ready is { Count: > 0 } ready)
            Upsert(ready);
        UpdateStatus();
    }

    private void OnCompleted(ScanResult result, List<LibraryRow> rows)
    {
        _fault = null;
        _unavailable = result.UnavailableRoots;
        ApplyRows(rows);
        if (result.Added + result.Changed + result.Removed > 0 || result.Full)
            _host.Log(LogLevel.Info, $"biblioteca sincronizada{(result.Full ? " (completa)" : "")}: +{result.Added} · ~{result.Changed} · −{result.Removed}"
                + (result.Unreadable > 0 ? $" · {result.Unreadable} sem tags legíveis" : "")
                + $" · {Format.Count(result.Index.Entries.Count)} no total");
    }

    private void UpdateStatus()
    {
        var roots = _manifest.Roots;
        if (IsSyncing)
        {
            StatusText = "Sincronizando… " + (_progress ?? "");
        }
        else if (roots.Count == 0)
        {
            StatusText = "Nenhuma pasta no catálogo.";
        }
        else
        {
            var status = _manifest.Status;
            var when = _manifest.LastSync is { } t ? t.ToLocalTime() : (DateTime?)null;
            var last = when is null ? "ainda não sincronizada"
                : when.Value.Date == DateTime.Today ? $"sincronizada às {when:HH:mm}" : $"sincronizada em {when:d} {when:HH:mm}";
            StatusText = $"{Format.Count(Rows.Count)} {(Rows.Count == 1 ? "arquivo" : "arquivos")} em {roots.Count} {(roots.Count == 1 ? "pasta" : "pastas")} · {last}"
                + (status == SyncStatus.Pending ? " · há mudanças pra sincronizar" : status == SyncStatus.Failed ? " · a última sincronização não terminou" : "");
        }

        var warnings = new List<string>();
        if (_fault is not null)
            warnings.Add("A sincronização falhou: " + _fault);
        foreach (var path in _unavailable)
            warnings.Add($"Pasta não encontrada: {path} — disco desligado? O que já estava no catálogo continua listado.");
        WarningText = string.Join("\n", warnings);
    }

    // ───── Opening things ─────

    private void Open(LibraryRow row)
    {
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
        try
        {
            if (File.Exists(row.Path))
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
        _manifest.Update(m => m.Player = dialog.FileName);
        OnPropertyChanged(nameof(PlayerText));
        OnPropertyChanged(nameof(HasPlayer));
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
