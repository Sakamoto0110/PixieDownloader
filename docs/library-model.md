# Biblioteca — Manifest, Index e Filesystem: o modelo da Library 2.0 (revisão 6)

Revisão 6 em 2026-09-20: o modelo **implementado** na branch `library-2.0-groups` (Library 2.0.0),
depois que o dono trocou o contrato da revisão 5 (Managed/ExternalLinked, VirtualFolder, Alias, storage
privado, órfãos) pelo modelo funcional de `docs/library-model-analysis.md` e do plano
`docs/library-implementation-plan.md`: **filesystem = verdade física; Manifest = só o que o disco não
sabe; Index = a árvore observada, cache; grupos aninhados com membership N:N; uma lista só, a da 1.1.**
Tudo abaixo descreve código que existe (`src/Plugins/Pixie.Library/Catalog/…`, `Projection.cs`,
`LibraryTabViewModel.cs`); o que ficou pra depois está em §12.

---

## 1. Princípio

| Camada | O que é | Onde | Quem escreve |
|---|---|---|---|
| Filesystem | O estado físico real: onde cada arquivo está e qual é a hierarquia | disco | ninguém do plugin, exceto Importar, Criar pasta e Mover fisicamente |
| **Manifest** | O lógico que o disco não sabe: configuração, a storage root, as fontes, os itens com identidade persistente, os grupos e as memberships | `data\library\manifest.v2.json` | toda operação lógica, inteiro, por temp + rename |
| **Index** | A árvore observada pelo scanner: fontes → pastas → arquivos, com mtime, identidade física e tags. Cache: apagou, uma varredura completa refaz | `data\library\index.v2.json` | só o runner de sync, inteiro, uma vez por rodada |
| **Model** | O Manifest e o Index juntados com o disco pelo `Reconciler` (puro): cada item resolvido, os grupos em árvore, os problemas, as ações a executar | memória | `LibraryService`, a cada mudança |
| **Projeção** | A lista única da aba: "Tudo" (a hierarquia física) e os grupos, containers colapsáveis iguais | memória | `LibraryProjection.Build`, pura |

O Manifest nunca copia a hierarquia física: uma pasta num grupo é uma membership da pasta; o que está
dentro dela vem do Index. Estruturas auxiliares (lookups por caminho, por id, memberships por grupo) são
construídas quando uma operação precisa e nunca gravadas.

## 2. Os arquivos em `data\library\`

| Arquivo | Dono | Regra |
|---|---|---|
| `manifest.json`, `index.json`, `inbox\` | 1.x | **nunca escritos** pelo 2.x: voltar pra 1.1 funciona. Lidos só pra migrar (§11) |
| `manifest.v2.json` (schema 3) | `LibraryManifest` | `.bak` a cada gravação; ilegível → `.bad` e o `.bak` é tentado; schema **maior** → aberto só pra leitura (nada é gravado, nenhuma varredura roda); schema **menor** (o 2 da branch 2.0, nunca publicada) → `.bad` e o 1.x é migrado de novo; `syncing` achado no disco → `failed` (a próxima varredura é completa) |
| `index.v2.json` (schema 3) | `LibraryStore` | schema diferente ou ilegível → `.bad` e árvore vazia (= varredura completa); nunca convertido |
| `inbox.v2\` | `Inbox` | um bloco JSON por arquivo entregue, dobrado como prioridade na varredura e apagado **depois** do índice ir pro disco |

## 3. Manifest (schema 3)

```jsonc
{
  "schemaVersion": 3,
  "status": "pending|synced|syncing|failed", "lastSync": "…",
  "storageRoot": null,                 // null = a pasta Music do sistema (SpecialFolder.MyMusic), resolvida a cada uso; nunca um literal com nome de usuário
  "player": "…", "autoAddRoots": true, "autoSync": true, "extraExtensions": [], "groupByFolder": true,
  "items": [
    { "id": "…", "kind": "folder", "locator": { "lastKnownPath": "D:\\Sets", "volumeSerial": "…", "fileId": "…" }, "source": { "name": "Sets" } },
    { "id": "…", "kind": "file",   "locator": { "lastKnownPath": "C:\\…\\Music\\foo.mp3", "volumeSerial": "…", "fileId": "…", "size": 1, "mtime": "…" } }
  ],
  "groups":      [ { "id": "G1", "name": "Favoritos" }, { "id": "G2", "name": "Música", "parentGroupId": "G1" } ],
  "memberships": [ { "groupId": "G2", "itemId": "…" } ],
  "legacyV1": { "observedAt": "…", "fingerprint": "…", "observedRoots": [ … ], "observedInbox": [ … ] }
}
```

- **Storage root**: a pasta da biblioteca — sempre varrida, destino do Importar e do Criar pasta. Só muda por
  "Mudar diretório padrão…" (`SetStorageRoot`, hoje só a configuração — §12). Adicionar, Importar, varredura
  e contagem de nós **nunca** a mudam.
- **Item**: uma coisa do disco sobre a qual o catálogo guarda estado — `kind` file|folder e um **locator**
  (caminho onde foi visto pela última vez + `volumeSerial`/`fileId` NTFS + tamanho/mtime de arquivo). Com
  `source` é uma **fonte** (o "root" da 1.x: uma pasta andada ou um arquivo stat'ado por conta própria; `name`
  é o que o chip mostra). Sem `source`, existe porque um grupo o nomeia. **Um item existe se e só se tem
  estado lógico**: nasce no primeiro Adicionar a (`EnsureItem`) ou no Adicionar
  pasta/arquivo; some com a última membership quando não é fonte. Um arquivo que o scanner descobriu e nada
  aponta vive só no Index. Uma fonte que também entra num grupo é o **mesmo** id.
- **Grupo**: `{id, name, parentGroupId?}` — organização só do catálogo, nunca uma pasta. Árvore: no máximo um
  pai; `MoveGroup` sobe pelos pais e recusa fechar ciclo ("um grupo não pode ficar dentro de si mesmo"); um
  ciclo ou pai inexistente achado no arquivo deixa o grupo no topo com diagnóstico, nunca reescrito sozinho.
- **Membership**: `(groupId, itemId)`, única por par, N:N. Nunca implicada pelo grupo pai nem pelo conteúdo de
  uma pasta: `Música → Sets` é uma membership; `foo.mp3` dentro de `Sets` aparece no grupo porque a projeção
  desce na subárvore do Index, sem membership própria. Membership pra grupo ou item inexistente é problema
  reportado, mantido.
- **Ids** são únicos entre itens e grupos juntos; repetição = `ConflictedIds`: nenhum dos portadores resolve
  até `RegenerateConflictedIds`/`RemoveAllWithId` (explícitos; referências não são reescritas).
- `legacyV1`: o snapshot do 1.x (§11). Sem `revision`, `origin`, `createdAt`, `generation`: nada os consome.

## 4. Index (schema 3): a árvore

```jsonc
{
  "schemaVersion": 3, "scannedAt": "…",
  "extensions": [".mp3", "…"], "rulesVersion": 3,       // diferente do plano → varredura completa
  "sources": [                                           // uma por fonte COBRIDORA do plano
    { "rootPath": "C:\\Users\\…\\Music", "kind": "folder", "state": "ok|unavailable", "volumeSerial": "…",
      "root": { "name": "Music", "modified": "…", "state": "ok|inaccessible",
                "folders": [ { "name": "Sets", "modified": "…", "state": "ok", "folders": [], "files": [ … ] } ],
                "files":   [ { "name": "a.mp3", "size": 1, "modified": "…", "created": "…", "fileId": "…",
                               "title": "…", "artist": "…", "album": "…", "duration": 1, "bitrate": 1, "chapters": 0 } ] } },
    { "rootPath": "E:\\solo.mp3", "kind": "file", "state": "ok", "volumeSerial": "…", "file": { … } }
  ]
}
```

- `rootPath` é o único caminho absoluto da árvore; o caminho de um nó é `rootPath` + os nomes até ele,
  derivado ao carregar (`Attach` liga `Parent`/`Source`; `FullPath` é calculado quando uma operação pede —
  abrir, Pasta, mover, probe, locator). `Entries`, `FindFile`, `FindFolder`, `SourceOf` são lookups
  construídos na primeira pergunta; a árvore não muda depois da varredura.
- **Fonte aninhada não é árvore própria**: uma fonte dentro de outra é um nó da árvore de fora (o conjunto
  cobridor do plano); o reconciliador a acha pelo caminho. Fonte dentro da storage root: a mesma regra da 1.1
  (a raiz mais funda atribui o arquivo ao chip).
- Estados: fonte `unavailable` (não estava lá pra observar — disco fora, pasta sumida: a árvore inteira é
  mantida), pasta `inaccessible` (existe e recusou stamp/listagem: o que se sabia dela e da subárvore é
  mantido — **inacessível ≠ vazia**). Pasta que sumiu com o pai listável é observação válida: sai.
- Sem `path` por nó, sem `scanSourceNodeId`, sem posição lógica, sem fingerprint, sem `generation`.

## 5. ScanPlan (derivado, nunca gravado)

`ScanPlan.Build(snapshot, musicFolder)` = storage root resolvida + fontes de pasta (`items` com `source` e
`kind: folder`), como **conjunto cobridor** (uma pasta dentro de outra é andada pela de fora; mesmo caminho
duas vezes conta uma), + fontes de arquivo não cobertas + extensões (`FileKinds.All(extraExtensions)`).
Item sem `source` não é fonte: fora das fontes ele fica "não observado" (§7), a varredura não cresce por
causa dele. Importar e Criar pasta nunca mudam o plano (é conteúdo novo da árvore da storage root).

Invalidação: o índice grava `extensions` e `rulesVersion`; só a diferença deles força varredura completa
(`LibraryScanner.RulesChanged`). Fonte nova = árvore nova andada inteira; fonte que saiu = árvore
descartada; nada disso precisa de varredura completa das outras.

## 6. Scanner

`LibraryScanner.Scan(plan, árvore anterior, full, prioridades, readTags, identity)` → árvore nova, pura
(FS só leitura, tags e identidade por delegate):

- Por fonte do plano: árvore anterior pelo `rootPath`; `Directory.Exists` falso → anterior copiada com
  `state: unavailable`.
- Por pasta: stat do mtime falhou → nó anterior copiado, `inaccessible`; `!full` e mtime igual ao do nó
  anterior → arquivos reusados, desce só nos filhos conhecidos; senão lista (reparse points, pastas System e
  `.~*` — as temporárias do app — pulados); listagem falhou → `inaccessible`.
- Por arquivo listado: um handle pra identidade (`FileIdentity`); `(size, mtime, fileId)` iguais ao nó
  anterior → reusado com as tags; senão lê as tags na fase 2 — inbox primeiro, `Ready` no progresso pra aba
  mostrar antes do fim.
- Fonte de arquivo: stat; pasta em volta sumiu → `unavailable` mantendo o nó; arquivo sumiu com a pasta lá →
  sai.
- Saída: árvore + contagens (por diferença de caminhos canônicos) + fontes indisponíveis + pastas
  inacessíveis. `DirectoriesChanged` (o que a aba faz ao abrir) = um stat por pasta `ok`; fonte sem árvore ou
  `unavailable` que existe agora conta como mudança; fonte de arquivo por `(size, mtime)`.
- Um arquivo reescrito no lugar (mesmo nome) escapa da incremental — o botão Sincronizar (completa) pega.
  Rename numa pasta que o volume não stampa idem (o runner do GitHub faz isso; NTFS de desktop não).

## 7. Reconciler

`Reconciler.Build(snapshot, árvore, probe)` → `Model + Actions`, puro; sonda a storage root e **cada item**
(fontes e membros: dezenas), nunca os milhares de arquivos da árvore. Identidade nunca nasce aqui.

| O item… | Presence / Resolution | Ação |
|---|---|---|
| está onde o locator diz (probe `Found`, tipo bate) | `Present` / `Resolved`; `File`/`Folder`/`Tree` apontam pro nó da árvore quando ela o alcança | locator sem `fileId`, ou tamanho/mtime velhos → `RewriteLocator` silencioso |
| está lá, mas com outro `fileId` | `Present` / `Replaced` ("substituído; continua sendo este caminho") | `RewriteLocator` |
| existe e recusou o probe | `Inaccessible` / `Resolved` | — |
| é pasta onde há arquivo (ou vice-versa); locator inválido | `TypeMismatch` / `StructuralConflict` | — |
| sumiu, volume fora (`IsVolumeReady`/`PathRootExists` falso) | `Unknown` / `Unverifiable` ("disco desligado?") | — |
| sumiu, volume presente, mesmo `fileId` **em outro lugar da árvore** (moveu dentro da biblioteca) | `Present` / `Relocated` no caminho novo | `RewriteLocator` (sem `OpenFileById`) |
| sumiu, volume presente, `OpenFileById` acha no volume | `Present` / `Relocated` | `RewriteLocator` |
| sumiu e nada achou | `Missing` / `Broken` | `RequestManualRelocation` (Localizar… ou Remover) |

Grupos: cadeias de pai validadas (inexistente, em conflito, ciclo → `DetachedReason`, listado no topo,
problema `structure`); `GroupView` em árvore com `Children` e `Members`. Memberships: pares únicos;
penduradas → problema `membership.dangling`, mantidas. Problemas globais: storage root ausente/desconhecida/
arquivo no lugar/sem acesso, fonte `unavailable`/`Broken`, pastas inacessíveis, ids em conflito. Ações:
`RequestScan(Full)` quando a árvore nunca veio de varredura ou as regras mudaram; `RequestScan(Pending|Full)`
quando o manifesto está `pending`/`failed`; manifesto read-only → nenhuma ação. `LibraryService` executa:
locators reescritos vão pro manifesto, pedidos de varredura pro runner (com throttle de 5 s, exceto plano
mudado), relocation manual fica no modelo pra aba oferecer.

## 8. Operações (`LibraryOperations`)

| Operação | Disco | Manifest | Recusa / regra |
|---|---|---|---|
| Adicionar pasta / arquivos | nada | item com `source{name}` e locator (probe pro `fileId`); arquivo vai pro inbox | mesmo caminho já fonte → recusa; arquivo já coberto por fonte/storage root → "já está na biblioteca", nada criado; membro que vira fonte mantém o id; pasta dentro de outra fonte é permitida (1.1) |
| Importar arquivos | `StorageCopy.CopyInAsync`: temp `.~import-<guid>` no topo da storage root → validação → rename pra pasta escolhida **dentro** da storage root (default: a raiz) | nada (a árvore mostra) | nome já existente → " (2)", nunca sobrescreve; destino fora da storage root → recusa; sem permissão no destino → a cópia vai pra pasta Music (que vira fonte se não coberta), a aba diz, a configuração fica; cancelado → nada |
| Importar pasta | a pasta inteira como filha da storage root (subpastas vazias inclusive) | nada | " (2)" na colisão; recusa se já está dentro ou se contém a storage root |
| Criar pasta | `mkdir` na storage root ou numa pasta dentro dela | nada | nome existente → recusa; mesmo fallback de permissão |
| Criar / Renomear / Mover / Excluir grupo | nada | `groups` | pai inexistente → recusa; ciclo → recusa; excluir leva os grupos abaixo e as memberships deles |
| Adicionar a (menu ou drop) | nada | `EnsureItem` por alvo (idempotente por caminho canônico) + membership | `Outermost`: pasta selecionada junto com algo dentro dela vale pela subárvore; par repetido é ignorado; nunca remove outras memberships |
| Remover de <grupo> | nada | a membership; o item sem `source` e sem outra membership some junto | — |
| Mover fisicamente… | **a única que mexe no disco**: rename no volume; entre volumes cópia (tempos preservados) + apaga origem | locators de todo item sob o que moveu reescritos por prefixo (identidade relida no destino) — memberships intocadas; `MarkPending` | colisão em qualquer alvo → recusa **antes** de mover qualquer coisa; pasta pra dentro de si mesma → recusa; sem fallback |
| Localizar… | nada | locator do item | probe tem que achar algo do tipo certo |
| Mudar diretório padrão | nada | `storageRoot` (pasta Music → null) | o que acontece com o conteúdo e a raiz antiga: §12 |
| Ids em conflito | nada | ids novos (referências não reescritas) / remoção de todos | só pra ids em conflito |
| Download entregue (`Deliver`) | — | coberto → inbox; fora de tudo e `autoAddRoots` → a pasta de saída vira fonte + inbox | extensão fora do catálogo → ignorado |

Toda operação recusa num manifesto read-only. Nenhuma apaga arquivo.

## 9. Projeção e a aba

`LibraryProjection.Build(model, fontes, opções)` → linhas + totais do rodapé, pura:

- `⌄ Tudo`: as pastas da storage root (containers), as outras fontes de pasta (containers, com o nome da
  fonte), os arquivos da storage root e as fontes de arquivo. Depois, os grupos do topo. Todo container é a
  mesma linha colapsável (chevron, nome, contagem; grupo com um filete de accent). Grupo aberto → subgrupos,
  depois membros: pasta-membro é um container sobre a **mesma subárvore do Index** que "Tudo" mostra (nada
  copiado; `baz.mp3` novo em `Sets` aparece nos dois lugares sem membership); arquivo-membro é a linha do
  arquivo; membro não localizado é uma linha esmaecida com o diagnóstico e Localizar….
- Chave da linha = cadeia de containers + nó: o mesmo arquivo em N lugares são N linhas, uma entidade
  (`File`/`Item` iguais); os totais do rodapé contam cada caminho uma vez.
- Só o que está aberto é materializado (`_expanded` por chave; "Tudo" aberto de início, o resto fechado).
  Buscar abre tudo e esconde container sem resultado; limpar devolve os flags. Chip = filtro por fonte
  (grupo vazio continua listado: é onde se solta); Tipo e busca escondem container vazio. "Pastas" desligado
  = a lista plana da 1.1. A ordem vale dentro de cada container (containers antes: pastas, fontes, depois
  arquivos; nos grupos: subgrupos, pastas, arquivos, sumidos).
- A aba: `ListBox` `Extended` (multi-select), duas `DataTemplate`s, menu de contexto montado pra linha sob o
  mouse, drag/drop (linhas → grupo = membership; grupo → grupo ou "Tudo" = muda o pai; pasta física não
  recebe drop) — **desligado** em `LibraryView.DragAndDropEnabled`, o menu de contexto faz o mesmo —,
  `Adicionar ▾` no cabeçalho, nomes pela faixa inline, Opções com a seção da storage root. O
  resto — chips, busca, Tipo, Ordem, rodapé, status, avisos, Opções, VLC — é a 1.1 (`a293192`).

## 10. Caminhos: canonicalização e contenção (invariante)

`PathUtil` é o único lugar que decide "mesmo caminho" e "dentro de": `Normalize` (`GetFullPath`, `\\?\`
removido, UNC mantido, separador final tirado exceto raiz de volume), `Canonical` (minúsculas invariantes —
NTFS é case-insensitive), `TryNormalizeAbsolute` (rejeita relativo, device path, caracteres inválidos),
`IsSamePath`, `IsDescendantOrSame` (prefixo canônico **+ separador**: `C:\Music` não contém `C:\Music2`),
`IsProperDescendant`, `Relative`, `VolumeRootOf`. Nomes 8.3 não são expandidos; junctions não são
resolvidas (dois caminhos via junction são duas identidades; o scanner pula reparse points).

## 11. Upgrade e downgrade

- Primeira carga sem `manifest.v2.json`: `manifest.json` (1.x) → raiz igual à pasta Music vira a storage
  root implícita (nada gravado), as outras raízes viram fontes com o nome de cada uma; opções copiadas;
  `status: pending` (a primeira varredura constrói a árvore, lendo as tags de tudo uma vez). Snapshot
  `legacyV1` (raízes canônicas + fingerprint): raízes que o 1.x ganhar num downgrade entram uma vez; as que
  sumirem não são propagadas; `inbox\` do 1.x é traduzido por nome de bloco, uma vez. O 2.x nunca escreve
  os arquivos do 1.x.
- Os arquivos com a forma da branch 2.0 (schema 2: `nodes`, `managedRoot`, `generation`) nunca foram
  publicados: são postos de lado como `.bad` e o 1.x é migrado de novo. Não há conversão.

## 12. O que ficou pra depois (não bloqueia)

- **"Mudar diretório padrão…"** hoje só muda a configuração (`SetStorageRoot`); se o conteúdo muda de lugar e o
  que a raiz antiga vira (fonte? esquecida?) fica pra fechar.
- Item que sai de toda fonte mantendo membership fica "não observado" (sem tags, sem subárvore); tornar isso
  uma fonte implícita foi recusado por enquanto.
- Detalhes que são decisão local, não contrato: caches de `FullPath`, os lookups do Index, o algoritmo do
  Mover entre volumes, a forma das chaves de projeção, o filete do grupo.
