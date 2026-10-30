# Biblioteca — análise do modelo funcional (grupos + índice hierárquico) e plano de adaptação da 2.0

2026-09-19. Resposta ao pedido de revisão do modelo funcional antes de qualquer implementação. Nada foi
alterado, nem código nem schema; este arquivo é só a análise.

O que foi inspecionado: master `0927c39` (a UI 1.1 está em `a293192`; `0e29a03` só tem o bump do csproj —
a árvore dele é a 1.0, sem `FolderGroup` e sem a linha do VLC) e a branch `library-2.0` (`df60a09`).

Duas correções de premissa: a branch 2.0 **tem** o `FolderGroup`/`GroupStyle` e a linha do VLC da 1.1 (o
VM foi escrito por cima da árvore de trabalho de 18/09); o que ela perdeu foi a lista única — pôs contexto,
breadcrumb e chips-navegação em cima.

## 1. Interpretação do modelo

- O disco é a verdade física. O plugin não inventa hierarquia: observa (scanner) e materializa numa
  **árvore** — um Index por biblioteca, cache descartável (apagou, uma varredura completa refaz).
- O Manifest guarda só o que o disco não sabe: configuração; **a** raiz de storage (uma, default `Music`,
  só muda por "Mudar diretório padrão"); as fontes externas (Adicionar = registrar sem copiar); as
  identidades persistentes dos itens que ganharam estado lógico; grupos; memberships N:N.
- A UI é **uma** lista agrupada (a da 1.1): pastas físicas (do Index) e grupos (do Manifest) são
  cabeçalhos colapsáveis do mesmo tipo; "Tudo" é o superset; o mesmo arquivo pode aparecer sob vários
  cabeçalhos — projeções da mesma entidade, nunca entidades.
- Operações físicas (Importar, Criar pasta, Mover fisicamente) mexem no disco e o scanner vê. Operações
  lógicas (Criar grupo, Adicionar a, arrastar pra grupo) mexem só no Manifest. Nada é implícito: fonte não
  vira raiz, importar não vira fonte, grupo não vira pasta.
- Caminho é localização. Identidade persistente (id + locator com `fileId`) nasce só quando um item ganha
  estado lógico, e sobrevive a movimento feito pelo programa e a relocation externa.

## 2. Diferença exata

| Conceito | O que é | Onde mora | Quem escreve |
|---|---|---|---|
| Filesystem tree | O estado físico real | Disco | Ninguém do plugin, exceto Importar/Criar pasta/Mover fisicamente |
| LibraryStorageRoot | A pasta física gravável, destino default de Importar e Criar pasta; também é fonte de varredura (a primária) | `manifest.storageRoot` (`null` = `Music`) | Só "Mudar diretório padrão" |
| LibraryViewRoot ("Tudo") | Container sintético = todo item conhecido (união das árvores das fontes) | Não persiste; é a lista sem filtro | Ninguém |
| Group | `{id, name}`, organização lógica; nunca cria diretório | `manifest.groups` | Criar/renomear/excluir grupo |
| GroupMembership | Par `(groupId, itemId)`, N:N; membership de pasta cobre a subárvore por derivação, não por linhas | `manifest.memberships` | Adicionar a / arrastar / remover do grupo |
| Fonte externa adicionada | Entrada de varredura fora da storage root (pasta ou arquivo), com locator pra relocation | `manifest.items` com papel `source` | Adicionar arquivo/pasta, autoAdd de download, migração 1.x |
| Manifest | O lógico autoritativo, legível | `manifest.v2.json`, gravado inteiro (`LibraryManifest`) | Toda operação lógica |
| Index persistente | A árvore observada, por fonte cobridora, com stamps/tags/identidade física | `index.v2.json`, gravado inteiro por sync | Só o runner de sync |
| Derivados em memória | Dicionários e projeções reconstruídos do Index + Manifest a cada carga/reconciliação (§7) | RAM | Nunca gravados |

## 3. Semântica das operações

| Operação | Disco | Manifest | Index | Recusa / nota |
|---|---|---|---|---|
| Adicionar arquivo | nada | `items += {id, kind:file, source:{name,origin:manual}, locator}` | próxima varredura lê o arquivo (inbox prioritário, como o 2.0 faz) | já coberto por fonte/storage root → informa "já está na biblioteca", nada é criado |
| Adicionar pasta | nada | `items += {kind:folder, source, locator}` | subárvore entra como fonte cobridora (aninhada em outra fonte: a externa anda, a interna é um nó dentro — regra 1.1 "raiz mais funda atribui") | mesmo caminho já fonte → recusa (1.1) |
| Importar arquivo(s) | cópia transacional (`CopyInAsync`: temp + rename) pra pasta destino **dentro** da storage root (default: a raiz); origem intocada | nada | scan vê (mtime da pasta muda); inbox prioriza | colisão: item 22-G; destino sem permissão: item 22-F |
| Importar pasta | copia a pasta inteira como `<storageRoot>\<nome>` | nada | idem | colisão de nome: item 22-G |
| Criar pasta | `mkdir` em storage root ou numa pasta física dentro dela | nada | scan vê | fora da storage root: recusa (§9) |
| Criar grupo | nada | `groups += {id, name}` | nada | nome vazio; nome repetido (assumo único, sem acento/caixa) |
| Adicionar a Group | nada | pra cada selecionado: garante item persistente (cria se não há, locator do Index), `memberships += (g, item)`; idempotente; nunca remove outras | nada | — |
| Arrastar pra Group | nada | = Adicionar a Group; alvo = cabeçalho do grupo | nada | soltar em cabeçalho de pasta física: não faz nada (§18 exige menu explícito) |
| Mover fisicamente… | dedupe da seleção por contenção; mesmo volume: rename; outro volume: cópia transacional + apaga origem (preservando `CreationTime`) | reescreve `locator.lastKnownPath` (e `fileId` se mudou de volume) de todo item/fonte sob o caminho movido; memberships intocadas | `MarkPending` + incremental (as duas pastas-pai mudaram de mtime) | destino inválido ou sem permissão: recusa, sem fallback |
| Multi-select | — | Adicionar a / Mover em lote; pasta + descendente selecionados = só a pasta conta | — | membership de pasta não expande em filhos |

## 4. Estrutura proposta do Index persistente

```jsonc
{
  "schemaVersion": 3,               // item 22-N: nome do arquivo e número
  "scannedAt": "…",
  "extensions": [".mp3", "…"],      // com o que esta árvore foi varrida (substitui o fingerprint)
  "rulesVersion": 3,
  "sources": [                      // só as fontes COBRIDORAS (o plano), não a lista do manifest
    { "path": "C:\\Users\\Sakamoto\\Music",   // o único caminho absoluto da árvore
      "kind": "folder", "state": "ok",         // ok | unavailable (disco fora) — mantém a árvore inteira
      "volumeSerial": "…",
      "root": { "name": "Music", "mtime": "…", "state": "ok",
                "folders": [ { "name": "Sets", "mtime": "…", "state": "ok", "folders": [], "files": [ … ] } ],
                "files":   [ { "name": "foo.mp3", "size": 1, "mtime": "…", "created": "…", "fileId": "…",
                               "title": "…", "artist": "…", "album": "…", "duration": 1, "bitrate": 1, "chapters": 0 } ] } },
    { "path": "E:\\foo.mp3", "kind": "file", "state": "ok", "file": { … } }
  ]
}
```

Fonte aninhada em outra (Sets dentro de Music) não é árvore própria: o reconciliador acha o nó `Music\Sets`
pela árvore cobridora — é o que a 1.1 já faz com raiz aninhada e o que o `ScanPlan` 2.0 já calcula
(conjunto cobridor).

## 5. Dados de cada node

- **Fonte**: `path` absoluto (normalizado), `kind`, `state` (`ok`/`unavailable`), `volumeSerial`.
- **Pasta**: `name`, `mtime` (a chave da incremental), `state` (`ok`/`inaccessible` — B5: inacessível
  preserva o que se sabia, e fica marcada), `folders[]`, `files[]`. Pasta sumida com o pai listável =
  observação válida, sai.
- **Arquivo**: `name`, `size`, `mtime`, `created` ("adicionado em" / Recentes), `fileId` (identidade de
  "as tags ainda valem" e chave de relocation), tags (`title/artist/album/duration/bitrate/chapters`).
  `volumeSerial` sobe pra fonte (reparse points são pulados, então a árvore inteira é um volume).
- Nada de `path`, `scanSourceNodeId`, nem posição lógica.

## 6. Full path derivado

Ao carregar, cada nó recebe `Parent` (referência, não persistida) e `Depth`; `FullPath` é lazy e cacheado:
`Parent.FullPath + '\' + Name`, raiz = `source.path`. `Canonical` idem (lower invariant do `FullPath`). Só
quem precisa chama: abrir, Pasta (Explorer), copiar/mover, probe, comparação de caminho, locator, inbox. A
UI usa `Name`, `Parent`, `Children`. `PathUtil.Normalize` roda só na fonte (o único absoluto gravado); os
nomes já são segmentos. É exatamente o custo que o scanner 2.0 paga hoje (`previousByDir`/
`previousChildDirs` refeitos de `GetDirectoryName` sobre a lista plana) e que some.

## 7. Derivados em memória recomendados

| Estrutura | Função |
|---|---|
| `NodesByCanonicalPath` (pastas e arquivos) | resolver locator de item/fonte, inbox prioritário, dedupe de fonte aninhada, `NoteOperated`, `RootOf` dos chips |
| `FilesByFileId` (por volume) | relocation **dentro do varrido** sem `OpenFileById`: sumiu de A e apareceu em B com o mesmo id = moveu (novo; hoje só `FindByFileId` faz isso) |
| `ItemsById`, `ItemsByCanonicalPath` | "esse caminho já é item?" no Adicionar a (idempotente), resolver linhas de item sem nó (Broken) |
| `MembershipsByGroup`, `GroupsByItem` | projeções dos grupos; o ✓ no submenu "Adicionar a"; "Remover de …" |
| `SourceOf(path)` (fonte mais funda) | filtro dos chips (é o `RootOf` da 1.1) |
| `FolderGroup` por `(container, subcaminho)` | cabeçalhos com o flag de expandido que sobrevive à virtualização (o `_groups` da 1.1) |
| `Rows` + `SearchKey` sob a `ListCollectionView` | busca/ordem/filtro/agrupamento — já é o search index da 1.1 |
| Contagens por chip e por cabeçalho | `RefreshRoots` da 1.1 e `ItemCount` do grupo (já filtrado) |

Todos reconstruídos de Index + Manifest; nenhum gravado.

## 8. O que continua válido sem alteração

- `PathUtil` (§16): `Normalize/Canonical/TryNormalizeAbsolute/IsSamePath/IsDescendantOrSame/
  IsProperDescendant/Relative/VolumeRootOf` — só `TryNormalizeManagedRelative` perde o cliente.
- `FileProbe.cs` inteiro: `IFileProbe`, `WindowsFileProbe`, `FileIdentity` (P/Invoke), `OpenFileById` +
  `GetFinalPathNameByHandle`.
- `LibraryManifest`: load/save, `.bak`/`.bad`, read-only de schema mais novo, `syncing`→`failed`,
  `Generation` CAS em memória, `MarkPending`/`TryMarkSynced`, `ConflictedIds` (§10).
- `LibrarySync` (runner), `LibraryStore`/`Inbox` (`inbox.v2\`), `TagReader`,
  `LegacyMigration.TryReadV1/SyncRootsSnapshot/TranslateInbox` (leitura só do 1.x), `AtomicFile`,
  `LibraryJson`.
- Do scanner: reuso por mtime, B5 (`Preserve`/`KeepKnown`), reparse/System pulados, identidade por
  arquivo listado, `SameFileAs` com `fileId`, prioridade do inbox + `Ready`.
- Do reconciliador: `ProbeOnce`, a máquina de estados `ResolveExternal` (Resolved/Replaced/Relocated/
  Unverifiable/Broken), `ApplyObservation`, `Diagnostic`/`Problem`/`Severity`, as ações `RewriteLocator`/
  `RequestScan`/`RequestManualRelocation`, `Build` puro.
- `LibraryService` (loop de reconciliação coalescido, `Apply` das ações, throttle de scan, `Start`).
- `ManagedStorage.CopyInAsync/UniqueTopLevelName/Sanitize/CleanupTemporaries` (com destino parametrizado).
- UI 1.1 inteira: `FolderGroup`, `GroupStyle`/`HeaderSite`, chips como filtro, busca (Esc, 200 ms, abre
  todas), Tipo, Ordem (Recentes…), Pastas, rodapé, Opções + linha do VLC + prompt do cone,
  `BulkObservableCollection`, `ApplyRows` por diff, `Post` guardado.
- `VlcInstaller/VlcLocator/VlcIcon`, `RelayCommand`, os testes de PathUtil, FileIdentity, TagReader, VLC,
  FolderGroup.

## 9. O que precisa ser adaptado

- `ManifestModels`: `NodeData` (kind/parent/storageMode/managedPath/target) → `items[]` (com papel
  `source`), `groups[]`, `memberships[]`, `storageRoot`.
- `LibraryModels`: `IndexData` plano → árvore (item 4); `LibraryEntry` vira o nó-arquivo; `ScanPlanData`
  some (ficam `extensions` + `rulesVersion`).
- `ScanPlan.Build`: fontes = storage root + itens-fonte + itens **não cobertos** (item 22-H); mesmo
  conjunto cobridor; sem `managedRoot`, sem boundary, sem fingerprint.
- `LibraryScanner`: mesmo algoritmo sobre a árvore anterior em vez dos três dicionários;
  `DirectoriesChanged` anda na árvore; saída = árvore nova + estados.
- `Reconciler`: entra Manifest (itens/grupos/memberships) + árvore; resolve fontes e itens pelo locator
  (caminho → `FilesByFileId` → `OpenFileById` → Broken); projeta grupos; mantém diagnósticos.
- `LibraryOperations`: `AddFile/AddFolder/Import*/Rename/Remove/Relocate/Deliver` mudam de alvo (item,
  storage root); entram `CreateGroup/RenameGroup/DeleteGroup/AddToGroup/RemoveFromGroup/
  CreatePhysicalFolder/MovePhysical/SetStorageRoot`.
- `LegacyMigration.Migrate`: raiz 1.x == `Music` → storage root (não duplica); demais → itens-fonte.
- `LibraryRow`: `Group` → `Groups` (uma projeção física + uma por grupo que o contém direta ou por pasta
  ancestral); ganha `ItemId?`; perde alias/managed/`IsNode`.
- `LibraryTabViewModel`: volta ao da 1.1 (887 linhas) + o que o modelo pede: `ContainerGroup` com marca de
  grupo, `GroupsFor(row)`, seleção múltipla, menu de contexto, drag/drop, comandos novos; a
  `LibraryService` fica como fonte do modelo (`ModelChanged`) no lugar dos eventos diretos do runner.
- `LibraryView.xaml`: o da 1.1 + `SelectionMode="Extended"`, `ContextMenu` nas linhas e nos cabeçalhos,
  drop nos cabeçalhos de grupo, marca discreta no cabeçalho de grupo, e os pontos de entrada das
  operações (item 22-C).
- Testes: `ScanPlanTests`, `ReconcilerTests` (652 linhas, maior reescrita), `LibraryOperationsTests`,
  `LegacyMigrationTests`, `ManagedStorageTests`, scanner/sync/inbox (árvore).
- `docs/library-model.md` revisão 5 fica superada (revisão 6 depois da aprovação, não agora).

## 10. O que pode ser removido ou simplificado

- `StorageMode` Managed/ExternalLinked, `managedPath`, `managedRoot` privado em `data\library\managed`,
  `SetManagedRoot`, "imutável depois do primeiro nó Managed".
- Boundary D12 (`ViolatesBoundary`, `BoundaryViolations`, bloqueio de Importar).
- Órfãos G10: `FindOrphans`, `Adopt`, `DeleteToRecycleBin`, o problema `managedOrphan`.
- `VirtualFolder` + `parent` singular + passos 2 (estrutura/ciclo/attached) do reconciliador; `Move`
  lógico; `NewFolder`.
- `Alias` inteiro (`CreateAlias`, `ResolveAlias`, tabela F, `ResolvedTarget`).
- `Promote` como criação de nó (vira "garantir item" no Adicionar a).
- Fingerprint SHA-256 do plano + `index.scanPlan` + `AcknowledgeIndexGeneration`/
  `LastReconciledGeneration` (um write do manifesto por sync pra nada; `index.generation` pode ficar como
  contador de log).
- `scanSourceNodeId` nas entradas; `InaccessibleDirectories`/`UnavailableSources` como listas (viram
  estado do nó).
- Na UI 2.0: `ContainerChip` como navegação, `Breadcrumb`, `Context*`, `NavigateTo`, `FolderChoices`/
  picker, `Novo ▾` com textbox, menu `⋯` por linha, painel Problemas com ações (o que sobrar vira a linha
  de aviso da 1.1 — item 22-M), `ApplyGrouping` condicional.

## 11. Modelo lógico proposto

```jsonc
{
  "schemaVersion": 2,               // item 22-N
  "revision": 1, "status": "synced", "lastSync": "…",
  "storageRoot": null,              // null = Music; só "Mudar diretório padrão" escreve
  "player": "…", "autoAddRoots": true, "autoSync": true, "extraExtensions": [], "groupByFolder": true,
  "items": [
    { "id": "…", "kind": "folder", "createdAt": "…",
      "locator": { "lastKnownPath": "D:\\Sets", "volumeSerial": "…", "fileId": "…", "size": null, "mtime": null },
      "source": { "name": "Sets", "origin": "manual" } },          // presente = é fonte de varredura
    { "id": "…", "kind": "file", "createdAt": "…",
      "locator": { "lastKnownPath": "C:\\Users\\…\\Music\\foo.mp3", "volumeSerial": "…", "fileId": "…", "size": 1, "mtime": "…" } }
  ],
  "groups":      [ { "id": "…", "name": "Favoritos", "createdAt": "…" } ],
  "memberships": [ { "groupId": "…", "itemId": "…", "addedAt": "…" } ],
  "legacyV1": { "…": "mantido: vigia de downgrade" }
}
```

Uma identidade por coisa física: um arquivo adicionado é um item com `source`; se entrar num grupo, é o
**mesmo** id. Grupo sem `parent` (plano). Membership referencia `itemId`, nunca caminho.

## 12. Quando um item do Index ganha identidade persistente

No primeiro estado lógico: Adicionar a Group / arrastar pra grupo (cria o item com locator tirado do nó do
Index — caminho, `fileId`, tamanho, mtime — antes de gravar a membership), ou Adicionar arquivo/pasta (fonte
é lógico por definição). Nunca por varredura, abrir, tocar ou busca. Lookup por `ItemsByCanonicalPath`
antes de criar (idempotente). Proposta: item sem membership e sem papel de fonte é apagado na mesma
gravação que removeu a última membership (invariante "item existe ⇔ tem estado lógico"; item 22-I).

## 13. Identidade estável através de…

- **ViewRoot / grupos / projeções**: a linha carrega `ItemId?` e o caminho canônico; todas as projeções de
  uma entidade são o **mesmo** objeto `LibraryRow` posto em N grupos pela `ListCollectionView` (item 14);
  contagens do rodapé dedupe por caminho canônico (o 2.0 já faz).
- **Movimento pelo programa**: quem move sabe origem e destino → reescreve por prefixo os locators de todo
  item/fonte sob o caminho movido, reprova `fileId` se trocou de volume, e pede incremental; memberships
  não são tocadas.
- **Relocation externa**: (1) `FilesByFileId` na própria varredura (mesmo volume, dentro das fontes) →
  `RewriteLocator`; (2) `FindByFileId` (`OpenFileById`, volume inteiro) — já existe; (3) volume fora →
  `Unverifiable`, linha mantida e esmaecida; (4) nada → `Broken` → Localizar…/Remover do grupo. O
  reconciliador nunca cria id novo por evidência física (§25).

## 14. Como a UI 1.1 recebe grupos sem outra navegação

O mecanismo da 1.1 já é "cabeçalhos colapsáveis sobre uma lista virtualizada":
`PropertyGroupDescription("Group")` + `FolderGroup` + `Expander`. Grupos entram como uma segunda família
de cabeçalho no **mesmo** `GroupStyle`: `LibraryRow.Groups` devolve uma coleção (a projeção física + uma
por grupo); a `ListCollectionView` põe um item em vários grupos quando o nome do grupo é `ICollection`
(comportamento documentado do `CollectionViewGroupRoot`; precisa ser provado no `RenderHarness` com
virtualização antes de apostar). O cabeçalho de grupo recebe a marca discreta (§20); `FolderGroup.Comparer`
ordena fontes físicas primeiro (ordem 1.1) e grupos depois (assunção). Chips continuam filtro por fonte;
busca continua abrindo todos os cabeçalhos; "Pastas" desligado = superset plano (item 22-K).

O que a 1.1 não tem e o modelo exige: `SelectionMode=Extended`, `ContextMenu` (linha: Adicionar a ▸
[grupos com ✓], Remover de <grupo> quando a linha é projeção de grupo, Mover fisicamente…, Abrir, Pasta;
cabeçalho de grupo: Renomear, Excluir grupo; cabeçalho de pasta: Adicionar a ▸, Mover fisicamente…, Abrir
pasta), drag da linha/cabeçalho de pasta e drop no cabeçalho de grupo. Sem breadcrumb, sem contexto, sem
página.

## 15. Pasta projetada dentro de grupo reusa o subtree

Membership `(Favoritos, Sets)`: ao montar as linhas, para cada nó-arquivo `F` da subárvore de `Sets` no
Index, `GroupsFor(F)` acrescenta o `FolderGroup` de chave `(Favoritos, relpath(dir(F), pai(Sets)))` —
cabeçalho "Favoritos › Sets" (ou "Favoritos › Sets › sub"). Nenhuma cópia: são os mesmos nós e a mesma
`LibraryRow`. Quando a varredura acrescenta `baz.mp3` a `Sets`, a próxima montagem de linhas o projeta
sozinha, sem membership nova.

## 16. Impacto sobre o Manifest atual

Da 1.x: `roots` → `items` com `source` (menos a raiz igual a `Music`, que vira a storage root implícita);
`player/autoAddRoots/autoSync/extraExtensions/groupByFolder/status/lastSync` iguais. Da 2.0 (branch):
`nodes` inteiro sai; `managedRoot` sai; `index.lastReconciledGeneration` sai; `legacyV1` fica. O
`manifest.json` 1.x continua intocado (downgrade).

## 17. Impacto sobre o Index atual

Da 1.x/2.0 (lista plana `entries` + `directories` + `scanPlan`): substituído pela árvore. Não há migração
de índice — é cache: índice de schema desconhecido é posto de lado (`.bad`, como o `ReadIndex` já faz) e a
primeira varredura é completa. Custo único: uma leitura de tags de tudo (1.441 arquivos hoje).

## 18. Impacto sobre o Scanner

Entrada: árvore anterior + plano (fontes cobridoras + extensões + regras). Fonte nova sem árvore → andada
inteira; árvore sem fonte no plano → descartada; extensões ou `rulesVersion` diferentes do gravado →
completa. Por pasta: mesmo mtime → reusa o nó e desce nos filhos conhecidos (hoje `previousByDir`/
`previousChildDirs`); mudou → lista; ilistável → `state: inaccessible` mantendo o nó; fonte ausente →
`state: unavailable` mantendo a árvore. Tags só onde `(size, mtime, fileId)` mudou. `PriorityFiles` (inbox)
e `Ready` iguais. Ganha: `FilesByFileId` da árvore anterior permite anotar "moveu" para itens.

## 19. Impacto sobre o Reconciler

Some: estrutura de pais/ciclos/attached, managed root, boundary, órfãos, aliases, generation. Fica: probe
cacheado, `ResolveExternal` (agora para **fontes e itens**, com o passo extra `FilesByFileId` antes do
`OpenFileById`), `ApplyObservation` para entradas operadas, diagnósticos e ações. Entra: projeção de grupos
(`group → memberships → item → nó do Index (ou linha sem nó se Broken)`), aviso de fonte `unavailable`/
pasta `inaccessible` a partir do estado dos nós, `conflictedId` mantido. Continua puro:
`Build(manifest, índice, probe) → Model + Actions`.

## 20. Impacto sobre o ScanPlan

Continua derivado e nunca gravado: `storageRoot + itens-fonte pasta (cobridores) + itens-fonte arquivo não
cobertos + itens não cobertos (22-H)`, mais extensões e `rulesVersion`. Importar/Criar pasta nunca o
alteram (§23). Sai o fingerprint como chave de invalidação: a comparação passa a ser estrutural (fonte a
fonte) + `extensions`/`rulesVersion` gravados no índice.

## 21. Estratégia de migração

- **1.x → novo** (a única que importa: 2.0 nunca foi publicada): na primeira carga sem `manifest.v2.json`,
  `Migrate` faz raiz == `Music` → storage root (nada gravado além de `storageRoot: null`); outras raízes →
  itens-fonte com `origin: migrated`; `legacyV1` snapshot igual ao de hoje; `inbox\` traduzido uma vez
  (`TranslateInbox`). Downgrade continua seguro: o 1.x nem sabe que os arquivos `.v2` existem.
- **Branch 2.0 → novo**: sem caminho. Os únicos `manifest.v2.json`/`index.v2.json`/`inbox.v2\` com a forma
  da branch são os do Debug do dono (`revision 25`, um `virtualFolder` "favoritos" e 2 arquivos promovidos
  de testes) e os do harness — apagar à mão antes do primeiro start do novo. Depende do item 22-N.

## 22. Ambiguidades que ainda impedem implementação segura

- **A. Pasta como item vs. pasta como cabeçalho.** Na 1.1 a pasta é cabeçalho (`GroupItem`), não item do
  `ListBox`: não entra em multi-select nativo e não é "selecionada". §17/§19 pedem pasta selecionável junto
  com arquivos. Opções: (a) pasta continua cabeçalho — ações de pasta só por botão direito/drag no
  cabeçalho, multi-select só de arquivos; (b) pasta vira linha (árvore) — muda o visual da 1.1.
  Pasta-membro vazia em (a) fica invisível (cabeçalho sem linha some, regra 1.1).
- **B. Profundidade dentro de um grupo**: cabeçalhos planos com prefixo ("Favoritos › Sets › sub", o jeito
  1.1 de mostrar "Music › Sets") ou indentação aninhada como nos exemplos de §20? A 1.1 não aninha.
- **C. Onde ficam as operações novas** (Adicionar arquivo/pasta, Importar arquivos/pasta, Criar pasta,
  Criar grupo, Mudar diretório padrão): o cabeçalho 1.1 tem `[Sincronizar] [Adicionar pasta] [Opções]`.
  Não decido isso.
- **D. Migração da raiz 1.x e o default**: confirmar "raiz == `Music` vira storage root; as outras viram
  fontes". Quem tem raízes 1.x que **não** incluem `Music` passa a ter `Music` observada sem ter pedido —
  ok? E o default: `SpecialFolder.MyMusic` (honra redirecionamento; hoje igual a `%USERPROFILE%\Music`
  nesta máquina) ou o literal?
- **E. "Mudar diretório padrão"**: move o conteúdo? A raiz antiga vira fonte externa, some, ou nada? Só com
  isso os locators absolutos dos itens têm regra.
- **F. Fallback de permissão (§6)**: só a operação em curso vai pra `Music` (a raiz configurada fica) ou a
  raiz é reconfigurada? Mover fisicamente não tem fallback possível — recusa.
- **G. Colisão no Importar**: proposta "(2)" no nome e nunca sobrescrever/mesclar (é o
  `UniqueTopLevelName` de hoje); pasta com o mesmo nome também vira "(2)". Confirmar.
- **H. Item que sai de toda fonte** (movido pra fora, ou fonte removida) mantendo membership: vira fonte
  implícita (continua varrido, com tags) — para arquivo faz sentido; para pasta significa varrer uma
  subárvore fora da biblioteca. Ou vira Broken.
- **I. Última membership removida**: apaga o item (proposta) ou mantém.
- **J. Grupo é plano** (sem grupo dentro de grupo) e nome único — confirmar.
- **K. "Pastas" desligado** esconde também os grupos (lista plana = superset) — confirmar.
- **L. Chips**: só fontes de pasta + "Todas" (arquivo adicionado aparece sob o cabeçalho da pasta real
  dele, como a 1.1 faz com arquivo fora de raiz), ou grupos também viram chip (é filtro, §21 permite)?
- **M. Problemas com ação** (Localizar… de item Broken, ids em conflito): a 1.1 só tem a linha de aviso.
  Cabe no menu de contexto da linha esmaecida?
- **N. Nome/schema**: reusar `manifest.v2.json`/`index.v2.json` com `schemaVersion 2` de forma nova (2.0
  nunca saiu) ou subir pra 3 (e o índice pra `index.v3.json`).
- **O. Drop**: só em cabeçalho de grupo; soltar em cabeçalho de pasta física é ignorado (não é Mover).
  Confirmar.

## 23. Arquivos/classes que mudariam

| Arquivo | Ação |
|---|---|
| `Catalog/ManifestModels.cs` | reescrever (itens/grupos/memberships/storageRoot) |
| `Catalog/LibraryModels.cs` | reescrever `IndexData` como árvore; `LibraryEntry` → nó-arquivo |
| `Catalog/ScanPlan.cs` | adaptar (sem managed/boundary/fingerprint) |
| `Catalog/LibraryScanner.cs` | adaptar à árvore (mesmo algoritmo) |
| `Catalog/Reconciler.cs`, `Catalog/LibraryModel.cs` | reescrever a metade estrutural; manter probe/estados/ações |
| `Catalog/LibraryOperations.cs` | adaptar + operações novas (grupos, pasta física, mover) |
| `Catalog/ManagedStorage.cs` | reduzir à cópia transacional com destino parametrizado (renomear) |
| `Catalog/LegacyMigration.cs` | adaptar `Migrate` |
| `Catalog/LibraryManifest.cs`, `LibraryStore.cs`, `LibrarySync.cs`, `PathUtil.cs`, `FileProbe.cs`, `TagReader.cs` | manter (ajustes de tipo) |
| `LibraryService.cs` | manter (sem órfãos/cleanup do managed) |
| `LibraryRow.cs` | 1.1 + `Groups` + `ItemId` |
| `FolderGroup.cs` | + tipo de container (pasta/grupo) e chave composta |
| `LibraryTabViewModel.cs`, `LibraryView.xaml(.cs)` | base 1.1 + itens do 14 (drag/drop e menu no code-behind) |
| `LibraryPlugin.cs` | composição (2.0) sem managed |
| `tests/…/{ScanPlan,Reconciler,LibraryOperations,LegacyMigration,ManagedStorage,LibraryScanner,LibrarySync,ManifestAndInbox}Tests.cs`, `Fakes.cs` | adaptar/reescrever |
| `docs/library-model.md`, `CLAUDE.md`, `CHANGELOG.md` | revisão 6 depois |

## 24. Decisões da 2.0 que deixam de fazer parte, e por quê

- **Managed vs ExternalLinked e o `managed\` privado**: a storage root é uma pasta visível do usuário e é
  varrida como qualquer fonte; "estar dentro dela" é contenção de caminho, não um modo gravado.
- **Boundary disjunta (D12)**: existia pra separar ownership do storage privado; sem storage privado, fonte
  aninhada é a regra 1.1 (a mais funda atribui).
- **Órfãos, Adotar, Apagar**: tudo dentro da storage root é biblioteca por observação; não há "arquivo sem
  dono".
- **VirtualFolder + parent singular**: era hierarquia lógica com posição única; o requisito é N:N sem
  hierarquia → `Group + Membership`.
- **Alias**: era o jeito de um nó aparecer em dois lugares; membership faz isso sem entidade nova nem
  ciclos.
- **Promoção como nó explícito**: a ideia fica (identidade só com estado lógico), o nó não — vira registro
  de item criado no Adicionar a.
- **Posição lógica como projeção de nós físicos**: a árvore do Index já é a posição física; a única
  projeção lógica é a de grupo.
- **Fingerprint do ScanPlan + generation/ack**: invalidação passa a ser estrutural na árvore; o ack era um
  write por sync sem consumidor além do status.
- **Index plano com caminho absoluto por entrada**: reconstruía a hierarquia a cada varredura e a cada
  montagem de linhas.
- **Navegação/contexto/chips-navegação/menus por linha/Novo ▾/picker/Problemas com ações**: a UI 2.0 não é
  referência de comportamento; a 1.1 é.
