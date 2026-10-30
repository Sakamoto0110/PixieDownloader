using System.IO;
using YtDlpCore;

namespace Pixie.Library.Catalog;

internal sealed record OperationResult(bool Ok, string? Id = null, string? Message = null)
{
    public static OperationResult Done(string? id, string? message = null) => new(true, id, message);
    public static OperationResult Refused(string message) => new(false, null, message);
}

/// <summary>A file or folder an operation is applied to, as the tab hands it over: where it is, and which of the two it is.</summary>
internal sealed record Target(string Path, bool IsFolder);

/// <summary>
/// The commands of the tab, each a check-and-commit on the manifest or a change on disk that the scanner then
/// sees: Adicionar (a source pointing where the thing is), Importar (a copy into the storage root), Criar pasta
/// (a real folder inside it), the groups and their memberships (logical only), Mover fisicamente (the one
/// operation that moves files), and the housekeeping — rename, remove, relocate, the storage root, ids in
/// conflict. Every refusal comes back as a message; nothing throws for a rule. Identity is created here and
/// only here: an item exists from the moment something logical refers to it.
/// </summary>
internal sealed class LibraryOperations
{
    private readonly LibraryManifest _manifest;
    private readonly LibraryStore _store;
    private readonly IFileProbe _probe;
    private readonly Action<LogLevel, string> _log;

    public LibraryOperations(LibraryManifest manifest, LibraryStore store, IFileProbe probe, Action<LogLevel, string> log)
    {
        _manifest = manifest;
        _store = store;
        _probe = probe;
        _log = log;
    }

    /// <summary>The library's own folder, resolved.</summary>
    public string StorageRoot => _manifest.StorageRoot;

    /// <summary>The system's Music folder: the default storage root, and where a write goes when the configured one refuses.</summary>
    public string MusicFolder => PathUtil.Normalize(_manifest.MusicFolder);

    private ScanPlan Plan() => ScanPlan.Build(_manifest.Snapshot(), _manifest.MusicFolder);

    // ───── Adicionar: a source where the thing is ─────

    public OperationResult AddFile(string path) => AddSource(path, isFolder: false);

    public OperationResult AddFolder(string path) => AddSource(path, isFolder: true);

    private OperationResult AddSource(string path, bool isFolder)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (!PathUtil.TryNormalizeAbsolute(path, out var normalized, out var reason))
            return Refuse($"caminho inválido: {reason}");
        var snapshot = _manifest.Snapshot();
        var existing = snapshot.FindItemByPath(normalized);
        if (existing is not null && existing.IsFolder != isFolder)
            return Refuse(isFolder ? "isso é um arquivo, não uma pasta" : "isso é uma pasta, não um arquivo");
        if (existing is { IsSource: true })
            return isFolder ? Refuse("essa pasta já está no catálogo") : OperationResult.Done(existing.Id, "esse arquivo já estava no catálogo");
        var plan = ScanPlan.Build(snapshot, _manifest.MusicFolder);
        if (!isFolder && plan.SourceOf(normalized) is { } covering)
            return OperationResult.Done(null, $"esse arquivo já está na biblioteca (dentro de {covering.Path})");

        var probe = _probe.Stat(normalized);
        if (probe.Outcome == ProbeOutcome.Found && probe.IsFolder != isFolder)
            return Refuse(isFolder ? "isso é um arquivo, não uma pasta" : "isso é uma pasta, não um arquivo");
        var name = UniqueSourceName(snapshot, LegacyMigration.NameFor(normalized));
        string id;
        if (existing is not null)
        {
            // a group member becomes a source too: the same item, one identity
            id = existing.Id;
            _manifest.Update(m =>
            {
                foreach (var item in m.Items.Where(i => i.Id == existing.Id))
                    item.Source = new SourceData { Name = name };
            });
        }
        else
        {
            var item = new ItemData
            {
                Id = NewId(),
                Kind = isFolder ? ItemKinds.Folder : ItemKinds.File,
                Locator = LocatorOf(normalized, probe),
                Source = new SourceData { Name = name },
            };
            id = item.Id;
            _manifest.Update(m => m.Items.Add(item));
        }
        if (!isFolder && probe.Outcome == ProbeOutcome.Found)
            _store.Inbox.Append(normalized);
        _manifest.MarkPending();
        _log(LogLevel.Info, $"{(isFolder ? "pasta" : "arquivo")} \"{name}\" ({normalized}) entrou no catálogo");
        return OperationResult.Done(id);
    }

    /// <summary>Only the name the tab shows; nothing changes on disk.</summary>
    public OperationResult RenameSource(string itemId, string name)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (string.IsNullOrWhiteSpace(name))
            return Refuse("a pasta precisa de um nome");
        var snapshot = _manifest.Snapshot();
        if (snapshot.FindItem(itemId) is not { IsSource: true })
            return Refuse("a fonte não existe");
        var trimmed = name.Trim();
        _manifest.Update(m =>
        {
            foreach (var item in m.Items.Where(i => i.Id == itemId && i.Source is not null))
                item.Source!.Name = trimmed;
        });
        return OperationResult.Done(itemId);
    }

    /// <summary>
    /// "Remover do catálogo": the source is no longer walked. Nothing on disk is touched. The item stays if it
    /// belongs to a group (it just stops being observed on its own); otherwise it goes.
    /// </summary>
    public OperationResult RemoveSource(string itemId)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        var snapshot = _manifest.Snapshot();
        if (snapshot.FindItem(itemId) is not { IsSource: true } item)
            return Refuse("a fonte não existe");
        var member = snapshot.Memberships.Any(mb => mb.ItemId == itemId);
        _manifest.Update(m =>
        {
            if (member)
            {
                foreach (var i in m.Items.Where(i => i.Id == itemId))
                    i.Source = null;
            }
            else
            {
                m.Items.RemoveAll(i => i.Id == itemId);
            }
        });
        _manifest.MarkPending();
        _log(LogLevel.Info, $"\"{item.Source!.Name}\" saiu do catálogo (os arquivos ficam onde estão)");
        return OperationResult.Done(null, "removido do catálogo");
    }

    /// <summary>"Localizar…": the person points at where the thing is now; the locator is rewritten.</summary>
    public OperationResult Relocate(string itemId, string newPath)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (!PathUtil.TryNormalizeAbsolute(newPath, out var path, out var reason))
            return Refuse($"caminho inválido: {reason}");
        var snapshot = _manifest.Snapshot();
        if (snapshot.FindItem(itemId) is not { } item)
            return Refuse("o item não existe");
        var probe = _probe.Stat(path);
        if (probe.Outcome != ProbeOutcome.Found)
            return Refuse("não há nada nesse caminho");
        if (probe.IsFolder != item.IsFolder)
            return Refuse(item.IsFolder ? "isso é um arquivo, não uma pasta" : "isso é uma pasta, não um arquivo");
        var locator = LocatorOf(path, probe);
        _manifest.Update(m =>
        {
            foreach (var i in m.Items.Where(i => i.Id == itemId))
                i.Locator = locator;
        });
        _manifest.MarkPending();
        return OperationResult.Done(itemId);
    }

    // ───── Importar: a copy into the storage root ─────

    /// <summary>
    /// Copies each file into <paramref name="destination"/> (a folder inside the storage root; null = the root
    /// itself). A name already there gets " (2)" — never overwritten, never merged. A destination that refuses
    /// (no permission) sends the copy to the Music folder instead, and says so; the configured root stays.
    /// </summary>
    public async Task<OperationResult> ImportFilesAsync(IReadOnlyList<string> sources, string? destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (ResolveDestination(destination) is not { } target)
            return Refuse("o destino tem que ser uma pasta dentro da pasta da biblioteca");
        var copied = new List<string>();
        var fellBack = false;
        var n = 0;
        foreach (var source in sources)
        {
            if (ct.IsCancellationRequested)
                return Refuse(copied.Count == 0 ? "importação cancelada; nada foi copiado" : $"importação cancelada depois de {copied.Count} arquivo(s)");
            if (!PathUtil.TryNormalizeAbsolute(source, out var path, out var reason))
                return Refuse($"caminho inválido: {reason}");
            if (!File.Exists(path))
                return Refuse($"{Path.GetFileName(path)}: o arquivo não existe");
            if (PathUtil.IsDescendantOrSame(path, StorageRoot))
                return Refuse($"{Path.GetFileName(path)} já está dentro da pasta da biblioteca");
            var slice = new Progress<double>(p => progress?.Report((n * 100.0 + p) / sources.Count));
            string final;
            try
            {
                final = await CopyWithFallbackAsync(path, isFolder: false, target, slice, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Refuse(copied.Count == 0 ? "importação cancelada; nada foi copiado" : $"importação cancelada depois de {copied.Count} arquivo(s)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log(LogLevel.Warning, $"importar {path}: {ex.Message}");
                return Refuse($"não consegui copiar {Path.GetFileName(path)}: {ex.Message}");
            }
            fellBack |= !PathUtil.IsDescendantOrSame(final, target);
            copied.Add(final);
            _store.Inbox.Append(final);
            n++;
        }
        if (copied.Count > 0)
            _manifest.MarkPending();
        _log(LogLevel.Info, $"{copied.Count} arquivo(s) importado(s) para {(fellBack ? MusicFolder : target)}");
        return OperationResult.Done(null, fellBack ? $"sem permissão em {target} — foi para {MusicFolder}" : null);
    }

    /// <summary>Copies the folder whole, as a child of the storage root — no new source, no group: it is part of the library's own tree now.</summary>
    public async Task<OperationResult> ImportFolderAsync(string source, IProgress<double>? progress, CancellationToken ct)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (!PathUtil.TryNormalizeAbsolute(source, out var path, out var reason))
            return Refuse($"caminho inválido: {reason}");
        if (!Directory.Exists(path))
            return Refuse("a pasta não existe");
        if (PathUtil.IsDescendantOrSame(path, StorageRoot))
            return Refuse("essa pasta já está dentro da pasta da biblioteca");
        if (PathUtil.IsDescendantOrSame(StorageRoot, path))
            return Refuse("essa pasta contém a pasta da biblioteca — não dá pra copiar a biblioteca pra dentro dela mesma");
        string final;
        try
        {
            final = await CopyWithFallbackAsync(path, isFolder: true, StorageRoot, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Refuse("importação cancelada; nada foi copiado");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log(LogLevel.Warning, $"importar {path}: {ex.Message}");
            return Refuse($"não consegui copiar: {ex.Message}");
        }
        _manifest.MarkPending();
        _log(LogLevel.Info, $"\"{Path.GetFileName(path)}\" importado de {path} para {final}");
        var fellBack = !PathUtil.IsDescendantOrSame(final, StorageRoot);
        return OperationResult.Done(null, fellBack ? $"sem permissão em {StorageRoot} — foi para {MusicFolder}" : null);
    }

    /// <summary>The copy, tried at the destination and, when that refuses, at the Music folder — which then has to be observed too.</summary>
    private async Task<string> CopyWithFallbackAsync(string source, bool isFolder, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var name = StorageCopy.UniqueName(destination, Path.GetFileName(source));
        try
        {
            return await StorageCopy.CopyInAsync(source, isFolder, StorageRoot, destination, name, progress, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) when (!PathUtil.IsDescendantOrSame(destination, MusicFolder))
        {
            _log(LogLevel.Warning, $"sem permissão em {destination}: a cópia vai para {MusicFolder}");
            EnsureMusicObserved();
            name = StorageCopy.UniqueName(MusicFolder, Path.GetFileName(source));
            return await StorageCopy.CopyInAsync(source, isFolder, MusicFolder, MusicFolder, name, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A destination for a copy or a new folder: inside the storage root, or the root itself.</summary>
    private string? ResolveDestination(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return StorageRoot;
        if (!PathUtil.TryNormalizeAbsolute(destination, out var path, out _))
            return null;
        return PathUtil.IsDescendantOrSame(path, StorageRoot) ? path : null;
    }

    /// <summary>After a fallback, the Music folder is where the file went: it has to be scanned, so it becomes a source unless a source already covers it.</summary>
    private void EnsureMusicObserved()
    {
        if (Plan().SourceOf(MusicFolder) is not null)
            return;
        var added = AddFolder(MusicFolder);
        if (!added.Ok)
            _log(LogLevel.Warning, $"a pasta Music não pôde entrar no catálogo: {added.Message}");
    }

    // ───── Criar pasta: a real folder ─────

    /// <summary>A folder on disk, in <paramref name="parent"/> (inside the storage root; null = the root). The scanner shows it.</summary>
    public OperationResult CreatePhysicalFolder(string? parent, string name)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (string.IsNullOrWhiteSpace(name))
            return Refuse("a pasta precisa de um nome");
        if (ResolveDestination(parent) is not { } destination)
            return Refuse("a pasta nova tem que ficar dentro da pasta da biblioteca");
        var clean = StorageCopy.Sanitize(name);
        var path = Path.Combine(destination, clean);
        if (Directory.Exists(path) || File.Exists(path))
            return Refuse($"já existe \"{clean}\" em {destination}");
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (UnauthorizedAccessException) when (!PathUtil.IsDescendantOrSame(destination, MusicFolder))
        {
            EnsureMusicObserved();
            path = Path.Combine(MusicFolder, clean);
            if (Directory.Exists(path) || File.Exists(path))
                return Refuse($"sem permissão em {destination}, e já existe \"{clean}\" em {MusicFolder}");
            try { Directory.CreateDirectory(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Refuse($"não consegui criar a pasta: {ex.Message}"); }
            _manifest.MarkPending();
            return OperationResult.Done(null, $"sem permissão em {destination} — a pasta foi criada em {MusicFolder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse($"não consegui criar a pasta: {ex.Message}");
        }
        _manifest.MarkPending();
        return OperationResult.Done(null);
    }

    // ───── Groups ─────

    public OperationResult CreateGroup(string name, string? parentId = null)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (string.IsNullOrWhiteSpace(name))
            return Refuse("o grupo precisa de um nome");
        var snapshot = _manifest.Snapshot();
        if (parentId is not null && snapshot.FindGroup(parentId) is null)
            return Refuse(snapshot.ConflictedIds.Contains(parentId) ? "o grupo pai tem id em conflito" : "o grupo pai não existe");
        var group = new GroupData { Id = NewId(), Name = name.Trim(), ParentGroupId = parentId };
        _manifest.Update(m => m.Groups.Add(group));
        return OperationResult.Done(group.Id);
    }

    public OperationResult RenameGroup(string groupId, string name)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (string.IsNullOrWhiteSpace(name))
            return Refuse("o grupo precisa de um nome");
        if (_manifest.Snapshot().FindGroup(groupId) is null)
            return Refuse("o grupo não existe");
        var trimmed = name.Trim();
        _manifest.Update(m =>
        {
            foreach (var group in m.Groups.Where(g => g.Id == groupId))
                group.Name = trimmed;
        });
        return OperationResult.Done(groupId);
    }

    /// <summary>Why the group cannot go under <paramref name="newParentId"/>, or null when it can: itself, a group below it, a missing one.</summary>
    public string? WhyNotMoveGroup(string groupId, string? newParentId)
    {
        var snapshot = _manifest.Snapshot();
        if (snapshot.FindGroup(groupId) is null)
            return "o grupo não existe";
        if (newParentId is null)
            return null;
        if (newParentId == groupId)
            return "um grupo não pode ficar dentro de si mesmo";
        if (snapshot.FindGroup(newParentId) is null)
            return snapshot.ConflictedIds.Contains(newParentId) ? "o grupo de destino tem id em conflito" : "o grupo de destino não existe";
        // up from the destination: reaching the group would close a cycle
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = snapshot.FindGroup(newParentId); current is not null; current = snapshot.FindGroup(current.ParentGroupId))
        {
            if (current.Id == groupId)
                return "um grupo não pode ficar dentro de si mesmo";
            if (!seen.Add(current.Id))
                break;   // an existing cycle above: the reconciler reports it; not this move's doing
        }
        return null;
    }

    /// <summary>Puts the group under another (or at the top). Only the group tree changes: no membership moves.</summary>
    public OperationResult MoveGroup(string groupId, string? newParentId)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (WhyNotMoveGroup(groupId, newParentId) is { } why)
            return Refuse(why);
        _manifest.Update(m =>
        {
            foreach (var group in m.Groups.Where(g => g.Id == groupId))
                group.ParentGroupId = newParentId;
        });
        return OperationResult.Done(groupId);
    }

    /// <summary>Removes the group, the groups below it and their memberships. No item, no file is touched; items left without any state go too.</summary>
    public OperationResult DeleteGroup(string groupId)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        var snapshot = _manifest.Snapshot();
        if (snapshot.FindGroup(groupId) is null && !snapshot.ConflictedIds.Contains(groupId))
            return Refuse("o grupo não existe");
        var doomed = new HashSet<string>(StringComparer.Ordinal) { groupId };
        bool grew;
        do
        {
            grew = false;
            foreach (var g in snapshot.Groups)
            {
                if (!string.IsNullOrEmpty(g.Id) && g.ParentGroupId is not null && doomed.Contains(g.ParentGroupId) && doomed.Add(g.Id))
                    grew = true;
            }
        } while (grew);
        _manifest.Update(m =>
        {
            m.Groups.RemoveAll(g => doomed.Contains(g.Id));
            m.Memberships.RemoveAll(mb => doomed.Contains(mb.GroupId));
            PruneItems(m);
        });
        return OperationResult.Done(null, doomed.Count > 1 ? $"{doomed.Count} grupos removidos" : "grupo removido");
    }

    // ───── Memberships ─────

    /// <summary>
    /// The item for what is at <paramref name="path"/>, created if nothing logical referred to it yet — the moment
    /// a discovered file or folder gains persistent identity. Idempotent: the same path is the same item.
    /// </summary>
    public OperationResult EnsureItem(string path, bool isFolder)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (!PathUtil.TryNormalizeAbsolute(path, out var normalized, out var reason))
            return Refuse($"caminho inválido: {reason}");
        var snapshot = _manifest.Snapshot();
        if (snapshot.FindItemByPath(normalized) is { } existing)
            return existing.IsFolder == isFolder ? OperationResult.Done(existing.Id) : Refuse(isFolder ? "isso é um arquivo, não uma pasta" : "isso é uma pasta, não um arquivo");
        var probe = _probe.Stat(normalized);
        if (probe.Outcome == ProbeOutcome.Found && probe.IsFolder != isFolder)
            return Refuse(isFolder ? "isso é um arquivo, não uma pasta" : "isso é uma pasta, não um arquivo");
        var item = new ItemData { Id = NewId(), Kind = isFolder ? ItemKinds.Folder : ItemKinds.File, Locator = LocatorOf(normalized, probe) };
        _manifest.Update(m => m.Items.Add(item));
        return OperationResult.Done(item.Id);
    }

    /// <summary>
    /// "Adicionar a": a membership for each target — the folder alone when a folder and something inside it are
    /// both selected (the folder already stands for its subtree). Nothing moves, nothing is copied, no other
    /// membership is touched. Returns how many were new.
    /// </summary>
    public OperationResult AddToGroup(string groupId, IReadOnlyList<Target> targets)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (_manifest.Snapshot().FindGroup(groupId) is null)
            return Refuse("o grupo não existe");
        var ids = new List<string>();
        foreach (var target in Outermost(targets))
        {
            var ensured = EnsureItem(target.Path, target.IsFolder);
            if (!ensured.Ok)
                return ensured;
            ids.Add(ensured.Id!);
        }
        var added = 0;
        _manifest.Update(m =>
        {
            foreach (var id in ids)
            {
                if (m.Memberships.Any(mb => mb.GroupId == groupId && mb.ItemId == id))
                    continue;
                m.Memberships.Add(new MembershipData { GroupId = groupId, ItemId = id });
                added++;
            }
        });
        return OperationResult.Done(groupId, added == 0 ? "já estava no grupo" : added == 1 ? "adicionado ao grupo" : $"{added} adicionados ao grupo");
    }

    /// <summary>"Remover de <grupo>": the one membership goes; the item goes with it when nothing else refers to it.</summary>
    public OperationResult RemoveFromGroup(string groupId, string itemId)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        var snapshot = _manifest.Snapshot();
        if (!snapshot.Memberships.Any(mb => mb.GroupId == groupId && mb.ItemId == itemId))
            return Refuse("não está nesse grupo");
        _manifest.Update(m =>
        {
            m.Memberships.RemoveAll(mb => mb.GroupId == groupId && mb.ItemId == itemId);
            PruneItems(m);
        });
        return OperationResult.Done(itemId, "removido do grupo");
    }

    /// <summary>Items with no state left — no source role, no membership — have no reason to exist.</summary>
    private static void PruneItems(ManifestData m)
    {
        var referenced = new HashSet<string>(m.Memberships.Select(mb => mb.ItemId), StringComparer.Ordinal);
        m.Items.RemoveAll(i => !i.IsSource && !referenced.Contains(i.Id));
    }

    // ───── Mover fisicamente ─────

    /// <summary>
    /// Moves the targets (the outermost of them: a folder carries its subtree) into <paramref name="destination"/>,
    /// on disk. Refused whole when a name is already there — nothing is overwritten. Every item whose locator
    /// pointed under a moved path is pointed at the new place, memberships untouched; the scanner then sees
    /// the two folders that changed.
    /// </summary>
    public async Task<OperationResult> MovePhysicalAsync(IReadOnlyList<Target> targets, string destination, CancellationToken ct)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (!PathUtil.TryNormalizeAbsolute(destination, out var into, out var reason))
            return Refuse($"destino inválido: {reason}");
        if (!Directory.Exists(into))
            return Refuse("a pasta de destino não existe");
        var moves = new List<(string From, string To, bool IsFolder)>();
        foreach (var target in Outermost(targets))
        {
            if (!PathUtil.TryNormalizeAbsolute(target.Path, out var from, out var why))
                return Refuse($"caminho inválido: {why}");
            if (target.IsFolder ? !Directory.Exists(from) : !File.Exists(from))
                return Refuse($"{Path.GetFileName(from)} não está mais lá");
            if (target.IsFolder && PathUtil.IsDescendantOrSame(into, from))
                return Refuse($"{Path.GetFileName(from)} não pode ir pra dentro de si mesma");
            var to = Path.Combine(into, Path.GetFileName(from));
            if (PathUtil.IsSamePath(from, to))
                continue;   // already there
            if (Directory.Exists(to) || File.Exists(to))
                return Refuse($"já existe \"{Path.GetFileName(from)}\" em {into} — nada foi movido");
            moves.Add((from, to, target.IsFolder));
        }
        if (moves.Count == 0)
            return OperationResult.Done(null, "já está lá");

        var moved = new List<(string From, string To)>();
        foreach (var (from, to, isFolder) in moves)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await MoveAsync(from, to, isFolder, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log(LogLevel.Warning, $"mover {from} para {to}: {ex.Message}");
                RewriteLocators(moved);
                _manifest.MarkPending();
                return Refuse($"não consegui mover {Path.GetFileName(from)}: {ex.Message}" + (moved.Count > 0 ? $" ({moved.Count} já movido(s))" : ""));
            }
            moved.Add((from, to));
        }
        RewriteLocators(moved);
        _manifest.MarkPending();
        _log(LogLevel.Info, $"{moved.Count} item(ns) movido(s) para {into}");
        return OperationResult.Done(null, moved.Count == 1 ? "movido" : $"{moved.Count} movidos");
    }

    /// <summary>Same volume: a rename. Another volume: a copy that keeps the times, then the original goes.</summary>
    private static async Task MoveAsync(string from, string to, bool isFolder, CancellationToken ct)
    {
        if (!isFolder)
        {
            File.Move(from, to);   // across volumes too: copies, then deletes
            return;
        }
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(to)!;
            await StorageCopy.CopyInAsync(from, isFolder: true, parent, parent, Path.GetFileName(to), null, ct).ConfigureAwait(false);
            Directory.Delete(from, recursive: true);
        }
    }

    /// <summary>Every item under a moved path now points where it went — same item, same memberships; the identity on another volume is read again.</summary>
    private void RewriteLocators(IReadOnlyList<(string From, string To)> moved)
    {
        if (moved.Count == 0)
            return;
        var snapshot = _manifest.Snapshot();
        var rewrites = new Dictionary<string, LocatorData>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            if (!PathUtil.TryNormalizeAbsolute(item.Locator.LastKnownPath, out var old, out _))
                continue;
            foreach (var (from, to) in moved)
            {
                if (!PathUtil.IsDescendantOrSame(old, from))
                    continue;
                var relative = PathUtil.Relative(old, from);
                var now = relative.Length == 0 ? to : Path.Combine(to, relative);
                rewrites[item.Id] = LocatorOf(now, _probe.Stat(now));
                break;
            }
        }
        if (rewrites.Count == 0)
            return;
        _manifest.Update(m =>
        {
            foreach (var item in m.Items)
            {
                if (rewrites.TryGetValue(item.Id, out var locator))
                    item.Locator = locator;
            }
        });
    }

    /// <summary>The targets without those inside another target: a folder stands for its subtree, once.</summary>
    internal static IReadOnlyList<Target> Outermost(IReadOnlyList<Target> targets)
    {
        var normalized = targets.Where(t => PathUtil.TryNormalizeAbsolute(t.Path, out _, out _)).Select(t => new Target(PathUtil.Normalize(t.Path), t.IsFolder)).ToList();
        var kept = new List<Target>();
        foreach (var target in normalized)
        {
            var inside = normalized.Any(other => other.IsFolder && !ReferenceEquals(other, target) && PathUtil.IsProperDescendant(target.Path, other.Path));
            if (inside || kept.Any(k => PathUtil.IsSamePath(k.Path, target.Path)))
                continue;
            kept.Add(target);
        }
        return kept;
    }

    // ───── The storage root ─────

    /// <summary>"Mudar diretório padrão": only the configuration changes (null = the Music folder). What moves with it is not decided here.</summary>
    public OperationResult SetStorageRoot(string? path)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!PathUtil.TryNormalizeAbsolute(path, out var p, out var reason))
                return Refuse($"caminho inválido: {reason}");
            var probe = _probe.Stat(p);
            if (probe.Outcome == ProbeOutcome.Found && !probe.IsFolder)
                return Refuse("isso é um arquivo, não uma pasta");
            normalized = PathUtil.IsSamePath(p, MusicFolder) ? null : p;
        }
        _manifest.Update(m => m.StorageRoot = normalized);
        _manifest.MarkPending();
        return OperationResult.Done(null);
    }

    // ───── Conflicted ids: explicit, never automatic ─────

    /// <summary>Every item and group carrying <paramref name="id"/> gets a fresh id. Memberships naming the old id are not rewritten: they dangle, and the person repairs them knowing which.</summary>
    public OperationResult RegenerateConflictedIds(string id)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (!_manifest.Snapshot().ConflictedIds.Contains(id))
            return Refuse("esse id não está em conflito");
        _manifest.Update(m =>
        {
            foreach (var item in m.Items.Where(i => i.Id == id))
                item.Id = NewId();
            foreach (var group in m.Groups.Where(g => g.Id == id))
                group.Id = NewId();
        });
        _manifest.MarkPending();
        return OperationResult.Done(null);
    }

    /// <summary>Every item and group carrying <paramref name="id"/> is removed, with the groups under them and the memberships that named it.</summary>
    public OperationResult RemoveAllWithId(string id)
    {
        if (_manifest.ReadOnly)
            return Refuse(ReadOnlyMessage);
        if (!_manifest.Snapshot().ConflictedIds.Contains(id))
            return Refuse("esse id não está em conflito");
        _manifest.Update(m =>
        {
            m.Items.RemoveAll(i => i.Id == id);
            var doomed = new HashSet<string>(StringComparer.Ordinal) { id };
            bool grew;
            do
            {
                grew = false;
                foreach (var g in m.Groups)
                {
                    if (!string.IsNullOrEmpty(g.Id) && g.ParentGroupId is not null && doomed.Contains(g.ParentGroupId) && doomed.Add(g.Id))
                        grew = true;
                }
            } while (grew);
            m.Groups.RemoveAll(g => doomed.Contains(g.Id));
            m.Memberships.RemoveAll(mb => doomed.Contains(mb.GroupId) || mb.ItemId == id);
            PruneItems(m);
        });
        _manifest.MarkPending();
        return OperationResult.Done(null);
    }

    // ───── Downloads (and the legacy inbox replayed) ─────

    /// <summary>
    /// A file the app delivered: under the storage root or a source → inbox; outside everything → with
    /// <c>autoAddRoots</c>, its folder becomes a source and the file goes to the inbox. Returns whether the
    /// file will be indexed.
    /// </summary>
    public bool Deliver(string filePath, string? outputDirectory, DateTime? createdAt = null)
    {
        if (_manifest.ReadOnly)
            return false;
        if (!PathUtil.TryNormalizeAbsolute(filePath, out var path, out _))
            return false;
        if (!_manifest.Extensions.Contains(Path.GetExtension(path)))
            return false;   // a GIF, say, with GIFs not in the catalogue
        if (Plan().SourceOf(path) is null)
        {
            if (!_manifest.AutoAddRoots)
            {
                _log(LogLevel.Debug, $"{Path.GetFileName(path)} ficou fora do catálogo (pasta não cadastrada)");
                return false;
            }
            // The app's output folder is the folder the user would add by hand; the file's own folder if the
            // request did not say (or said something the file is not under — the template may have moved it).
            var folder = outputDirectory;
            if (string.IsNullOrWhiteSpace(folder) || !PathUtil.TryNormalizeAbsolute(folder, out var f, out _) || !PathUtil.IsDescendantOrSame(path, f))
                folder = Path.GetDirectoryName(path)!;
            var added = AddFolder(folder);
            if (!added.Ok)
            {
                _log(LogLevel.Warning, $"{Path.GetFileName(path)} ficou fora do catálogo: {added.Message}");
                return false;
            }
            _log(LogLevel.Info, $"pasta \"{LegacyMigration.NameFor(folder)}\" ({folder}) entrou no catálogo por causa de um download");
        }
        _store.Inbox.Append(path, createdAt);
        _manifest.MarkPending();
        return true;
    }

    // ───── helpers ─────

    private const string ReadOnlyMessage = "o manifesto está só pra leitura (versão mais nova do plugin)";

    private static string UniqueSourceName(ManifestSnapshot snapshot, string name)
    {
        var taken = new HashSet<string>(snapshot.Items.Where(i => i.IsSource).Select(i => i.Source!.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (var i = 2; ; i++)
        {
            if (!taken.Contains($"{name} ({i})"))
                return $"{name} ({i})";
        }
    }

    private static LocatorData LocatorOf(string path, ProbeResult probe) => new()
    {
        LastKnownPath = path,
        VolumeSerial = probe.Outcome == ProbeOutcome.Found ? probe.VolumeSerial : null,
        FileId = probe.Outcome == ProbeOutcome.Found ? probe.FileId : null,
        Size = probe.Outcome == ProbeOutcome.Found && !probe.IsFolder ? probe.Size : null,
        Mtime = probe.Outcome == ProbeOutcome.Found && !probe.IsFolder ? probe.Modified : null,
    };

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static OperationResult Refuse(string message) => OperationResult.Refused(message);
}
