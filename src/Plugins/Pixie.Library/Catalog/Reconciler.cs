using System.IO;

namespace Pixie.Library.Catalog;

/// <summary>
/// Joins the manifest, the index tree and the file system into one resolved model, and says what should change
/// — without changing anything. Reads the manifest snapshot and the tree; asks the probe about the storage root
/// and about every item (a few dozen at most: sources and group members), never about the thousands of files
/// the tree lists. Every state is a function of these inputs and of this reconciliation alone — nothing is
/// remembered between two calls, which is why "Broken" is decided on the spot. Identity is never invented here:
/// an item is created by a person's action and only a person removes it.
/// </summary>
internal static class Reconciler
{
    public static ReconcileResult Build(ReconcileInput input, IFileProbe probe)
    {
        var manifest = input.Manifest;
        var index = input.Index;
        var plan = input.Plan;
        var actions = new List<ReconcileAction>();
        var problems = new List<Problem>();

        var probes = new Dictionary<string, ProbeResult>(StringComparer.Ordinal);
        ProbeResult ProbeOnce(string path)
        {
            var key = PathUtil.Canonical(path);
            if (!probes.TryGetValue(key, out var result))
                probes[key] = result = probe.Stat(path);
            return result;
        }

        // ───── 1. The storage root ─────
        var storageRoot = plan.StorageRoot;
        var storageRootTree = LibraryScanner.FindSource(index, storageRoot);
        var rootProbe = ProbeOnce(storageRoot);
        var storageRootPresence = rootProbe.Outcome switch
        {
            ProbeOutcome.Found when rootProbe.IsFolder => Presence.Present,
            ProbeOutcome.Found => Presence.TypeMismatch,
            ProbeOutcome.Inaccessible => Presence.Inaccessible,
            _ => probe.PathRootExists(storageRoot) ? Presence.Missing : Presence.Unknown,
        };
        switch (storageRootPresence)
        {
            case Presence.Missing:
                problems.Add(new Problem("storageRootMissing", Severity.Warning, $"A pasta da biblioteca ({storageRoot}) não existe — Importar ou Criar pasta a criam.", Path: storageRoot));
                break;
            case Presence.Unknown:
                problems.Add(new Problem("storageRootUnknown", Severity.Warning, $"O disco da pasta da biblioteca ({storageRoot}) não está disponível — desligado? O que já estava no catálogo continua listado.", Path: storageRoot));
                break;
            case Presence.TypeMismatch:
                problems.Add(new Problem("storageRootMissing", Severity.Error, $"No lugar da pasta da biblioteca ({storageRoot}) há um arquivo.", Path: storageRoot));
                break;
            case Presence.Inaccessible:
                problems.Add(new Problem("storageRootInaccessible", Severity.Warning, $"A pasta da biblioteca ({storageRoot}) existe, mas não deu pra ler.", Path: storageRoot));
                break;
        }

        // ───── 2. Items with a usable id, resolved ─────
        foreach (var id in manifest.ConflictedIds.OrderBy(i => i, StringComparer.Ordinal))
        {
            var names = manifest.Items.Where(i => i.Id == id).Select(i => $"{i.Kind} \"{Path.GetFileName(i.Locator.LastKnownPath)}\"")
                .Concat(manifest.Groups.Where(g => g.Id == id).Select(g => $"grupo \"{g.Name}\""));
            problems.Add(new Problem("conflictedId", Severity.Error, $"Id repetido no manifesto: {string.Join(", ", names)} — nenhum deles resolve até o conflito ser desfeito", id));
        }
        var items = new Dictionary<string, ItemView>(StringComparer.Ordinal);
        var orderedItems = new List<ItemView>();
        foreach (var data in manifest.Items)
        {
            if (string.IsNullOrEmpty(data.Id))
            {
                problems.Add(new Problem("emptyId", Severity.Error, $"Item sem id no manifesto ({data.Kind} \"{data.Locator.LastKnownPath}\"); ignorado até ganhar um"));
                continue;
            }
            if (manifest.ConflictedIds.Contains(data.Id))
                continue;
            if (!ItemKinds.IsKnown(data.Kind))
            {
                problems.Add(new Problem("unknownKind", Severity.Warning, $"Item \"{data.Locator.LastKnownPath}\" de tipo desconhecido ({data.Kind}) — preservado, escrito por uma versão mais nova?", data.Id));
                continue;
            }
            var item = new ItemView(data);
            items[data.Id] = item;
            orderedItems.Add(item);
            Resolve(item, index, probe, ProbeOnce, actions);
        }

        // ───── 3. Groups: parents, cycles ─────
        var groups = new Dictionary<string, GroupView>(StringComparer.Ordinal);
        var orderedGroups = new List<GroupView>();
        foreach (var data in manifest.Groups)
        {
            if (string.IsNullOrEmpty(data.Id))
            {
                problems.Add(new Problem("emptyId", Severity.Error, $"Grupo \"{data.Name}\" sem id no manifesto; ignorado até ganhar um"));
                continue;
            }
            if (manifest.ConflictedIds.Contains(data.Id))
                continue;
            var group = new GroupView(data);
            groups[data.Id] = group;
            orderedGroups.Add(group);
        }
        foreach (var group in orderedGroups)
        {
            if (group.ParentGroupId is null)
                continue;
            if (manifest.ConflictedIds.Contains(group.ParentGroupId))
                Detach(group, "structure.parentConflicted", "o grupo pai tem id em conflito");
            else if (!groups.ContainsKey(group.ParentGroupId))
                Detach(group, "structure.missingParent", "o grupo pai não existe mais");
        }
        foreach (var group in orderedGroups.Where(g => g.DetachedReason is null))
        {
            // walk up; a repeat is a cycle
            var seen = new HashSet<string>(StringComparer.Ordinal) { group.Id };
            var current = group;
            while (current.ParentGroupId is not null && groups.TryGetValue(current.ParentGroupId, out var parent))
            {
                if (!seen.Add(parent.Id))
                {
                    Detach(group, "structure.parentCycle", "ciclo de grupos: o grupo está dentro de si mesmo");
                    break;
                }
                current = parent;
            }
        }
        var topLevel = new List<GroupView>();
        foreach (var group in orderedGroups)
        {
            if (group.DetachedReason is not null || group.ParentGroupId is null)
            {
                topLevel.Add(group);   // a detached group is listed at the top, with its reason
                continue;
            }
            var parent = groups[group.ParentGroupId];
            group.Parent = parent;
            parent._children.Add(group);
        }
        var attached = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<GroupView>(topLevel.Where(g => g.DetachedReason is null));
        while (stack.Count > 0)
        {
            var group = stack.Pop();
            if (!attached.Add(group.Id))
                continue;
            foreach (var child in group._children)
                stack.Push(child);
        }
        foreach (var group in orderedGroups)
            group.Attached = attached.Contains(group.Id);   // inside a detached group = not attached either; it stays under that parent
        foreach (var group in orderedGroups.Where(g => g.DetachedReason is not null))
            problems.Add(new Problem("structure", Severity.Warning, $"Grupo \"{group.Name}\": {group.DetachedReason} — mostrado no topo", group.Id));

        // ───── 4. Memberships ─────
        var pairs = new HashSet<(string, string)>();
        foreach (var membership in manifest.Memberships)
        {
            if (!groups.TryGetValue(membership.GroupId, out var group) || !items.TryGetValue(membership.ItemId, out var item))
            {
                problems.Add(new Problem("membership.dangling", Severity.Warning, $"Uma membership aponta pra um grupo ou item que não existe ({membership.GroupId} → {membership.ItemId}); mantida, sem efeito", membership.ItemId));
                continue;
            }
            if (!pairs.Add((group.Id, item.Id)))
                continue;
            group._members.Add(item);
            item._groups.Add(group);
        }

        // ───── 5. Problems the tree and the items raise ─────
        foreach (var item in orderedItems.Where(i => i.IsSource))
        {
            if (item.Presence == Presence.Unknown && item.Resolution == Resolution.Unverifiable)
                problems.Add(new Problem("sourceUnavailable", Severity.Warning, $"Pasta não disponível: {item.ResolvedPath} — disco desligado? O que já estava no catálogo continua listado.", item.Id, item.ResolvedPath));
            else if (item.Resolution == Resolution.Broken)
                problems.Add(new Problem("sourceBroken", Severity.Error, $"{(item.IsFolder ? "Pasta" : "Arquivo")} não encontrado: {item.ResolvedPath} — Localizar… ou Remover do catálogo.", item.Id, item.ResolvedPath));
        }
        foreach (var dir in index.InaccessibleDirectories)
            problems.Add(new Problem("inaccessibleDirectory", Severity.Warning, $"Não consegui ler a pasta {dir} na última varredura; o que o catálogo sabia dela foi mantido", Path: dir));

        // ───── 6. Status and actions ─────
        var planChanged = index.ScannedAt is null || LibraryScanner.RulesChanged(index, plan);
        if (manifest.ReadOnly)
        {
            actions.Clear();   // a manifest from a newer plugin: a model to look at, nothing to write, nothing to ask
        }
        else
        {
            if (planChanged)
                actions.Add(new RequestScan(ScanReason.ScanPlanChanged, Full: true));
            else if (manifest.Status is SyncStatus.Pending or SyncStatus.Failed)
                actions.Add(new RequestScan(ScanReason.Pending, Full: manifest.Status == SyncStatus.Failed));
        }
        var status = new ModelStatus(planChanged, manifest.ReadOnly, storageRootPresence);
        var sources = orderedItems.Where(i => i.IsSource).ToList();
        var model = new LibraryModel(storageRoot, storageRootTree, sources, items, topLevel, groups, problems, status, plan, manifest, index);
        return new ReconcileResult(model, actions);
    }

    // ───── one item: the tree, then the disk, then the volume ─────

    private static void Resolve(ItemView item, IndexData index, IFileProbe probe, Func<string, ProbeResult> probeOnce, List<ReconcileAction> actions)
    {
        var locator = item.Data.Locator;
        if (!PathUtil.TryNormalizeAbsolute(locator.LastKnownPath, out var path, out var reason))
        {
            item.Resolution = Resolution.StructuralConflict;
            item._self.Add(new Diagnostic("structure.invalidLocator", Severity.Error, $"caminho inválido: {reason}"));
            return;
        }
        item.ResolvedPath = path;
        Attach(item, index, path);

        var result = probeOnce(path);
        item.Probe = result;
        switch (result.Outcome)
        {
            case ProbeOutcome.Found when result.IsFolder != item.IsFolder:
                item.Presence = Presence.TypeMismatch;
                item.Resolution = Resolution.StructuralConflict;
                item._self.Add(new Diagnostic("presence.typeMismatch", Severity.Error, item.IsFolder ? "no lugar da pasta há um arquivo" : "no lugar do arquivo há uma pasta"));
                return;
            case ProbeOutcome.Found:
                item.Presence = Presence.Present;
                if (locator.FileId is not null && result.FileId is not null && !string.Equals(locator.FileId, result.FileId, StringComparison.OrdinalIgnoreCase))
                {
                    item.Resolution = Resolution.Replaced;
                    item._self.Add(new Diagnostic("link.replaced", Severity.Info, "o arquivo neste caminho foi substituído (re-download, tag editor…); continua sendo este caminho"));
                    actions.Add(new RewriteLocator(item.Id, LocatorFrom(path, result)));
                }
                else
                {
                    item.Resolution = Resolution.Resolved;
                    if (locator.FileId is null && result.FileId is not null || locator.Size != (result.IsFolder ? null : result.Size) || locator.Mtime != (result.IsFolder ? null : result.Modified))
                        actions.Add(new RewriteLocator(item.Id, LocatorFrom(path, result)));   // fingerprint learned or refreshed, silently
                }
                return;
            case ProbeOutcome.Inaccessible:
                item.Presence = Presence.Inaccessible;
                item.Resolution = Resolution.Resolved;
                item._self.Add(new Diagnostic("presence.inaccessible", Severity.Warning, "existe, mas não deu pra ler (permissão ou arquivo em uso)"));
                return;
        }

        // Missing: is the volume there to say so?
        var volumeAvailable = locator.VolumeSerial is { } serial ? probe.IsVolumeReady(serial, path) : probe.PathRootExists(path);
        if (!volumeAvailable)
        {
            item.Presence = Presence.Unknown;
            item.Resolution = Resolution.Unverifiable;
            item._self.Add(new Diagnostic("link.unverifiable", Severity.Warning, "o disco não está disponível — desligado? O que o catálogo sabia continua listado"));
            return;
        }
        item.Presence = Presence.Missing;
        if (locator.VolumeSerial is not null && locator.FileId is not null)
        {
            // The tree first: a file the last scan listed elsewhere with the same id moved inside the library
            // (a tree hit at the old path is the stale listing of this very file, not a find). Then the volume.
            string? found = null;
            if (item.IsFile && FindInTree(index, locator.VolumeSerial, locator.FileId) is { } inTree && !PathUtil.IsSamePath(inTree, path) && probeOnce(inTree).Outcome == ProbeOutcome.Found)
                found = inTree;
            found ??= probe.FindByFileId(locator.VolumeSerial, locator.FileId, path);
            if (found is not null && !PathUtil.IsSamePath(found, path))
            {
                var at = probeOnce(found);
                if (at.Outcome == ProbeOutcome.Found && at.IsFolder == item.IsFolder)
                {
                    item.ResolvedPath = found;
                    item.Presence = Presence.Present;
                    item.Probe = at;
                    item.Resolution = Resolution.Relocated;
                    item._self.Add(new Diagnostic("link.relocated", Severity.Info, $"movido para {found}"));
                    Attach(item, index, found);
                    actions.Add(new RewriteLocator(item.Id, LocatorFrom(found, at)));
                    return;
                }
            }
        }
        item.Resolution = Resolution.Broken;
        item._self.Add(new Diagnostic("link.broken", Severity.Error, item.IsFolder
            ? "a pasta não está mais onde estava e não foi encontrada no disco — Localizar… ou Remover"
            : "o arquivo não está mais onde estava e não foi encontrado no disco — Localizar… ou Remover"));
        actions.Add(new RequestManualRelocation(item.Id, "não encontrado"));
    }

    /// <summary>The tree's node at the item's path, and the source tree when the item is walked on its own.</summary>
    private static void Attach(ItemView item, IndexData index, string path)
    {
        item.File = item.IsFile ? index.FindFile(path) : null;
        item.Folder = item.IsFolder ? index.FindFolder(path) : null;
        item.Tree = item.IsSource ? LibraryScanner.FindSource(index, path) : null;
    }

    private static string? FindInTree(IndexData index, string volumeSerial, string fileId)
    {
        foreach (var file in index.Entries)
        {
            if (string.Equals(file.FileId, fileId, StringComparison.OrdinalIgnoreCase) && string.Equals(file.VolumeSerial, volumeSerial, StringComparison.OrdinalIgnoreCase))
                return file.FullPath;
        }
        return null;
    }

    private static LocatorData LocatorFrom(string path, ProbeResult probe) => new()
    {
        LastKnownPath = path,
        VolumeSerial = probe.VolumeSerial,
        FileId = probe.FileId,
        Size = probe.IsFolder ? null : probe.Size,
        Mtime = probe.IsFolder ? null : probe.Modified,
    };

    private static void Detach(GroupView group, string code, string reason)
    {
        group.DetachedReason ??= reason;
        group._self.Add(new Diagnostic(code, Severity.Warning, reason));
    }
}
