# Biblioteca — plano de implementação mínimo (grupos + índice hierárquico sobre a UI 1.1)

2026-09-19. Segunda passagem sobre `docs/library-model-analysis.md`, com as correções do dono absorvidas
(núcleo aprovado; `a293192` como referência real da UI; pasta é item; grupos aninham por `parentGroupId`;
nada de cache/estrutura auxiliar como contrato). Plano em ordem de execução. Nada foi alterado; aguarda
revisão antes de qualquer implementação.

## 1. Modelos que mudam primeiro (ordem)

1. **Index** (independente do Manifest): `IndexData` plano → árvore (`SourceTree`/`FolderNode`/`FileNode`).
   Sai `LibraryEntry`, `DirectoryStamp`, `ScanPlanData`, `ScanSourceData`, `InaccessibleDirectories`.
2. **Manifest**: `NodeData` (+ `NodeKinds`, `PhysicalKinds`, `StorageModes`, `NodeOrigins`, `IndexRefData`,
   `ManagedRoot`) → `items[]`, `groups[]`, `memberships[]`, `storageRoot`. `LocatorData` fica (vira o
   locator do item).
3. **Modelo reconciliado** (`LibraryModel.cs`): `ModelNode`/`EntryView`/`Aggregate`/`ModelStatus` →
   `SourceView`, `ItemView`, `GroupView`; `Presence`, `Resolution`, `Diagnostic`, `Problem`, as ações ficam.
4. **Linha da UI**: `LibraryRow` vira a linha da árvore achatada (arquivo | pasta | grupo | item não
   localizado), com `Depth` e, nos containers, `IsExpanded`.

## 2. Manifest mínimo

```jsonc
{
  "schemaVersion": 3,
  "status": "synced", "lastSync": "…",
  "storageRoot": null,                 // null = pasta Music do sistema (Environment.SpecialFolder.MyMusic), resolvida a cada uso
  "player": "…", "autoAddRoots": true, "autoSync": true, "extraExtensions": [], "groupByFolder": true,
  "items": [
    { "id": "…", "kind": "folder",
      "locator": { "lastKnownPath": "D:\\Sets", "volumeSerial": "…", "fileId": "…" },
      "source": { "name": "Sets" } },                                  // presente = fonte de varredura (o "root" da 1.1)
    { "id": "…", "kind": "file",
      "locator": { "lastKnownPath": "C:\\…\\Music\\foo.mp3", "volumeSerial": "…", "fileId": "…", "size": 1, "mtime": "…" } }
  ],
  "groups":      [ { "id": "G1", "name": "Favoritos" }, { "id": "G2", "name": "Música", "parentGroupId": "G1" } ],
  "memberships": [ { "groupId": "G2", "itemId": "…" } ],
  "legacyV1": { "…": "como hoje — vigia de downgrade (SyncRootsSnapshot lê)" }
}
```

Sem `revision`, `origin`, `createdAt`, `addedAt`, `index.lastReconciledGeneration` (sem consumidor).
`source.name` fica porque o chip e o Renomear da 1.1 usam. Schema 3: o `manifest.v2.json` com a forma da
branch (schema 2, nunca publicada) é posto de lado como `.bad` e o `manifest.json` 1.x é migrado de novo;
nome de arquivo continua `manifest.v2.json` (é o "arquivo do 2.x" da história de downgrade).

## 3. Index hierárquico mínimo

```jsonc
{
  "schemaVersion": 3, "scannedAt": "…",
  "extensions": [".mp3", "…"], "rulesVersion": 3,          // diferente do plano → varredura completa
  "sources": [                                              // as fontes cobridoras do plano, por caminho
    { "rootPath": "C:\\Users\\Sakamoto\\Music", "kind": "folder", "state": "ok", "volumeSerial": "…",
      "root": { "name": "Music", "mtime": "…", "state": "ok",
                "folders": [ { "name": "Sets", "mtime": "…", "state": "ok", "folders": [], "files": [ … ] } ],
                "files":   [ { "name": "standalone.mp3", "size": 1, "mtime": "…", "created": "…", "fileId": "…",
                               "title": "…", "artist": "…", "album": "…", "duration": 1, "bitrate": 1, "chapters": 0 } ] } },
    { "rootPath": "E:\\foo.mp3", "kind": "file", "state": "ok", "volumeSerial": "…", "file": { … } }
  ]
}
```

`state` de fonte: `ok | unavailable` (disco fora: árvore inteira mantida). `state` de pasta:
`ok | inaccessible` (B5: subárvore anterior mantida). Em memória, ao carregar, cada nó recebe `Parent`;
`FullPath` é calculado quando uma operação pede (abrir, Pasta, mover, probe, locator). Sem `generation`.

## 4. PersistentItem sob demanda

`LibraryOperations.EnsureItem(FileNode|FolderNode) → itemId`: procura em `items` pelo caminho canônico do
nó (lookup construído só nessa operação); não há → grava `{ id: Guid("N"), kind, locator: { lastKnownPath:
FullPath, volumeSerial: da fonte, fileId, size, mtime } }`. Quem chama: `AddToGroup` (menu e drop).
Adicionar arquivo/pasta cria o item já com `source{name}` (probe pro `fileId`). Remoção:
`RemoveMembership` apaga a última membership e, se o item não tem `source`, apaga o registro na mesma
gravação (é o único estado lógico que ele tinha). Varredura, abrir, busca nunca criam item.

## 5. Group + ParentGroupId + GroupMembership

Persistido como no item 2. Em memória (saída do reconciliador): `GroupView { Group, Parent?, Children:
List<GroupView>, Members: List<ItemView> }` — `Children` vem de `parentGroupId`, `Members` de um
`ILookup<groupId, itemId>` construído uma vez por reconciliação. Membership é só `(groupId, itemId)`,
única por par, nunca no pai. Ids repetidos entre itens/grupos → `ConflictedIds` como hoje (§10): o nó não
resolve e a linha de aviso diz.

## 6. Ciclos na hierarquia de grupos

- **Na escrita** (`CreateGroup(parentId)`, `MoveGroup(id, newParentId)`): sobe de `newParentId` por
  `parentGroupId` até `null`; se passar por `id` (ou `newParentId == id`) → recusa "um grupo não pode
  ficar dentro de si mesmo". O drop de grupo em grupo passa pelo mesmo `CanMoveGroup`.
- **Na leitura** (reconciliador): grupo com pai inexistente ou cuja cadeia repete um id → tratado como
  top-level com diagnóstico (`structure.missingParent`/`structure.parentCycle`, o código do 2.0 já faz
  isso para virtual folders). Nunca reescrito sozinho.
- `DeleteGroup(id)`: remove o grupo, os grupos abaixo (conjunto fechado, como o `Remove` do 2.0) e as
  memberships deles; nenhum item/arquivo é tocado.

## 7. ScanPlan derivado

`ScanPlan.Build(snapshot, musicFolder)`: fontes de pasta = `storageRoot` resolvida + itens com `source` de
pasta → conjunto cobridor (o código atual, sem `managedRoot`/boundary); fontes de arquivo = itens com
`source` de arquivo não cobertos; extensões = `FileKinds.All(extra)`; `RulesVersion`. Nunca persistido,
sem fingerprint. Item sem `source` **não** é fonte (correção H: fora das fontes ele fica "não observado",
não amplia a varredura). Importar/Criar pasta não mexem no plano.

## 8. Scanner → árvore

`Scan(ScanRequest(plan, previousTree, full, priorityFiles, readTags, identity))`:
- Por fonte do plano: árvore anterior pela mesma `rootPath` (canônica) ou nenhuma. `Directory.Exists`
  falso → árvore anterior com `state: unavailable` (ou fonte vazia `unavailable`).
- `WalkFolder(path, previousNode?)`: stat do mtime falhou → `previousNode` com `state: inaccessible` (B5);
  `!full && previous.Mtime == mtime` → reusa `files`, desce só nos `folders` conhecidos; senão lista
  (reparse/System pulados): subpastas → `WalkFolder`, arquivos com extensão do plano → nó novo ou reuso do
  anterior quando `(size, mtime, fileId)` batem; listagem falhou → `previousNode` com `inaccessible`.
- Fase 2 (como hoje): tags só dos nós novos/mudados, inbox primeiro, `Ready` no progresso.
- Fonte de arquivo: stat; pasta em volta sumiu → mantém `unavailable`; arquivo sumiu com a pasta lá → sai.
- Saída: `ScanResult(tree, added/changed/removed, unavailable, inaccessible)` — contagens por diferença
  de caminhos canônicos entre as duas árvores (a linha de log da 1.1 usa).
- `DirectoriesChanged(tree, plan)`: fonte do plano sem árvore e existente → true; um stat por pasta `ok`;
  fontes de arquivo por `(size, mtime)`.
- `previousByDir`/`previousChildDirs`/`previousStamps` somem: a árvore anterior é isso.

## 9. Reconciler

`Build(snapshot, tree, probe) → Model + Actions`, puro. Um lookup `caminho canônico → nó` da árvore,
construído aqui.
- **Fontes** (itens com `source`): locator → nó da árvore (raiz de fonte ou nó dentro de outra fonte,
  aninhada) → `Present`/`Unavailable`/`Inaccessible`; não está na árvore → `ResolveExternal` de hoje:
  probe → `Relocated` (`FindByFileId`) → `RewriteLocator`; volume fora → `Unverifiable`; nada → `Broken`
  + `RequestManualRelocation`.
- **Itens** sem `source`: locator → nó da árvore → `Present` (fileId diferente e conhecido → `Replaced` +
  `RewriteLocator`); fora da árvore → probe: existe fora das fontes → `Present` com diagnóstico "fora das
  pastas da biblioteca"; sumiu → `FindByFileId` → `Relocated`/`Unverifiable`/`Broken`. Nunca cria nem
  apaga item (H).
- **Grupos**: cadeias de pai validadas (item 6) → `GroupView` em árvore.
- **Memberships**: par com grupo ou item inexistente → diagnóstico, mantido.
- **Ações**: `RewriteLocator`; `RequestScan(Full)` se `tree.extensions/rulesVersion` ≠ plano ou fontes do
  plano ≠ fontes da árvore; `RequestScan(Incremental|Full)` se `status` pendente/falhou. Some:
  `AcknowledgeIndexGeneration`, `ExtraProbePaths`/`NoteOperated`, managed root, boundary, órfãos, aliases,
  `Aggregate`.

## 10. Projeção (hierarquia de grupos + memberships + subárvore física)

Uma função `Project(model, state) → List<LibraryRow>` no VM (pura, testável), achatando só o que está
expandido:
1. `⌄ Tudo` (container sintético, chave `tudo`): filhos = pastas e arquivos da **storage root** direto
   nele + um container por fonte externa (nome da fonte). Container físico expandido → subpastas
   (containers) e arquivos, da árvore do Index.
2. Grupos top-level, um container cada; expandido → grupos filhos (containers) e depois memberships:
   item-pasta → container físico projetado (mesma subárvore do Index, chave `g|<groupId>|<caminho>`);
   item-arquivo → linha de arquivo; item não localizado → linha esmaecida com o nome do locator.
3. Chaves de linha = cadeia de containers + caminho canônico (o mesmo arquivo em N lugares = N linhas;
   `ApplyRows` continua o diff por chave, guardando scroll e seleção).
4. `IsExpanded` mora num dicionário `chave do container → bool` do VM (o papel do `_groups` da 1.1):
   nasce fechado; buscar expande tudo e esconde container sem descendente que case; limpar devolve os
   flags (regra `SetSearching`).
5. Filtros na projeção, não na `ListCollectionView`: chip (fonte do arquivo, como `RootPath` da 1.1),
   Tipo, busca (`SearchKey` do arquivo inclui a cadeia de pastas, como a 1.1); container sempre visível
   fora de busca (pasta recém-criada vazia tem que aparecer; grupo vazio recebe drop). Ordem: dentro de
   cada container, containers primeiro (pastas, depois grupos, por nome) e arquivos na ordem escolhida —
   decisão local.
6. `Pastas` desligado = a lista plana da 1.1 (só arquivos, ordem global, sem containers).
7. Rodapé/contagens: linhas de arquivo visíveis, totais deduplicados por caminho canônico (já é assim no
   2.0).

## 11. Como a UI `a293192` é reutilizada

Fica literalmente: cabeçalho da aba (título, Sincronizar, Opções), linha de status, linha de aviso, painel
Opções inteiro (Abrir com + Usar VLC + cone + prompt inline, checkboxes, extensões), chips (`RootOption`:
Todas + storage root + fontes de pasta, com ⚠), busca (Esc, 200 ms), Tipo, Ordem (Recentes…), toggle
Pastas, rodapé, "Nada com esse filtro", o template da linha de arquivo (EXT, título/subtítulo, faixas,
duração, tamanho, adicionado, Abrir/Pasta, duplo clique, tooltip), `BulkObservableCollection`,
`ApplyRows`, `Post`.

Adaptado só onde o requisito novo obriga:
- `ListBox`: sai `GroupStyle`/`Expander`/`IsVirtualizingWhenGrouping`; entra `SelectionMode="Extended"`
  e duas `DataTemplate` (container/arquivo) escolhidas por um seletor pequeno. O template do container é
  o `HeaderSite` do `FolderExpander` de hoje (chevron, nome, contagem, `Brush.Bg.Elevated`) com `Margin`
  por `Depth` e uma marca discreta quando é grupo; o chevron liga em `IsExpanded`. Pasta e grupo viram
  itens do `ListBox` (selecionáveis, multi-select) — é o que a correção A exige.
- `ContextMenu` na linha: Abrir, Pasta, Adicionar a ▸ (grupos, com ✓ nos que já têm), Remover de <grupo>
  (só em projeção de grupo), Mover fisicamente…, Localizar… (só em item não localizado); em grupo:
  Renomear, Novo subgrupo…, Excluir grupo; em fonte externa: Remover do catálogo.
- Drag/drop no code-behind (ao lado do `OnSearchKeyDown`): arrasta a seleção; solta em grupo =
  `AddToGroup`; grupo em grupo = `MoveGroup` (com `CanMoveGroup`); em pasta física = efeito `None`.
- `[Adicionar pasta]` vira `[Adicionar ▾]` com as seis entradas (Adicionar pasta…, Adicionar arquivos…,
  Importar arquivos…, Importar pasta…, Criar pasta…, Criar grupo…) — mesmo lugar, mesmo estilo; diálogos
  do sistema (`OpenFolderDialog`/`OpenFileDialog`, a 1.1 já usa; o de pasta tem "Nova pasta"); nome de
  pasta/grupo pela faixa inline que a 1.1 já tem (o prompt do cone). Criar em um container específico =
  pelo menu de contexto dele.
- Opções, seção do chip selecionado: storage root mostra o caminho + `Mudar diretório padrão…` (semântica
  a fechar, correção E); fonte externa continua Nome/Renomear/Abrir pasta/Remover do catálogo.
- Texto do estado vazio ("Nenhuma pasta no catálogo") ajustado: a storage root sempre existe.

## 12. O que da UI 2.0 é removido

`ContainerChip` como navegação, `CrumbItem`/`Breadcrumb`, `NavigateCommand`/`NavigateTo`/`Context*`/
`ContextNameEdit`, `FolderChoice`/picker (`IsFolderPickerOpen`, `PickFolderCommand`), `Novo ▾` com
textbox, menu `⋯` por linha (`IsMenuOpen`, `ToggleRowMenuCommand`, `PutInFolder`, `AliasInFolder`, rename
inline por linha), painel Problemas (`ProblemRow`/`ProblemAction`/`ShowProblems`), `ShowEmptyContext`,
`ManagedRootText/Locked`, `InverseBool*` se sobrarem sem uso, `ApplyGrouping` condicional, `PopupCard`.

## 13. Operações

| Operação | Estado |
|---|---|
| `AddFolder`, `AddFile` | adaptar: item com `source{name}` + locator; sem boundary; inbox pro arquivo |
| `ImportFiles/ImportFolder` | adaptar: destino = pasta escolhida dentro da storage root (arquivos) / storage root (pasta); `CopyInAsync` como está; colisão = nome "(2)" (`UniqueTopLevelName` generalizado pro destino), nunca sobrescreve; sem permissão → repete em Music, avisa, config intocada, Music vira fonte se não coberta (F) |
| `Rename` (fonte), `Remove` (fonte), `Relocate` (Localizar…), `Deliver` | adaptar ao item |
| `CreatePhysicalFolder(parentPath, name)` | nova: `mkdir` dentro da storage root; fallback F |
| `CreateGroup(name, parentId?)`, `RenameGroup`, `MoveGroup`, `DeleteGroup` | novas (item 6) |
| `EnsureItem`, `AddToGroup(groupId, nodes[])`, `RemoveMembership` | novas (item 4); em lote dedupe por contenção |
| `MovePhysical(nodes[], destination)` | nova: dedupe por contenção; move; reescreve locators por prefixo; `MarkPending` + incremental. Algoritmo por volume é decisão local (mesmo volume: `Move`; outro: `CopyInAsync` + apagar) |
| `SetStorageRoot` | nova, só a configuração; o resto da semântica fecha depois (E) |
| `NewFolder`, `CreateAlias`, `Promote`, `Adopt`, `Move` lógico, `SetManagedRoot`, `BoundaryViolations` | removidas |
| `RegenerateConflictedIds`, `RemoveAllWithId` | ficam (ids continuam) |
| `ManagedStorage`: `FindOrphans`, `DeleteToRecycleBin` | removidas; `CopyInAsync/UniqueTopLevelName/Sanitize/CleanupTemporaries` ficam com destino parametrizado |

## 14. Testes que sobrevivem

`PathUtilTests` (menos os de `TryNormalizeManagedRelative`), `FileIdentityTests`, `TagReaderTests`,
`VlcInstallerTests`, `VlcLocatorTests`, `ManifestAndInboxTests` (máquina de estados, `.bak`,
`syncing→failed`, read-only, inbox — só o shape dos dados muda), `LibrarySyncTests` (runner, fila,
cancelamento — `ScanRequest` muda), `ManagedStorageTests` da cópia transacional/nome único/limpeza,
`LegacyMigrationTests` de snapshot e `TranslateInbox`, `ScanPlanTests` do conjunto cobridor, `Fakes.cs`
(probe falso). Os cenários de `LibraryScannerTests` (mtime reuso, rename, B5, raiz ausente, inbox
prioritário) e de `ReconcilerTests` (relocated/replaced/unverifiable/broken) sobrevivem como cenários; as
asserções mudam de lista pra árvore/itens.

## 15. Testes reescritos ou novos

- Reescrever: `LibraryScannerTests` (árvore), `ReconcilerTests` (fontes/itens/grupos/memberships no lugar
  de nós/aliases), `LibraryOperationsTests`, `LegacyMigrationTests.Migrate` (raiz == Music não duplica;
  demais viram fontes), `ScanPlanTests` (fontes de itens, sem managed/boundary), `FolderGroupTests` →
  testes do estado de expansão/busca no VM ou somem com a classe.
- Novos: ciclo de grupos (escrita e leitura), `DeleteGroup` em subárvore, `EnsureItem` idempotente,
  `AddToGroup` em lote com contenção, `RemoveMembership` que apaga o item sem `source`, `Project` (grupo
  aninhado, pasta-membro com subárvore, mesmo arquivo em N lugares, filtros, busca expandindo, Pastas
  desligado), `MovePhysical` no mesmo volume com locators reescritos, fallback de permissão, colisão no
  Importar, schema 2 da branch posto de lado.

## 16. Ordem de implementação (cada passo termina com build + `dotnet test tests/Pixie.Library.Tests` verdes)

0. Branch nova a partir do master `0927c39` com o `Catalog/` e os testes da `library-2.0` por cima (é a
   mesma árvore que a branch tem, mais os ajustes de teste do master; VM/XAML ficam os da 2.0 por
   enquanto, compilando). `<Version>` continua 2.0.0.
1. **Index em árvore + Scanner + Store + Sync.** Shim temporário: `IndexData.Entries` como propriedade
   calculada (arquivos com caminho completo) pra Reconciler e VM 2.0 continuarem compilando sem mexer.
   Testes do scanner reescritos.
2. **Manifest + ScanPlan + LegacyMigration.** `nodes` → `items/groups/memberships/storageRoot`; snapshot;
   `Migrate`; schema 3. Reconciler/Operations/VM ajustados só no mínimo pra compilar (nós = itens com
   `source`; virtual folder/alias/promote/adopt/managed removidos e seus comandos apagados do VM 2.0). A
   aba continua funcionando como catálogo plano.
3. **Reconciler + LibraryModel + Service.** `SourceView/ItemView/GroupView`; ações; sem
   generation/órfãos/boundary. VM 2.0 lê `Sources` no lugar de `Roots`. Testes do reconciliador
   reescritos.
4. **Operações.** Grupos, memberships, `EnsureItem`, `CreatePhysicalFolder`, `MovePhysical`, importar com
   destino, fallback, `SetStorageRoot` (só config). Testes novos.
5. **UI.** `LibraryTabViewModel`/`LibraryView.xaml`/`.xaml.cs` partindo de `a293192` (item 11): projeção,
   containers, multi-select, menu de contexto, drag/drop, `Adicionar ▾`, faixa de nome, Opções da storage
   root. O shim `Entries` do passo 1 e o VM 2.0 saem aqui. Verificação no `RenderHarness` e ao vivo no
   app.
6. **Limpeza.** `FolderGroup`, `ManagedStorage` reduzido, conversores sem uso, `LibraryRow` sem campos de
   alias/managed; `docs/library-model.md` revisão 6, `CLAUDE.md`, `CHANGELOG`.

Só o passo 5 troca a aba inteira de uma vez; os quatro anteriores mudam domínio com a aba 2.0 ainda de pé
(funcionalidade reduzida a cada passo, nunca quebrada). Nenhum passo grava schema novo por cima do 1.x.
