# Biblioteca — Manifest, Index e Filesystem: contrato do modelo (revisão 5)

Análise, não implementação. Revisão 5 em 2026-09-18, com os três blockers e os dois invariantes
apontados pelo dono sobre a revisão 4 (`Broken` sem estado não determinístico, cobertura
hierárquica do órfão, migração read-only do inbox v1, boundary disjunta Managed/ExternalLinked,
canonicalização e contenção de caminhos como invariante — §16); a taxonomia, a nomenclatura e
as decisões abaixo são o contrato pra autorizar o BLOCO 2. Tudo que está como "hoje" foi lido do código do plugin `Pixie.Library` 1.1.0
(`Catalog/LibraryModels.cs`, `LibraryManifest.cs`, `LibraryStore.cs`, `LibraryScanner.cs`,
`LibrarySync.cs`, `LibraryRow.cs`, `LibraryTabViewModel.cs`, `LibraryPlugin.cs`); tudo que está
como "proposta" ainda não existe.

Fora de escopo, por decisão: mover/apagar a origem depois de importar, `audioHash` obrigatório,
alvo `uri`, fontes remotas, abstração genérica de storage, refactor oportunista.

Os 15 itens pedidos estão numerados como seções §1–§15; antes deles, §0 é o que existe hoje e
por que muda.

---

## 0. O que existe hoje e o que muda

| Camada | Arquivo | Hoje | Identidade hoje |
|---|---|---|---|
| **Manifest** | `data\library\manifest.json` (`ManifestData`) | `roots[{name, path}]`, `player`, `autoAddRoots`, `autoSync`, `extraExtensions`, `groupByFolder`, `status`, `lastSync` | raiz = **`path`** (`RemoveAll(r => r.Path == …)`, `FindIndex(r => r.Path == …)`); sem id; `name` único só por convenção |
| **Index** | `data\library\index.json` (`IndexData` schema 1) | `scannedAt`, `directories[{path, modified}]`, `entries[{path, size, modified, created, tags…, chapters}]` | entrada = **`path`**; "mesmo arquivo" = `(size, modified)` (`SameFileAs`); não sabe de raiz (`RootOf` por prefixo na UI); sem id, sem `fileId` |
| **Inbox** | `data\library\inbox\*.json` | um bloco por arquivo entregue; prioridade da varredura; apagado depois do Index gravado | — |
| **Filesystem** | as raízes | a verdade | caminho; por baixo, o NTFS |

Comportamentos de hoje que o modelo novo mantém, renomeia ou corrige:

| Hoje | No modelo |
|---|---|
| Raiz = pasta externa varrida no lugar, nada copiado | **`PhysicalNode(Folder, ExternalLinked)`** — a mesma coisa, com id e locator (migração v1→v2, `origin: migrated`) |
| `autoAddRoots`: download fora de toda raiz cria uma raiz | cria um `PhysicalNode(Folder, ExternalLinked)` com `origin: autoAdd` — mantido |
| Raiz ausente → entradas e stamps mantidos, aviso | mantido, agora com `Unknown` (volume fora) separado de `Missing` (volume presente, pasta sumiu) |
| Pasta que falha ao listar → tratada como **vazia**, entradas somem | **corrigido** (§12): `Inaccessible`, entradas e stamp preservados |
| Manifest ilegível → `.bad` + manifesto novo (perde raízes) | `.bad` preservado + `manifest.v2.json.bak` da última carga boa como fallback (o mesmo desenho do `SettingsService`), porque o Manifest passa a ser a única casa da estrutura lógica |
| Geração do CAS em memória (`_generation` zera por processo) | mantida para o que ela faz (uma rodada contra um download no meio); `revision` e o ScanPlan são outra coisa (§8) |
| Raízes aninhadas: a mais funda "possui" o arquivo (`RootOf` por prefixo, na UI) | a varredura continua andando cada pasta uma vez; a **posição lógica** deixa de ser propriedade da entrada — o Index guarda só quem a produziu (`scanSourceNodeId`, técnico) e o reconciliador **projeta** a entrada sob todo nó cujo caminho a contém (§14) |
| Presence nunca conferida por arquivo depois da varredura | mantido como política (§5): `Unknown` até `stat` |

---

## 1. Taxonomia final

Dois eixos separados para o físico — **o que o recurso é** e **qual a relação da biblioteca com
o storage dele** — e dois tipos puramente lógicos.

```
Node
├── PhysicalNode            (kind: "physical")
│     physicalKind:  File | Folder
│     storageMode:   Managed | ExternalLinked
├── VirtualFolder           (kind: "virtualFolder")
└── Alias                   (kind: "alias")
```

| Nó | O que é | Relação | Onde mora o "onde" |
|---|---|---|---|
| **PhysicalNode(File, Managed)** | um arquivo que a biblioteca **copiou** pra dentro do storage dela | a biblioteca é dona da cópia; a origem deixou de importar | `managedPath` (relativo à managed root, §7) |
| **PhysicalNode(Folder, Managed)** | uma pasta copiada (estrutura e conteúdo) pro storage da biblioteca | idem; o conteúdo é **descoberto** pela varredura, não listado no Manifest | `managedPath` |
| **PhysicalNode(File, ExternalLinked)** | um arquivo que fica onde está; o Manifest guarda uma referência persistente | Manifest → Filesystem | `locator` (§9) |
| **PhysicalNode(Folder, ExternalLinked)** | uma pasta que fica onde está (a "raiz" de hoje); conteúdo descoberto pela varredura | Manifest → Filesystem | `locator` |
| **VirtualFolder** | container puramente lógico | não tem caminho, não está no Index | só `name` + `parent` |
| **Alias** | referência lógica pra outro nó | Manifest → Manifest (`targetNodeId`) | — |

Regras que fecham a taxonomia:

- **ExternalLinked não é Alias.** Um aponta pro disco (locator), o outro aponta pro Manifest (id).
  O "import por linkage" da rodada anterior é exatamente `PhysicalNode(…, ExternalLinked)`; o
  "physical import" é `PhysicalNode(…, Managed)`.
- **Entradas descobertas** (o que a varredura acha dentro de uma pasta física, managed ou
  linked) **não são nós**: são entradas do Index, penduradas abaixo do nó da pasta no modelo
  resolvido. O Manifest nunca as lista.
- **Extensão filtra descoberta, não nós explícitos**: um `PhysicalNode(File, …)` é indexado
  qualquer que seja a extensão; `extraExtensions` só decide o que a varredura pega dentro das
  pastas.
- **`parent` só pode ser um `VirtualFolder`** (ou nulo = topo). Pasta física não contém nós
  lógicos (seu conteúdo é descoberto); Alias e arquivo não contêm nada. Isso mantém "dentro de
  uma pasta física está o que o disco diz", sem misturar.
- **Managed quer dizer "sob o storage da biblioteca"**, não "nasceu de uma cópia". Um nó Managed
  nasce de Importar (cópia) **ou** de promoção (§ abaixo) — nos dois casos `managedPath` aponta
  pra algo que está na managed root.
- **Promoção**: uma entrada descoberta pode ser **promovida** a `PhysicalNode` explícito
  (`origin: promoted`), sem copiar nada: dentro de uma pasta Managed vira
  `PhysicalNode(File, Managed)` com `managedPath` = o caminho que ela já tem dentro da managed
  root; dentro de uma pasta ExternalLinked vira `PhysicalNode(File, ExternalLinked)` com o locator
  tirado da entrada. Um nó promovido **não** vira fonte nova do scanner enquanto a pasta que o
  cobre existir (§14). É assim que se cria um alias do que hoje é só uma linha da lista.
- **B8 significa só isto**: uma **importação** nunca copia pra dentro do subtree de outro nó
  Managed. Não proíbe um nó explícito para um arquivo que já está dentro de uma pasta Managed
  (é a promoção).
- **Alias pode apontar pra qualquer nó** (físico, virtual, outro alias); o que ele mostra e o que
  "abrir" faz vêm do alvo. Alias não aponta pra entrada descoberta (não é nó): "criar alias" numa
  entrada promove a entrada e aponta pro nó promovido.
- **Identidade de projeção e contagem = caminho normalizado resolvido.** É a semântica de caminho
  de §13 aplicada à contagem: o mesmo caminho, projetado sob vários nós, conta uma vez; um
  `fileId` trocado pelo TrackTracer no mesmo caminho não cria "outro" arquivo.
  `(volumeSerial, fileId)` é mecanismo de recuperação quando o caminho falha (§9), não identidade.
- **Nomes não são identidade**: dois irmãos podem se chamar "Favoritos"; renomear um nó não
  toca o disco (renomear no disco é outra operação, fora deste bloco).

## 2. Operações → nós

Operação é o que o usuário faz; nó é o que passa a existir. Nenhuma operação vira `kind`.

| Operação (botão) | O que faz | Nó criado | Toca a origem? |
|---|---|---|---|
| **Importar arquivo** | copia o arquivo pro storage gerenciado (Apêndice A) | `PhysicalNode(File, Managed)` | não — a origem fica intacta |
| **Importar pasta** | copia a pasta (estrutura + conteúdo) pro storage gerenciado | `PhysicalNode(Folder, Managed)` | não |
| **Adicionar arquivo** | não copia; guarda uma referência persistente ao arquivo onde ele está | `PhysicalNode(File, ExternalLinked)` | não |
| **Adicionar pasta** | não copia; referência persistente à pasta onde ela está (é o "Adicionar pasta" de hoje) | `PhysicalNode(Folder, ExternalLinked)` | não |
| **Nova pasta** | container lógico | `VirtualFolder` | não existe diretório |
| **Criar alias / atalho** | referência lógica a outro nó | `Alias` | — |

"Mover para a biblioteca" (copiar e apagar a origem) é uma política de ingestão futura, um comando,
não um tipo de nó — fora do BLOCO 2. O download do app continua como hoje: cai na pasta de saída
e, se ela não está no catálogo, `autoAddRoots` cria o `PhysicalNode(Folder, ExternalLinked)` dela.

---

## 3. A matriz

Classes de severidade (§4 define): **válido**, **transitório** (se resolve na próxima
varredura/reconciliação sem ninguém agir), **warning**, **erro**, **impossível por construção**.

### Dimensões

Cada nó carrega um **conjunto** de diagnósticos (vários ao mesmo tempo); cada dimensão responde
uma pergunta e é respondida por uma camada. "Sonda" = `stat` feito pelo reconciliador (§11);
"varredura" = o `LibraryScanner`.

**Presence** — "existe no disco, do jeito esperado, *agora*?" (responde: o filesystem, só quando
sondado)

| Valor | Significa |
|---|---|
| `Present` | sondado agora: existe e é do tipo esperado |
| `Missing` | sondado agora: não existe, e o volume/raiz acima existe |
| `Inaccessible` | sondado agora: existe, mas não dá pra ler/listar (ACL, lock, IO) |
| `TypeMismatch` | sondado agora: existe, mas é pasta onde se esperava arquivo (ou vice-versa) |
| `Unknown` | **não foi sondado nesta reconciliação** (o caso normal das entradas descobertas), ou o volume/raiz está fora e nada abaixo pode ser afirmado |

Política (item 5 do pedido): **lazy**. Nenhum `stat` por entrada ao abrir a aba. Sondados: os nós
físicos explícitos (managed e linked — poucos), a managed root, os alvos das cadeias de alias
(que são nós), e os itens que o usuário acabou de operar (abrir, mostrar na pasta). "Foi visto
na última varredura" **não** vira `Present`: vira `Unknown` com `lastObserved = index.scannedAt`,
que a UI pode mostrar como "visto em …" mas nunca como confirmação.

**Index** — duas perguntas distintas, com fontes distintas; por isso a **opção B** do pedido
(dois eixos), e não um enum só: *membership* sai do Index sozinho (um lookup por caminho, como
o `_byPath` de hoje); *consistency* exige comparar a entrada com uma sonda (o `SameFileAs` de
hoje) — e sem sonda não se pode afirmar `Current`. Nenhum dos dois é persistido: são derivados
na reconciliação, então não há enum novo em arquivo nenhum.

| IndexMembership | Significa |
|---|---|
| `Indexed` | há entrada pra este caminho |
| `Unindexed` | não há entrada (ainda não varrido; nó recém-adicionado) |
| `Excluded` | não há entrada **por regra** (extensão fora do catálogo, junction, pasta System) — só para descoberta; nó explícito nunca é `Excluded` |

| IndexConsistency (só quando `Indexed`) | Significa |
|---|---|
| `Current` | sondado agora: a **impressão digital observada ainda é compatível** — `(size, mtime)` batem com a entrada **e**, quando a entrada e a sonda têm `fileId`, ele também bate |
| `Stale` | sondado agora: existe, mas `size`, `mtime` **ou** `fileId` (quando os dois lados o conhecem) mudaram — as tags da entrada podem estar velhas; a varredura relê pela mesma regra (§6) |
| `Orphaned` | sondado agora: a entrada existe, o arquivo não |
| `Unknown` | não sondado nesta reconciliação, ou Presence `Unknown` |

**Resolution** — "o alvo deste nó se resolve?" (responde: Manifest para alias; Manifest + Index
+ sonda para ExternalLinked; não se aplica a Managed nem a VirtualFolder)

`Resolved | Relocated | Replaced | Broken | Unverifiable | MissingSource | Ambiguous | Cyclic |
StructuralConflict` — definidos na máquina de estados de §9 e nas tabelas C, D e F. Nenhum estado
depende de memória entre reconciliações: cada um é função do Manifest, do Index e da sonda
**desta** reconciliação (§9 explica por que `BrokenPending` saiu).

**Herança e agregado** (itens 17–19 do pedido): cada nó tem diagnósticos *self*; um alias
carrega os do alvo final como *inherited* (com a origem); um container tem *aggregate*
`{erros, warnings}` contado sobre os *self* dos descendentes. Herdados de uma raiz `Unknown` não
entram no agregado. A severidade de um nó deriva de self ∪ inherited; a do container, só de
self — o agregado é mostrado ao lado, não vira cor da pasta.

### As tabelas

### A. PhysicalNode(File, Managed)

Sondado a cada reconciliação (é nó explícito, são poucos). Sem locator, sem relocation: o
caminho é `managedRoot + managedPath` e ponto.

| # | Situação | Presence | Membership / Consistency | Classe | Como se vê | UI / ações |
|---|---|---|---|---|---|---|
| A1 | Cópia no lugar, entrada fresca | Present | Indexed / Current | válido | sonda + entrada batem | linha normal |
| A2 | Recém-importado, varredura ainda não rodou | Present | Unindexed | transitório | sem entrada | o import escreve um bloco em `inbox.v2\` → aparece na varredura seguinte (como um download hoje) |
| A3 | Conteúdo trocado no lugar (TrackTracer gravou capítulos; tag editor; `size`/`mtime`/`fileId` mudaram) | Present | Indexed / Stale | transitório | sonda ≠ entrada | tags relidas na próxima varredura (a troca por rename bumpa o mtime da pasta) |
| A4 | Apagado à mão dentro da managed root | Missing | Indexed / Orphaned até a varredura | **erro** | sonda | "arquivo gerenciado sumiu" — não há onde procurar; ações: **Reimportar…** (nova cópia) / **Remover**; a varredura solta a entrada, o nó fica |
| A5 | Trancado / sem permissão | Inaccessible | Indexed / Unknown | warning | sonda lança | abrir falha com o motivo; entrada mantida |
| A6 | No `managedPath` há uma pasta | TypeMismatch | — | **erro** | sonda | como A4 |
| A7 | Managed root inteira indisponível (está num volume desligado) | Unknown | Indexed / Unknown | warning (um só, na managed root) | volume da managed root não pronto | nó esmaecido; herdado, não conta no agregado (§11) |
| A8 | Managed root inteira sumiu (volume presente, pasta apagada) | Missing (todos) | Orphaned | **erro global** | sonda da managed root | banner "pasta da biblioteca não encontrada em <caminho>" — nada é apagado e **nada é reapontado** neste bloco (a root é imutável, §7; `RelocateManagedRoot` é operação futura); a pessoa devolve a pasta ao lugar |
| A9 | Mesmo `managedPath`, `fileId` diferente (substituído), mesmo com `size`/`mtime` iguais | Present | Indexed / Stale até a varredura | válido | sonda (`fileId` ≠ o da entrada) | nada a dizer: a biblioteca é dona do *slot* (§13); a varredura relê as tags porque o `fileId` mudou |
| A10 | Extensão fora do catálogo | — | — | **impossível** | — | nó explícito é sempre indexado |
| A11 | Origem (`importedFrom`) sumiu depois da importação | — | — | válido | — | irrelevante por desenho: a origem deixou de importar no commit |
| A12 | Nó **promovido** (entrada descoberta numa pasta Managed que ganhou nó explícito, `origin: promoted`, sem cópia) | Present | Indexed / Current | válido | — | não é fonte nova do scanner (a pasta já o cobre); é uma projeção a mais da mesma entrada; se o arquivo sumir, A4 |

### B. PhysicalNode(Folder, Managed)

Sondada a cada reconciliação. Conteúdo descoberto pela varredura (recursiva).

| # | Situação | Presence | Consistency | Classe | Como se vê | UI / ações |
|---|---|---|---|---|---|---|
| B1 | Pasta no lugar, varrida | Present | Current | válido | sonda | cabeçalho normal + agregado das entradas |
| B2 | Apagada à mão | Missing | Orphaned (entradas) | **erro** | sonda | como A4, no nível da pasta; entradas caem na varredura, o nó fica |
| B3 | Não dá pra listar (ACL/IO) | Inaccessible | Unknown | warning | varredura (§12) | entradas e stamp **preservados**; diagnóstico "não consegui ler" |
| B4 | No `managedPath` há um arquivo | TypeMismatch | — | **erro** | sonda | como A6 |
| B5 | Managed root indisponível / sumida | Unknown / Missing | — | como A7 / A8 | — | herdado da managed root |
| B6 | Subpasta descoberta apagada | (entradas) Missing | Orphaned | transitório | próxima varredura (mtime da mãe) | some da lista |
| B7 | Subpasta descoberta ilistável | Inaccessible | Unknown | warning | varredura (§12) | preservada, diagnóstico |
| B8 | **Importar** pra dentro de um nó Managed | — | — | **impossível** (a operação copia sempre como irmão no topo da managed root) | — | não confundir com A12: um nó promovido dentro de uma pasta Managed é permitido |
| B9 | Mesma origem importada duas vezes | — | — | válido | — | duas cópias, dois nós — redundante mas legítimo; a **operação** pode avisar "já importada em …" |
| B10 | Junction / symlink / pasta System dentro | — | Excluded | válido | atributo | puladas, como hoje |

### C. PhysicalNode(File, ExternalLinked)

Aqui mora a complexidade de locator. Sondado a cada reconciliação; resolução pela escada de §9.

| # | Situação | Presence | Membership / Consistency | Resolution | Classe | UI / ações |
|---|---|---|---|---|---|---|
| C1 | Caminho resolve, `fileId` bate | Present | Indexed / Current | Resolved | válido | linha normal |
| C2 | Caminho resolve, `fileId` **não** bate (re-download, tag editor, TrackTracer) — mesmo com `size`/`mtime` iguais | Present | Indexed / **Stale** até a varredura | **Replaced** (= Resolved) | válido (+ informação) | semântica de caminho (§13): `RewriteLocator` atualiza a impressão digital do nó; a varredura relê as tags (§6); nada pede confirmação |
| C3 | Caminho não resolve; `OpenFileById(volumeSerial, fileId)` acha em outro caminho (renomeado/movido no mesmo volume) | Present (no novo caminho) | Indexed se dentro de alguma fonte; senão Unindexed | **Relocated** | válido (+ informação "movido para …") | `RewriteLocator`; entrada velha vira Orphaned até a varredura |
| C4 | Caminho falha, volume disponível, `fileId` não acha (apagado, movido pra outro volume, copiado) | Missing | Orphaned até a varredura | **Broken** | **erro** | `RequestManualRelocation`: **Localizar…** (novo locator) / **Remover**. Sem estado intermediário: com o volume disponível, nada que uma varredura faça reencontra o recurso (§9) |
| C5 | Caminho falha, locator **sem** `fileId`/`volumeSerial` (nó migrado que nunca foi sondado presente), raiz do caminho existe | Missing | — | **Broken** | **erro** | idem C4; se a raiz do caminho (letra/share) não existe → C6 |
| C6 | Volume do `volumeSerial` não está montado | Unknown | Indexed / Unknown | **Unverifiable** | warning | esmaecido "disco desligado?"; não conta como quebrado; não gera scan |
| C7 | Existe, mas não abre (lock/ACL) | Inaccessible | Indexed / Unknown | Resolved | warning | abrir falha explicado |
| C8 | No caminho há uma pasta | TypeMismatch | — | **StructuralConflict** (tipo) | **erro** | como C5, com o motivo |
| C9 | Existe, entrada velha (Index atrasado) | Present | Indexed / Stale | Resolved | transitório | tags relidas na próxima varredura |
| C10 | Existe, sem entrada ainda (nó recém-adicionado ou recém-relocado; o Index ainda tem o caminho velho como Orphaned) | Present | Unindexed | Resolved / Relocated | transitório (só a dimensão Index) | o "Adicionar" escreve um bloco em `inbox.v2\` → aparece na varredura seguinte; a relocation já resolveu pelo `OpenFileById`, sem esperar varredura |
| C11 | O Index tem entradas que nenhum nó cobre mais (a fonte que as produziu foi removida; varredura ainda não rodou) | — | Indexed / — | **MissingSource** (na entrada) | transitório | sem projeção → não aparecem; o ScanPlan mudou → a varredura as solta |
| C12 | Dois nós ExternalLinked pro mesmo **caminho resolvido** | Present | Indexed (uma entrada) | Resolved (os dois) | válido (+ informação `RedundantReference`) | a mesma entrada é **projetada** sob os dois nós; conta uma vez (mesmo caminho); a UI pode oferecer "virar alias" — **não é erro**. Dois nós com o mesmo `fileId` em caminhos diferentes são dois arquivos, por decisão (§13) |
| C13 | Arquivo explícito dentro de uma pasta linked/managed que também o descobre (promovido ou adicionado) | Present | Indexed (uma entrada) | Resolved | válido | a entrada é projetada sob a pasta (descoberta) **e** sob o nó de arquivo; conta uma vez (mesmo caminho resolvido); o nó de arquivo **não** é fonte nova do scanner — está coberto |
| C14 | Arquivo movido pra **outro volume** (ou restaurado de backup) | Missing | — | Broken | **erro** (por decisão: sem `audioHash` no BLOCO 2) | Localizar… manual |
| C16 | `fileId` acha **duas** entradas no Index (hard links NTFS: dois caminhos, um objeto) | Present | Indexed | **Ambiguous** | warning | a UI oferece os candidatos; escolher = `RewriteLocator`. `OpenFileById` sozinho devolve um nome só — o dicionário do Index é que revela o outro |
| C15 | Nó promovido de uma entrada descoberta numa pasta linked (`origin: promoted`, locator copiado da entrada) | Present | Indexed / Current | Resolved | válido | como A12; se a pasta que o cobre for removida do Manifest, ele passa a ser fonte por si (a remoção já mudou o ScanPlan) |

### D. PhysicalNode(Folder, ExternalLinked)

A raiz de hoje. Sondada a cada reconciliação; conteúdo descoberto pela varredura.

| # | Situação | Presence | Resolution | Classe | UI / ações |
|---|---|---|---|---|---|
| D1 | Pasta no lugar, varrida | Present | Resolved | válido | cabeçalho normal + agregado |
| D2 | Caminho não resolve, volume presente; `OpenFileById` do **diretório** acha (renomeada/movida no volume) | Present (novo caminho) | **Relocated** | válido (+ informação) | `RewriteLocator`; o caminho novo muda o ScanPlan → a reconciliação seguinte pede varredura (§8) |
| D3 | Caminho falha, volume disponível, `fileId` do diretório não acha (apagada, movida pra outro volume) | Missing | **Broken** | **erro** | **Localizar…** / **Remover**; entradas **mantidas** até o usuário decidir (nunca apagadas por reconciliação) |
| D4 | Caminho falha, locator sem `fileId`/`volumeSerial` (raiz migrada nunca sondada presente), raiz do caminho existe | Missing | **Broken** | **erro** | idem D3; raiz do caminho inexistente → D5 |
| D5 | Volume desligado | Unknown | Unverifiable | warning (um só) | "disco desligado?"; filhos herdam Unknown e **não** entram no agregado |
| D6 | Não dá pra listar | Inaccessible | Resolved | warning | §12: entradas e stamps preservados |
| D7 | No caminho há um arquivo | TypeMismatch | StructuralConflict de tipo | **erro** | como D4 |
| D8 | Mesmo caminho, `fileId` diferente (pasta apagada e recriada com o mesmo nome) | Present | Replaced | válido (+ informação) | semântica de caminho (§13); conteúdo relistado na varredura (stamps não batem) |
| D9 | Linked dentro de linked | Present | Resolved | válido | a varredura anda a pasta **uma vez** (como hoje: a externa lista, a interna retorna cedo); as entradas de dentro são projetadas sob os **dois** nós; contam uma vez (mesmo caminho) |
| D12 | Linked que contém a managed root efetiva, ou linked dentro dela (**boundary**, §7) | — | **StructuralConflict** | **erro** | a operação recusa; se veio do arquivo (edição manual, migração de uma raiz v1 que contém a pasta do app), o nó vai pra Problems e **sai do ScanPlan** enquanto a violação durar; Importar fica bloqueado com o motivo |
| D10 | Mesma pasta linkada duas vezes (mesmo caminho resolvido) | Present | Resolved (os dois) | válido (+ `RedundantReference`) | a varredura anda uma vez (`scanSourceNodeId` = o nó que andou — bookkeeping, não posição); os dois nós projetam **todas** as entradas; contam uma vez; a **operação** continua recusando ("já está no catálogo"), como hoje |
| D11 | Junction / System dentro | — | — | válido | puladas |

### E. VirtualFolder

Não tem Presence nem Index. Só estrutura e agregado.

| # | Situação | Classe | UI / ações |
|---|---|---|---|
| E1 | `parent` nulo ou um VirtualFolder válido | válido | pasta normal; agregado dos descendentes |
| E2 | `parent` aponta pra id inexistente | **erro estrutural** | fora da árvore; em **Problemas** com **Mover pra raiz** / **Escolher pasta** — nada é alterado sozinho |
| E3 | Ciclo de `parent` (X→Y→X, X→X) | **erro estrutural** (todos os nós do ciclo) | fora da árvore; **Desfazer** (zera o `parent` que o usuário escolher) |
| E4 | `parent` é arquivo, pasta física ou alias | **erro estrutural** | como E2 |
| E5 | `parent` está em `ConflictedIds` | **erro estrutural** (Ambiguous) | como E2, depois que o conflito for resolvido (§10) |
| E6 | Filhos com erro | válido (self) + agregado `{erros, warnings}` | contador discreto no cabeçalho; a pasta **não** fica vermelha |
| E7 | Vazia | válido | — |
| E8 | Irmãos com o mesmo nome | válido | nome não é identidade; a UI pode desambiguar |

### F. Alias

`targetNodeId` resolvido com conjunto de visitados. O alias é **folha**: uma referência, nunca um
container — o alvo não é embutido debaixo dele (§11). Tem diagnósticos **próprios** (estrutura) e
**herdados** (do alvo final da cadeia) — nunca misturados.

| # | Situação | Self | Inherited | Classe | UI / ações |
|---|---|---|---|---|---|
| F1 | Alvo existe e é válido | Resolved | (os do alvo) | válido | mostra nome/estado do alvo (não o conteúdo); abrir = abrir o alvo |
| F2 | `targetNodeId` não existe | Broken | — | **erro** | "alvo removido"; **Remover** / **Apontar pra outro** |
| F3 | `targetNodeId` em `ConflictedIds` | Ambiguous / StructuralConflict | — | **erro** | resolve junto com §10 |
| F4 | Cadeia volta ao próprio alias | Cyclic (todos os aliases do ciclo) | — | **erro** | **Desfazer** |
| F5 | Alvo é outro alias (cadeia) | Resolved | (do alvo final) | válido | resolução segue a cadeia |
| F6 | Alvo existe mas está fora da árvore (E2–E5 nele) | Resolved | TargetStructurallyInvalid | warning (herdado) | abre mesmo assim se o alvo tiver arquivo |
| F7 | Alvo físico está Missing / Inaccessible / Unverifiable / Broken | Resolved | TargetMissing / TargetInaccessible / TargetUnverifiable / TargetBroken | severidade **herdada** (não é falha do alias) | UI diz "o alvo…", não "o alias…" |
| F8 | Alvo é VirtualFolder | Resolved | — | válido | atalho: abrir = **navegar** até a pasta; a subárvore dela **não** aparece sob o alias. `VirtualFolder A └── Alias X → A` é válido, não é ciclo (ciclo só existe em cadeia alias → alias) |
| F9 | Vários aliases pro mesmo alvo | Resolved | — | **válido** | — |
| F10 | Alias usado como `parent` de alguém | (o filho é E4) | — | erro estrutural **no filho** | o alias em si está ok |
| F11 | `parent` do alias inválido (E2–E5) | erro estrutural | — | **erro** | como qualquer nó |

### G. Global (Manifest e Index)

| # | Situação | Classe | O que se faz |
|---|---|---|---|
| G1 | Id lógico repetido | **erro** (`ConflictedIds`, §10) | todos os nós preservados; nenhum resolve; referências a ele → Ambiguous; ações explícitas |
| G2 | Manifest ilegível | **erro** | `manifest.v2.json.bad` preservado; carrega `manifest.v2.json.bak` (última carga boa) se houver; senão vazio + banner com o caminho do `.bad` |
| G3 | Manifest de schema mais novo | **erro** | carrega **só leitura**; banner "atualize o plugin"; nunca grava |
| G4 | Só existe `manifest.json` (v1) | transitório | migra na carga para `manifest.v2.json`; o `manifest.json` **não é tocado** — é a proteção de downgrade (§15) |
| G5 | `index.v2.json` ilegível ou ausente | transitório | `index.v2.json.bad`; reconstrói (varredura completa); **não toca o Manifest**; os nós ExternalLinked resolvem normalmente pela sonda (§9 não depende do Index) — só a dimensão Index fica `Unindexed` até a varredura |
| G6 | `index.generation ≠ manifest.index.lastReconciledGeneration` | transitório (gatilho) | reconcilia de novo; `AcknowledgeIndexGeneration` |
| G7 | `index.scanPlan.fingerprint ≠ fingerprint(ScanPlan atual)` | transitório (gatilho) | `RequestScan(ScanPlanChanged)` (completa) — o Index foi materializado com outro conjunto de fontes, outra managed root efetiva, outras extensões ou outras regras (§8) |
| G8 | Managed root ausente / desligada | ver A8 / A7 | — |
| G9 | Nó de `kind` desconhecido (escrito por versão mais nova) | warning | preservado byte a byte na regravação; listado em Problemas como "desconhecido" |
| G10 | **Managed Storage Orphan** (§7): caminho dentro da managed root efetiva, **não coberto** por nenhum `PhysicalNode(*, Managed)` (pasta cobre o subtree, arquivo cobre a si) e fora de pasta temporária conhecida (`.~import-*`) — o que sobra de uma cópia sem commit (Apêndice A) ou do que alguém largou lá | informação | listados como "órfãos do storage" com **Adotar** (vira nó Managed, `origin: adopted`) / **Apagar** — nunca apagados sozinhos |

## 4. Classificação dos estados

| Classe | Estados |
|---|---|
| **Válido** | A1, A9, A11, A12, B1, B9, B10, C1, C2, C3, C10 (resolução), C12, C13, C15, D1, D2, D8, D9, D10, D11, E1, E6 (self), E7, E8, F1, F5, F8, F9 |
| **Transitório** | A2, A3, B6, C9, C10 (dimensão Index), C11, G4, G5, G6, G7 |
| **Warning** | A5, A7, B3, B5 (= A7), B7, C6, C7, C16, D5, D6, F6, F7 (herdado conforme o alvo), G9; agregados de E6 |
| **Erro** | A4, A6, A8, B2, B4, C4, C5, C8, C14, D3, D4, D7, D12, E2, E3, E4, E5, F2, F3, F4, F11, G1, G2, G3 |
| **Impossível por construção** | A10 (nó explícito com extensão excluída), B8 (importar pra dentro de um nó Managed), Orphaned junto com Present, Cyclic na árvore física (junctions puladas), Ambiguous por caminho (caminho canônico é único, §16), `Broken` "pendente" (não há memória entre reconciliações, §9), managed root dentro de linked ou linked dentro da managed root sem Problem (D12), alias apontando pra entrada descoberta (não é nó), nó que é alias e pai ao mesmo tempo (F10 é recusado no filho), Relocated/MissingSource em Managed (§13 e §14 daqui; item 22 do pedido), entrada do Index sem `scanSourceNodeId` (a varredura sempre registra quem a produziu), entrada com posição lógica gravada no Index (posição é projeção, nunca campo) |

Regras aplicadas: id duplicado é erro (G1); alvo duplicado **não** é (C12, D10, F9 —
`RedundantReference` é informação, não warning); entradas herdadas de uma raiz `Unknown` não
entram no agregado; um nó pode carregar vários diagnósticos (C7 = Resolved + Inaccessible +
Unknown de consistência).

## 5. Manifest v2

```jsonc
{
  "schemaVersion": 2,
  "revision": 42,                  // +1 em toda gravação (§8); não existe scanRevision — o ScanPlan é derivado (§8)
  "index": { "lastReconciledGeneration": 17 },
  "legacyV1": {                    // o que o 2.x já viu do lado v1 (§15) — o 2.x nunca escreve manifest.json nem inbox\
    "observedAt": "…",
    "fingerprint": "sha256 das raízes v1, canônicas e ordenadas",
    "observedRoots": [ "C:\\Users\\Sakamoto\\Music" ],
    "observedInbox": [ "20260918-093012-417-001-3f2a….json" ]   // blocos de inbox\ já traduzidos pra inbox.v2\
  },

  "status": "synced",              // a máquina de estados do sync, intocada: pending | synced | syncing | failed
  "lastSync": "…",
  "managedRoot": null,             // null = <data\library>\managed (§7); absoluto se escolhido antes do primeiro nó Managed; depois, imutável
  "player": null, "autoAddRoots": true, "autoSync": true, "extraExtensions": ["gif"], "groupByFolder": true,

  "nodes": [
    { "id": "3f0e…", "kind": "physical", "physicalKind": "folder", "storageMode": "externalLinked",
      "name": "Music", "parent": null, "origin": "migrated",           // manual | autoAdd | migrated | promoted | adopted
      "locator": { "lastKnownPath": "C:\\Users\\Sakamoto\\Music", "volumeSerial": "1a2b3c4d", "fileId": "0005…" } },

    { "id": "9b77…", "kind": "physical", "physicalKind": "file", "storageMode": "externalLinked",
      "name": "foo.mp3", "parent": "c1d2…", "origin": "manual",
      "locator": { "lastKnownPath": "D:\\Music\\foo.mp3", "volumeSerial": "…", "fileId": "…", "size": 123, "mtime": "…" } },

    { "id": "5aa0…", "kind": "physical", "physicalKind": "folder", "storageMode": "managed",
      "name": "Sets", "parent": null, "managedPath": "Sets",
      "importedFrom": "D:\\Sets", "importedAt": "…" },                // proveniência, só informação

    { "id": "7e13…", "kind": "physical", "physicalKind": "file", "storageMode": "managed",
      "name": "set do sábado.mp3", "parent": "c1d2…", "managedPath": "set do sábado.mp3",
      "importedFrom": "D:\\Solto\\set.mp3", "importedAt": "…" },

    // Managed promovido (A12): nomeia um arquivo que já está dentro de "Sets"; nada foi copiado
    { "id": "a4b5…", "kind": "physical", "physicalKind": "file", "storageMode": "managed",
      "name": "abertura.mp3", "parent": "c1d2…", "managedPath": "Sets\\2019\\abertura.mp3", "origin": "promoted" },

    { "id": "c1d2…", "kind": "virtualFolder", "name": "Favoritos", "parent": null },

    { "id": "e0f1…", "kind": "alias", "name": null, "parent": "c1d2…", "targetNodeId": "9b77…" }
  ]
}
```

- `id`: GUID gerado na criação; a única identidade lógica. `name`: rótulo (nulo no alias =
  herda o nome do alvo). Ordem do array não significa nada.
- `physicalKind` × `storageMode`: os dois eixos de §1. Managed tem `managedPath`; ExternalLinked
  tem `locator`; nunca os dois.
- `locator` (§9): `lastKnownPath` absoluto normalizado, `volumeSerial` + `fileId` (NTFS), e para
  arquivo `size` + `mtime`. Sem `audioHash` (campo opcional futuro, ignorado se ausente).
- `parent`: nulo ou id de `virtualFolder` (§1).
- **Round-trip**: propriedades desconhecidas preservadas na regravação (`JsonExtensionData` nos
  nós e na raiz), pra uma versão mais nova não perder nada numa mais velha.
- **`ConflictedIds` não é persistido**: é calculado na carga (§10). Os nós ficam em `nodes`
  como estão.
- **Arquivo**: `data\library\manifest.v2.json`. O `manifest.json` de hoje continua sendo o v1 e
  **nunca é escrito pelo 2.x** — é isso que protege um downgrade (§15).
- **Migração v1 → v2** (só quando não existe `manifest.v2.json`): cada `roots[i]` vira
  `physical/folder/externalLinked` com id novo, `origin: migrated`,
  `locator.lastKnownPath = path` (`volumeSerial`/`fileId` preenchidos na primeira reconciliação
  em que a pasta esteja presente); `revision = 1`; os demais campos copiados; `legacyV1` recebe o
  snapshot das raízes v1. `manifest.json` fica como está. `Generation` (CAS) continua em memória.
- **Reentrada depois de um downgrade**: pelo snapshot `legacyV1`, nunca por "v1 atual − v2 atual"
  (§15 — senão uma raiz removida no 2.x ressuscitaria a cada start).

## 6. Index v2

```jsonc
{
  "schemaVersion": 2,
  "generation": 17,                 // +1 por gravação, persistente (§8)
  "scanPlan": {                     // o plano efetivo que esta varredura materializou (§8)
    "fingerprint": "sha256…",
    "effectiveManagedRoot": "C:\\…\\data\\library\\managed",
    "folderSources": [ { "nodeId": "3f0e…", "path": "C:\\Users\\Sakamoto\\Music" } ],
    "fileSources":   [ { "nodeId": "9b77…", "path": "D:\\Music\\foo.mp3" } ],
    "extensions": [ ".aac", "…", ".gif" ],
    "rulesVersion": 2
  },
  "scannedAt": "…",
  "directories": [ { "path": "…", "modified": "…" } ],
  "inaccessibleDirectories": [ "C:\\…\\pasta" ],     // §12: o que falhou nesta varredura (entradas preservadas)
  "entries": [
    { "path": "…", "size": 123, "modified": "…", "created": "…",
      "volumeSerial": "…", "fileId": "…",             // colhidos no mesmo passo em que o arquivo é (re)listado
      "scanSourceNodeId": "3f0e…",                    // quem PRODUZIU a entrada nesta varredura — bookkeeping; NÃO é onde ela aparece (§14)
      "title": "…", "artist": "…", "album": "…", "duration": 1, "bitrate": 1, "chapters": 0 }
  ]
}
```

- **Arquivo**: `data\library\index.v2.json` (o `index.json` v1 fica pro 1.x; é descartável nos
  dois sentidos).
- Continua **descartável**: `index.v2.json.bad` → reconstrói. Nada lógico mora aqui; nada do
  Manifest aponta pra nada daqui por id — só por caminho/impressão digital, que sobrevivem.
- Uma entrada é **estado físico observado** e só isso. Onde ela aparece na biblioteca é
  **projeção** do reconciliador (§14): a mesma entrada pode aparecer sob vários nós, e nenhum
  campo do Index diz isso. `scanSourceNodeId` registra qual fonte a listou (pastas aninhadas ou
  duplicadas são andadas uma vez, então é "quem andou", uma escolha técnica) — serve pra
  bookkeeping da varredura e pra C11; nunca pra posicionar.
- `volumeSerial`/`fileId`: um handle por arquivo (re)listado (`GetFileInformationByHandleEx`
  com `FileIdInfo`); entradas reaproveitadas pela incremental não pagam nada. Usos: (a) o
  dicionário `(volumeSerial, fileId) → entrada` que faz o degrau 2 de §9 ser um lookup quando o
  arquivo está dentro de alguma fonte; (b) a **identidade de "as tags ainda valem"** da
  varredura passa a ser `(size, mtime, fileId quando os dois lados o conhecem)` — o `SameFileAs`
  de hoje mais o `fileId`, a mesma regra do `Current` de §3. Arquivo trocado no mesmo caminho com
  `size`/`mtime` iguais (A9, C2) tem as tags relidas.
- `scanPlan` é o plano que a varredura leu do Manifest e do ambiente ao começar (§8); o
  reconciliador compara o `fingerprint` com o do plano atual (G7). `effectiveManagedRoot` fica
  gravado por inteiro pra diagnóstico ("o Index foi feito com a biblioteca em …").
- Sem `index.v2.json` (primeira execução do 2.x, ou `.bad`) → `generation 0`, sem `scanPlan` →
  G7 → varredura completa (é a que preenche `fileId` e `scanSourceNodeId`). O `index.json` v1
  não é lido pelo 2.x: relistar é mais barato que migrar um arquivo descartável.

## 7. Managed root

- **Onde**: `managedRoot` do Manifest; nulo = `<IPluginHost.DataDirectory>\managed`, isto é
  `data\library\managed\` ao lado do `.exe` — a convenção do app (tudo ao lado do exe, `data\<id>\`
  nunca é apagado pelo host; e um app portátil movido de pasta leva a managed root junto, porque
  o valor nulo é relativo ao data dir).
- **Imutável depois de usada**: `managedRoot` (nulo ou absoluto) só pode ser alterada **enquanto
  não existe nenhum nó Managed**. Com conteúdo Managed, trocar só o valor reinterpretaria todos os
  `managedPath` e apontaria pra outro lugar — inválido. A UI desabilita a opção com o motivo.
  Uma futura operação explícita `RelocateManagedRoot` (copiar/mover → validar → commit da root
  nova) fica fora deste bloco; A8 (root sumida) também não reaponta nada.
- **Como os caminhos são persistidos**: `managedPath` **relativo** à managed root — `Sets`,
  `set do sábado.mp3` para importações (sempre no topo), `Sets\2019\abertura.mp3` para um nó
  promovido (nomeia algo que já está lá). O que está abaixo de uma pasta importada e não foi
  promovido é descoberto — a entrada `…\managed\Sets\2019\x.mp3` vive no Index, com caminho
  absoluto, como qualquer outra.
- **Layout**: nome legível, gerado na importação a partir do nome de origem, com sufixo de
  colisão (`Sets (2)`); uma importação nunca copia pra dentro do subtree de outro nó (B8).
  Renomear o nó **não** renomeia no disco; o `managedPath` só muda por operação explícita (fora
  do bloco). Alternativa considerada e recusada: `managed\<id>\<nome>` — à prova de colisão, mas
  ilegível pra quem abre a pasta no Explorer, e o dono abre.
- **O que a managed root não é**: não é nó, não é varrida inteira. Só os subtrees/arquivos dos
  nós managed são fontes do scanner.
- **Managed Storage Orphan** (G10), definição exata: um caminho `P` é órfão sse `P` está sob a
  managed root efetiva **e** `P` não está sob o caminho resolvido de nenhum `PhysicalNode(*, Managed)`
  (nó de pasta cobre o subtree inteiro; nó de arquivo cobre a si mesmo) **e** `P` não está sob uma
  pasta temporária conhecida (`.~import-*`). "Não ter nó próprio" **não** é o critério:
  `managed\Sets\a.mp3` sem nó é conteúdo descoberto válido de `Sets`.
  **Algoritmo** (cobertura hierárquica — uma listagem do primeiro nível **não** basta: se a pasta
  `Sets` deixa de ser nó e o promovido `Sets\2019\abertura.mp3` fica, `Sets\foo.mp3` e
  `Sets\bar.mp3` viraram órfãos embora `Sets` seja prefixo de um `managedPath`). Com os
  `managedPath` canônicos num trie/conjunto de prefixos, a partir da root:

  ```
  Visit(dir):
    para cada filho C de dir (canônico, §16):
      C é .~import-*                                   → pular
      existe nó de pasta Managed com IsSamePath(C)     → coberto, não descer
      existe nó de arquivo Managed com IsSamePath(C)   → coberto
      C é diretório e é ancestral próprio de ≥1 managedPath (prefixo no trie)
                                                       → Visit(C)          (cobertura parcial: procurar irmãos)
      senão                                            → órfão (C e tudo abaixo), não descer
  ```

  Pasta Managed cobre o subtree inteiro; arquivo Managed cobre só o caminho exato; diretório que
  é apenas prefixo é percorrido. Custo proporcional ao número de diretórios "prefixo parcial",
  nunca ao acervo. Roda no start e depois de cada importação/promoção/remoção de nó Managed;
  resultado vai pra `Problems` como informação (definição ≡ algoritmo: um caminho é reportado sse
  satisfaz a definição acima).
- **Boundary Managed / ExternalLinked — disjunta, por decisão** (D12): o caminho resolvido de um
  nó ExternalLinked **não** pode ser a managed root efetiva nem estar sob ela; a managed root
  efetiva **não** pode estar sob o caminho resolvido de um `PhysicalNode(Folder, ExternalLinked)`.
  Um recurso que está fisicamente dentro da managed root só existe como `Managed` (importado,
  promovido ou adotado). Onde se garante: "Adicionar arquivo/pasta" recusa (com sugestão: Importar
  / Adotar, ou uma subpasta que não contenha a pasta da biblioteca); "Pasta da biblioteca"
  (`managedRoot`, só antes do primeiro nó Managed) recusa um caminho dentro de uma pasta linked;
  `autoAddRoots` não cria nó pra download que caiu dentro da managed root (vira órfão G10, com
  **Adotar**); na carga/reconciliação, um Manifest que viola (edição manual; migração de uma raiz
  v1 que contém a pasta do app) põe o nó linked em Problems como `StructuralConflict`, **tira-o
  do ScanPlan** enquanto durar, e bloqueia Importar com o motivo. Consequências: ownership nunca
  se sobrepõe; o scanner do 1.x, num downgrade, só alcança a managed root se o usuário tiver uma
  raiz v1 que a contenha — e essa raiz é exatamente a que o 2.x marca como violação e recusa
  criar, então "o 1.x não enxerga `managed\`" vale enquanto o Manifest é válido. Junctions não
  furam a regra: o scanner pula reparse points.
- **Root efetiva e app portátil**: com `managedRoot` nulo, mover a pasta do app muda a root
  efetiva e, com ela, os caminhos resolvidos de todos os nós Managed — o Index antigo tem caminhos
  absolutos velhos. Isso é detectado pelo ScanPlan (§8), não por coincidência: os caminhos
  resolvidos das fontes managed entram no fingerprint, então a mudança pede varredura completa
  (as tags dos arquivos managed são relidas uma vez, porque os caminhos são "novos" — custo
  aceito; otimizar por `fileId` fica pra depois).
- **Pastas de trabalho** dentro dela: `.~import-<guid>\` (Apêndice A), apagadas no start se sobrarem —
  o mesmo padrão de `.~store-*` e `.~vlc-*`.

## 8. revision, ScanPlan e index generation

### O ScanPlan efetivo

O que o scanner observa é uma **função** do Manifest e do ambiente — não é persistido no
Manifest, é derivado sempre que preciso:

```
ScanPlan(manifest, environment) =
  effectiveManagedRoot  = manifest.managedRoot ?? <DataDirectory>\managed        (normalizado)
  folderSources         = { caminho resolvido de cada PhysicalNode(Folder, *) que NÃO está sob
                            o caminho resolvido de outro PhysicalNode(Folder, *) }  (o conjunto cobridor)
  fileSources           = { caminho resolvido de cada PhysicalNode(File, *) que NÃO está sob
                            nenhum folderSource }
  extensions            = FileKinds.All(manifest.extraExtensions)
  rulesVersion          = constante do scanner (sobe quando a observação muda de regra: B5, fileId…)

fingerprint(ScanPlan) = SHA-256 do texto canônico: folderSources e fileSources ordenados e em
                        minúsculas, extensions ordenadas, rulesVersion
```

- "Caminho resolvido": Managed = `effectiveManagedRoot + managedPath`; ExternalLinked =
  `locator.lastKnownPath` (o que a última reconciliação deixou lá).
- `effectiveManagedRoot` **não** é um campo separado do fingerprint: ela já está dentro dos
  caminhos resolvidos das fontes managed. Sem nó Managed, mover o app não muda o plano — correto,
  não há nada managed a reobservar. Com nós Managed, muda todos os caminhos deles → muda o plano.
  O Index guarda a root efetiva por inteiro só pra diagnóstico (§6).
- Nó coberto (arquivo dentro de pasta-fonte; pasta dentro de pasta-fonte; nó promovido) **não
  aparece** no plano — adicioná-lo, promovê-lo, movê-lo de pasta virtual ou curar seu locator
  dentro da mesma pasta-fonte não muda nada do que se observa.

### Os campos

| Campo | Onde | Muda quando | Serve para |
|---|---|---|---|
| `manifest.revision` | Manifest | **toda** gravação persistente (nós, opções, locators curados, geração reconhecida, `legacyV1`) | concorrência: "o Manifest mudou desde que eu li" (CAS da camada de aplicação; base pra multi-escritor no futuro) |
| `fingerprint(ScanPlan)` | derivado, nunca gravado no Manifest | quando muda **o plano efetivo**: o conjunto cobridor de pastas-fonte, o conjunto de arquivos-fonte descobertos, a managed root efetiva (via caminhos), `extraExtensions`, `rulesVersion` | saber se o Index materializou outro plano |
| `index.scanPlan` (+ `fingerprint`) | Index | gravado pela varredura com o plano que ela leu ao começar | comparar com o plano atual (G7) |
| `index.generation` | Index | toda gravação do Index (uma por varredura) | saber se o modelo precisa reconciliar de novo (G6) |
| `manifest.index.lastReconciledGeneration` | Manifest | quando a aplicação executa `AcknowledgeIndexGeneration` | comparar com `index.generation` |
| `Generation` (CAS) | memória | `MarkPending` (download, importação, pasta adicionada) | a rodada de sync contra um download no meio — como hoje, inalterado |

### Regra de invalidação

```
plano atual ≠ index.scanPlan.fingerprint   →  RequestScan(ScanPlanChanged, full: true)      (G7: reobserve)
index.generation ≠ lastReconciledGeneration →  reconciliar de novo + AcknowledgeIndexGeneration (G6: reinterprete)
status = pending (inbox, download, mtime)   →  RequestScan(Pending, full: false)             (como hoje)
```

Exemplos, com `Music` já coberto por `PhysicalNode(Folder, ExternalLinked)`:

| Mudança | `revision` | plano |
|---|---|---|
| Adicionar ou promover `PhysicalNode(File, …)` para `Music\foo.mp3` | +1 | **igual** — o scanner já observava exatamente esse conjunto |
| `RewriteLocator` desse arquivo para outro caminho **dentro** de `Music` (C3) | +1 | igual |
| `RewriteLocator` desse arquivo para fora de qualquer pasta-fonte | +1 | **muda** (entra em `fileSources`) → varredura |
| `RewriteLocator` da pasta `Music` renomeada (D2) | +1 | **muda** → varredura |
| Adicionar pasta ExternalLinked **dentro** de `Music` (D9) | +1 | igual (coberta) |
| Remover `Music` tendo um arquivo promovido dentro | +1 | **muda** (a pasta sai; o arquivo, agora descoberto, entra) |
| Renomear VirtualFolder, mover alias, mudar `parent`, `AcknowledgeIndexGeneration`, trocar player | +1 | igual |
| `extraExtensions`, `managedRoot` (só antes do primeiro nó Managed), mover o app com nós Managed | +1 (ou nada, no caso do app) | **muda** |

Por que isso não entra em loop: a reconciliação grava `revision`, mas o plano só muda se um
caminho de fonte mudou — e a cura de locator é **idempotente**. Sequência de uma pasta
renomeada: reconcile → `RewriteLocator` → plano muda → G7 → varredura (Index com o plano novo,
`generation 18`) → G6 → reconcile → nada a curar → `AcknowledgeIndexGeneration(18)` → estável.
Um download dentro de uma pasta-fonte não muda o plano; passa pelo inbox + `status: pending`.

## 9. Locator inicial (sem `audioHash`)

```
locator = { lastKnownPath, volumeSerial, fileId, size?, mtime? }
```

Máquina de estados de resolução, a cada reconciliação de um nó ExternalLinked — **função só do
Manifest, do Index e da sonda desta reconciliação**, sem memória:

```
sonda(lastKnownPath):
  Present, tipo certo:
      locator sem fileId, ou fileId igual                → Resolved
      fileId diferente                                   → Replaced (= Resolved, §13) + RewriteLocator
  Present, tipo errado                                   → StructuralConflict (C8/D7)
  Inaccessible                                           → Resolved + Inaccessible (C7/D6): existe, não abre
  Missing:
      volume disponível?  = volumeSerial conhecido ? IsVolumeReady(volumeSerial)
                                                    : a raiz de lastKnownPath (letra/share) existe
      não                                                → Unverifiable (C6/D5)
      sim, fileId conhecido:
          Index[(volumeSerial, fileId)] com 2+ caminhos existentes  → Ambiguous (C16, hard links)
          Index[…] com 1 caminho existente, ou OpenFileById(volume, fileId) + GetFinalPathNameByHandle
                                                         → Relocated + RewriteLocator (C3/D2)
          nada                                           → Broken + RequestManualRelocation (C4/D3)
      sim, fileId desconhecido                           → Broken + RequestManualRelocation (C5/D4)
```

`BrokenPending` saiu. Motivo: `Build` é puro e nada no Manifest, no Index nem no runtime guarda
"em que geração a falha foi vista pela primeira vez" — o estado só seria determinístico com uma
memória persistente nova (`failureObservedAtGeneration` por nó), e ela não compraria nada: com o
volume disponível, caminho e `fileId` falhando, **uma varredura não tem outro mecanismo pra
reencontrar o recurso** — o `OpenFileById` já consulta o filesystem inteiro do volume sem
depender do Index, e `audioHash` está fora do bloco. Logo Broken é decidido na hora, e o único
caminho de volta é manual (Localizar…) ou a recuperação por `audioHash` num bloco futuro.

- `OpenFileById` é API pública do Win32 (`hVolumeHint` = qualquer handle no volume, por exemplo
  a raiz `C:\` aberta com `FILE_FLAG_BACKUP_SEMANTICS`; sem admin) — acha um arquivo/pasta
  movido pra qualquer lugar do mesmo volume sem depender do Index. O dicionário do Index é só o
  atalho barato.
- `audioHash` fica como **degrau 4 futuro**, só sob demanda e só sobre o trecho invariante do
  áudio (TagLib# expõe `InvariantStartPosition/EndPosition`); cobre C14 (outro volume, backup).
  Managed nunca precisa dele pra localizar.
- **Localizar…** (manual) = o usuário aponta o arquivo/pasta; o nó recebe um locator novo
  (`RewriteLocator`); se o caminho novo muda o ScanPlan (§8), a reconciliação seguinte pede
  varredura.

## 10. ConflictedIds

Na carga: `ConflictedIds = { id | aparece em 2+ nós }`.

- **Todos** os nós com id conflitado são preservados em `nodes` e regravados como estão; nenhum
  "vence"; nenhum entra na árvore resolvida.
- Qualquer referência a um id conflitado (`parent`, `targetNodeId`) resolve como
  `Ambiguous`/`StructuralConflict` (E5, F3) — o que elimina a contradição entre "id duplicado"
  e "lookup ambíguo": é a mesma coisa vista de dois lugares.
- `Problems` lista o conflito com os nós envolvidos (nome, kind, o que apontam) e as ações,
  todas explícitas: **Gerar novo id** (num nó que o usuário escolhe — e opcionalmente reapontar
  as referências pra ele), **Remover nó**, **Reparar referência** (apontar um alias/parent pra
  um dos nós). Nunca auto-reparo.
- O mesmo tratamento para estrutura inválida (E2–E5, F10, F11): preservar, tirar da árvore,
  listar, reparar por clique.

## 11. Reconciler / ReconcileResult / Actions

```
Reconciler.Build(ManifestData manifest, IndexData index, IFileProbe probe, ProbeScope scope) → ReconcileResult
```

- **Puro**: não grava Manifest, não grava Index, não dispara varredura, não altera o
  filesystem. Lê o Manifest e o Index (imutáveis) e pergunta ao `probe` **só** sobre o que o
  `scope` autoriza (§5 do pedido): os caminhos dos nós físicos (managed e linked; poucos), a
  managed root, e uma lista explícita de "itens operados" que a camada de aplicação acrescenta
  (o arquivo que o usuário acabou de tentar abrir). Entradas descobertas **não** são sondadas:
  ficam `Presence = Unknown` / `Consistency = Unknown`.
- `IFileProbe`: `Stat(path) → { Presence, IsFolder, Size, Mtime, VolumeSerial, FileId }`,
  `IsVolumeReady(volumeSerial)`, `FindByFileId(volumeSerial, fileId) → path?`
  (`OpenFileById`). Testável com um probe falso, como o `FakeTagReader` do scanner.

```
ReconcileResult
├── Model : LibraryModel (imutável)
│     ├── Nodes        árvore resolvida: nós estruturalmente válidos, com filhos. Alias é FOLHA:
│     │                AliasModel { TargetNodeId, ResolvedTargetId, ResolvedTargetKind } — nunca tem filhos,
│     │                o alvo não é embutido; abrir/navegar é a UI seguindo ResolvedTargetId
│     ├── Projections  sob cada nó de pasta física, toda entrada cujo caminho resolvido está sob o do nó;
│     │                sob cada nó de arquivo, a entrada do seu caminho resolvido — a mesma entrada pode
│     │                ter várias projeções (C12, C13, D9, D10); identidade pra contar = caminho resolvido
│     ├── Diagnostics  por nó/entrada: Self (conjunto), Inherited (conjunto, com a origem), e a
│     │                severidade derivada dos dois
│     ├── Aggregates   por container: {erros, warnings} dos descendentes (self de cada um; herdados
│     │                de raiz Unknown não contam)
│     ├── Problems     estrutural/global: ConflictedIds, nós fora da árvore (com o motivo), G2–G5, G9, G10
│     └── Status       G6/G7 pendentes, managed root (A7/A8)
└── Actions : lista de mudanças propostas, nenhuma executada
      ├── RewriteLocator(nodeId, locator)                // sem flag de scan: o plano é comparado depois (§8)
      ├── AcknowledgeIndexGeneration(generation)
      ├── RequestScan(reason: ScanPlanChanged | Pending, full)
      └── RequestManualRelocation(nodeId, reason)        // vira botão na UI, não side effect
```

A **camada de aplicação** (o `LibraryPlugin`/um `LibraryService` pequeno) roda `Build` depois
de: Index carregado, varredura terminada, Manifest alterado, item operado; e executa as
`Actions`: `RewriteLocator`/`AcknowledgeIndexGeneration` → `LibraryManifest.Update`
(`revision++`); `RequestScan` → `LibrarySync.Request`;
`RequestManualRelocation` → nada (o modelo já carrega o diagnóstico; a UI mostra o botão). O VM
constrói linhas e grupos do `Model`, como hoje constrói do `IndexData`. Convergência: `Build` é
determinístico e suas ações são idempotentes (§8).

## 12. Correção de B5 — Inaccessible ≠ Empty

Hoje `Walker.Walk` tem **dois** caminhos que apagam: (1) `Directory.GetLastWriteTimeUtc(dir)`
lança → `return` antes de `stamps[dir] = mtime` → nem o stamp nem as entradas anteriores entram
em `current`; (2) `EnumerateFileSystemInfos()` lança → `catch` vazio → o stamp fica gravado com o
mtime novo e nenhuma entrada é achada → as entradas anteriores dessa pasta somem (`removed`). Em
ambos, a próxima varredura que precisar listar a pasta (completa, ou mtime mudou) "prova" uma
ausência que ninguém observou.

Correção (pré-requisito explícito do BLOCO 2, primeira tarefa da implementação):

- Em qualquer falha ao carimbar ou listar `dir`: **preservar** o stamp anterior (se havia), as
  entradas anteriores de `dir` e, recursivamente, o que o Index já sabia dos subdiretórios
  conhecidos (exatamente o que o ramo de raiz ausente já faz), e registrar `dir` em
  `ScanResult.InaccessibleDirectories` → `IndexData.inaccessibleDirectories`.
- Uma pasta **nova** que falha (sem stamp anterior) não entra em `directories` (não há o que
  preservar) e também é registrada.
- Só uma observação válida (listagem que terminou) pode remover entradas. `DirectoriesChanged`
  (o `stat` por pasta ao abrir a aba) trata pasta inacessível como "não mudou", não como
  "mudou" — senão a aba pediria varredura a cada abertura.
- Teste: `TempTree` com uma subpasta que recebe uma regra de ACL negando listagem/atributos ao
  usuário atual (removida no `Dispose`); varredura completa → entradas e stamp iguais aos
  anteriores, pasta listada em `InaccessibleDirectories`, `Removed == 0`; regra removida →
  varredura seguinte volta ao normal.

## 13. Semântica de caminho vs. de objeto

Caso: `locator.lastKnownPath = D:\Music\foo.mp3`, `fileId = X`; o `foo.mp3` original é removido e
outro arquivo passa a ocupar `D:\Music\foo.mp3` com `fileId = Y`.

Fato decisivo, vindo do próprio projeto: no Windows o `fileId` muda em operações que **não**
trocam o recurso lógico — o TrackTracer grava capítulos por cópia + `File.Move(overwrite)`
(`Id3ChapterWriter.Write`), o yt-dlp entrega por rename, tag editors fazem "safe save". Uma
semântica de objeto estrita produziria "substituído, confirme" a cada gravação de tracklist.

| | Managed | ExternalLinked |
|---|---|---|
| **Recomendação** | **Semântica de caminho, pura.** O nó é um *slot* (`managedPath`) no storage da biblioteca; o que está lá é o nó. `fileId` não participa da identidade; a impressão digital serve só para `Stale` (reler tags). | **Caminho primeiro, objeto como fallback.** Enquanto `lastKnownPath` resolve, é o nó (Resolved); `fileId` diferente = `Replaced`, **informação** (tooltip/log), impressão digital atualizada em silêncio. `fileId` só decide quando o caminho falha (relocation, §9). |
| Trade-off aceito | Um arquivo trocado à mão no storage vira o nó sem aviso (o nome do nó pode ficar "errado" até o usuário renomear). É storage da biblioteca: quem mexeu sabia. | Se o usuário **mover** `foo.mp3` e **outro** `foo.mp3` aparecer no caminho antigo, o link segue o novo (e o movido fica sem link). Raro; o caso comum (tag editor/TrackTracer/re-download no mesmo caminho) fica silencioso e certo. |
| Alternativa recusada | Objeto: exigiria `fileId` estável — não existe no Windows para os fluxos deste app. | Objeto estrito por nó (`bindTo: object`) com confirmação em `Replaced`: possível como flag futura por nó, não no BLOCO 2. |
| Tipo errado no caminho | `TypeMismatch` → erro (A6/B4) — caminho não "ganha" de tipo | idem (C8/D7) |

Consequência para a matriz: `Replaced` é sempre válido; nunca pede confirmação; nunca dispara
`RequestManualRelocation`.

## 14. Quem participa do scanner e como

O que a varredura observa é o **ScanPlan** de §8, derivado do Manifest e do ambiente na hora
em que ela começa (e gravado no Index como `scanPlan`):

| Nó | Participa? | Como | Por quê |
|---|---|---|---|
| **Managed Folder** | sim | recursivo, como uma raiz de hoje, a partir de `managedRoot + managedPath` | é o conteúdo importado; descoberto, não listado |
| **Managed File** | sim | uma entrada só (`stat` + tags pela regra de identidade) | o modelo precisa de título/duração/capítulos, e a UI lista o nó como qualquer arquivo |
| **ExternalLinked Folder** | sim, **conteúdo inteiro** | recursivo a partir de `locator.lastKnownPath` | é a raiz de hoje: "Adicionar pasta" existe pra ver o que há dentro (1.441 arquivos do dono); linkage só da pasta, sem conteúdo, não teria uso na aba |
| **ExternalLinked File** | sim, **se não estiver coberto** | uma entrada só | metadados e cache de identidade (`fileId`) pra escada de §9 |
| **Nó promovido** (Managed ou ExternalLinked, A12/C15) | **não**, enquanto a pasta que o cobre existir | — | o recurso já está materializado pela pasta; promover não muda o ScanPlan |
| VirtualFolder, Alias | não | — | não têm caminho |
| Entradas descobertas | são o **produto** | — | — |

- **Fontes** = `folderSources` (o conjunto cobridor: pastas físicas não contidas em outra
  pasta física — uma pasta aninhada é andada pela externa, como hoje; pela boundary de §7 uma
  pasta Managed nunca está dentro de uma linked, então toda pasta Managed importada é fonte
  própria) + `fileSources` (arquivos físicos não contidos em nenhuma pasta-fonte). Nó linked em
  violação da boundary (D12) fica **fora** do plano. Contenção pelos primitivos de §16. Cobertura é recalculada a cada varredura a partir
  do conjunto de nós; um nó de arquivo que perde a pasta que o cobria passa a ser fonte sozinho
  (a remoção da pasta já mudou o plano).
- **Produção ≠ projeção**: a varredura registra em cada entrada `scanSourceNodeId` = a fonte
  que a listou (bookkeeping; pastas aninhadas/duplicadas → "quem andou"). Onde a entrada
  aparece é decidido pelo reconciliador, projetando-a sob **todo** nó físico cujo caminho
  resolvido a contém (C12, C13, D9, D10). Contagens e "Todas" deduplicam por caminho resolvido.
- **Extensões**: só filtram a descoberta dentro de pastas; nós de arquivo entram sempre.
- **Inbox**: "Importar arquivo" e "Adicionar arquivo" escrevem um bloco em `inbox.v2\` (como um
  download) pra entrada aparecer na varredura seguinte com prioridade — sem esperar relistar tudo.
  O 2.x nunca lê nem escreve `inbox\` (§15).
- **Fontes ausentes**: raiz `Unknown`/`Missing`/`Inaccessible` mantém entradas e stamps (hoje +
  §12). Fonte **removida do Manifest** (o plano mudou) tem as entradas soltas na varredura
  seguinte (C11 até lá).

## 15. Decisões fechadas, downgrade e o que resta

### Decisões fechadas nesta revisão (do dono)

1. **Posição lógica é projeção**, não campo do Index (§6, §14; C12, C13, D9, D10 revisados).
2. **Alias de entrada descoberta = promoção** a `PhysicalNode` explícito (`origin: promoted`,
   sem cópia; Managed se dentro de pasta Managed, com `managedPath` = o caminho existente);
   B8 vale só para importações; nó promovido coberto não é fonte nova (A12, C15, §14).
3. **`managedRoot` é imutável** depois do primeiro nó Managed; `RelocateManagedRoot` é operação
   futura (§7; A8 sem "Localizar…").
4. **Identidade de projeção/contagem = caminho normalizado resolvido**; `(volumeSerial, fileId)`
   só pra recuperação (§1, §9, §13).
5. `parent` só `VirtualFolder`; `DirectoriesChanged` com raiz `Unknown` não pede varredura
   (junto com B5, §12); remoção de nó Managed e apagar a cópia ficam fora (G10 já representa
   órfãos); ordem da importação aprovada (Apêndice A; queda entre rename e commit = G10,
   aceitável); invalidação de varredura só por mudança do **ScanPlan efetivo** (§8), nunca por
   "nó mudou"; URI, `audioHash` e mover origem continuam fora; nomes de UI não alteram o domínio.
6. **Revisão 4**: `Current` exige `fileId` compatível quando conhecido (§3, §6); alias é folha
   no modelo (§11, F8); `inbox.v2\` (abaixo); órfão do storage por cobertura (§7, G10);
   re-upgrade por snapshot `legacyV1` (abaixo); managed root efetiva detectada pelo plano (§7, §8).
7. **Revisão 5**: `Broken` sem estado pendente (§9); órfão por cobertura **hierárquica** (§7);
   migração read-only do `inbox\` v1 (abaixo); boundary Managed/ExternalLinked **disjunta** (§7,
   D12); canonicalização e contenção de caminhos como invariante do domínio (§16).

### Migração e downgrade

Risco real: o `LibraryManifest.Load` do 1.x desserializa `ManifestData` ignorando campos
desconhecidos e **não confere `schemaVersion`** — um `manifest.json` v2 seria lido como
"zero raízes" e reescrito no formato v1 na primeira `Update`, destruindo `nodes`. O Index v2 seria
lido (campos extras ignorados) e regravado como v1 — inofensivo, é descartável.

Decisão: **armazenamento versionado por nome de arquivo**, que torna o downgrade estruturalmente
seguro sem depender de código no 1.x (que já está publicado):

| Arquivo | Quem lê/escreve | Papel |
|---|---|---|
| `manifest.json` | só o 1.x | o estado v1; o 2.x lê uma vez pra migrar e **nunca escreve** |
| `manifest.v2.json` (+ `.bak`, `.bad`) | só o 2.x | o estado v2, autoritativo |
| `index.json` | só o 1.x | descartável |
| `index.v2.json` (+ `.bad`) | só o 2.x | descartável |
| `inbox\` | o 1.x escreve e apaga; o 2.x **só lê** (migração abaixo) | o 2.x nunca escreve nem apaga aqui — o 1.x publicado tem `autoAddRoots`, e um bloco do 2.x com `managed\foo.mp3` (cópia recém-importada) que sobrasse aqui num downgrade faria o 1.x colocar `managed\` no catálogo v1 |
| `inbox.v2\` | só o 2.x | o inbox do 2.x (downloads, importações, adições); o 1.x não conhece a pasta e não tem como processá-la — isolamento estrutural, não um campo que o 1.x ignoraria |
| `managed\` | só o 2.x | o 1.x não sabe que existe e nunca a toca |

- **Upgrade** (2.x encontra só `manifest.json`): migra para `manifest.v2.json` (§5); `manifest.json`
  fica intacto — ele **é** o backup v1, não precisa de `manifest.v1.bak`.
- **Downgrade** (1.x roda de novo): usa `manifest.json`/`index.json` como sempre; ignora os
  `.v2.json` e `managed\`. Nada do v2 é perdido. O que o 1.x não mostra: nós virtuais, aliases,
  arquivos adicionados/importados (não estão em `roots`).
- **Re-upgrade** — o modelo corrigido, com histórico. "v1 atual − v2 atual" está errado: uma raiz
  migrada e depois **removida no 2.x** continua no `manifest.json` e ressuscitaria a cada start.
  Por isso o v2 guarda `legacyV1 = { observedAt, fingerprint, observedRoots[] }`, o snapshot das
  raízes v1 da última vez que o 2.x olhou pra elas:

  ```
  no primeiro upgrade:      legacyV1.observedRoots = roots(v1); fingerprint = hash(roots(v1))
  em todo start seguinte:   ler manifest.json (se ilegível/ausente: não fazer nada)
                            se hash(roots(v1)) == legacyV1.fingerprint → nada
                            senão: added = roots(v1) − legacyV1.observedRoots        (por caminho normalizado)
                                   para cada added sem PhysicalNode(Folder, ExternalLinked) de mesmo caminho
                                       → criar (origin: migrated)
                                   removals (observedRoots − roots(v1)) → NÃO propagadas (decisão)
                                   legacyV1 ← snapshot novo; revision++
  ```

  Assim "já existia no snapshot migrado" e "foi acrescentada durante um downgrade" ficam
  distinguíveis; opções do v1 (player, extensões) nunca são propagadas — o v2 é autoritativo.
- **Pendências do `inbox\` v1 — migração read-only.** Sem isso, um bloco que o 1.x escreveu e
  não chegou a consumir (download entregue fora das raízes, processo fechado antes do
  `autoAddRoots`/varredura) sumiria no upgrade. Identidade estável de um bloco = o **nome do
  arquivo** (`{createdAt}-{seq}-{guid}.json`, escrito uma vez por temp+rename, nunca alterado).
  A cada start do 2.x:

  ```
  para cada inbox\*.json (ordem de nome), com nome ∉ legacyV1.observedInbox:
      ler o bloco (mesmo JSON: { createdAt, files[] }; ilegível → registrar o nome e seguir)
      para cada caminho em files:
          sob alguma fonte do ScanPlan          → bloco em inbox.v2\ (mesmo createdAt)
          fora de toda fonte, autoAddRoots=on   → PhysicalNode(Folder, ExternalLinked) da pasta
                                                   (origin: autoAdd; recusado se violar a boundary → só log)
                                                   + bloco em inbox.v2\
          fora de toda fonte, autoAddRoots=off  → só log (como o 1.x faria)
      legacyV1.observedInbox += nome
  legacyV1.observedInbox ∩= nomes que ainda existem em inbox\   (o 1.x apaga os que consumiu; a lista não cresce pra sempre)
  se algo mudou: revision++; MarkPending
  ```

  Nunca escrever, renomear ou apagar em `inbox\`. Downgrade: o 1.x consome os seus blocos como
  sempre (os dois lados convergem na mesma pasta). Re-upgrade: blocos criados durante o período
  em 1.x têm nomes novos → traduzidos uma vez. Bloco cujo arquivo já não existe é inofensivo (o
  inbox é otimização: "dobrar um bloco duas vezes é inofensivo", e a varredura decide).
- Um 3.x futuro repete o padrão (`manifest.v3.json`); dentro de um mesmo arquivo, mudanças
  compatíveis usam `schemaVersion` + round-trip, e `schemaVersion` maior que o suportado é G3
  (só leitura).

Ou seja: a migração 2.0 é **reversível** por construção, e isso fica documentado aqui e no
CHANGELOG do plugin quando o BLOCO 2 sair.

### Contradições arquiteturais restantes

Nenhuma que impeça a implementação. Notas que são escolhas de UI, de bookkeeping ou custo, não
de domínio:

1. **Projeção sob pastas aninhadas** (D9): o modelo projeta a entrada sob a pasta externa e sob
   a interna; a UI decide se, debaixo da externa, mostra a subárvore inteira ou um atalho pro nó
   interno. Não muda contagem (caminho resolvido).
2. **`scanSourceNodeId` em pastas aninhadas/duplicadas** é "quem andou", não "a mais funda" —
   bookkeeping; nada de posicionamento depende dele.
3. **Custo de mover o app com conteúdo Managed** (§7): uma varredura completa com releitura das
   tags dos arquivos managed — aceito; otimização por `fileId` fica pra depois.
4. **Boundary e raízes v1 grandes**: quem migrar com uma raiz v1 que contém a pasta do app (por
   exemplo o perfil do usuário inteiro) vê D12 — o nó continua listado, mas fora do ScanPlan e
   com Importar bloqueado até trocar a raiz por uma subpasta ou mover a pasta da biblioteca
   (possível enquanto não há nó Managed). É a única fricção que a boundary disjunta cria; a
   alternativa (overlap modelado) custaria ownership, projeção, dedupe, downgrade e ciclo de vida
   sobrepostos, e foi recusada.

## 16. Caminhos: canonicalização e contenção (invariante do domínio)

"Está dentro de" sustenta cobertura do ScanPlan, projeção, dedupe, ownership Managed, órfãos,
boundary e a fonte de cada entrada — então é primitivo do domínio, com uma implementação só
(`PathUtil` cresce; o `Normalize`/`IsUnder`/`Relative` de hoje já fazem prefixo + separador, e
passam a ser estes):

| Primitivo | Contrato |
|---|---|
| `NormalizeAbsolutePath(p)` | `Path.GetFullPath` (resolve `.`/`..`, `/`→`\`), remove `\\?\`, mantém UNC `\\servidor\share\…`, tira separador final (exceto raiz de volume `C:\`), **forma canônica para identidade = minúsculas invariantes** (NTFS é case-insensitive por padrão; case-sensitivity por diretório não é suportada). Rejeita: caminho relativo, device path (`\\.\`), caracteres inválidos. |
| `NormalizeManagedRelativePath(rel)` | Só relativo: não `IsPathRooted`, não começa com `\`/`/`, sem `:` (drive/ADS), sem UNC; segmentos separados por `\`/`/`, nenhum vazio, `.` ou `..`, sem caracteres inválidos, sem ponto/espaço final (o Windows os descarta — canonicalização diferente = identidade diferente); e `NormalizeAbsolutePath(Combine(root, rel))` tem que ser descendente **próprio** da root (nunca a própria root). Inválido → o nó vai pra Problems como `StructuralConflict` e **nunca** é sondado, copiado, varrido ou apagado. |
| `IsSamePath(a, b)` | igualdade ordinal das formas canônicas |
| `IsDescendantOrSame(child, parent)` | `IsSamePath` **ou** `canônico(child)` começa com `canônico(parent) + "\"` (a raiz de volume já termina em `\`). É o separador que impede `C:\Music` de conter `C:\Music2`. Nunca `StartsWith` textual solto. |
| `IsProperDescendant(child, parent)` | descendente e não o mesmo |

Consumidores obrigatórios do mesmo canonicalizador: caminho resolvido de cada nó (identidade de
projeção/contagem, §1), fingerprint do ScanPlan (§8), projeção (§14), dedupe, cobertura de órfãos
(§7), boundary (D12), "andado uma vez" do scanner, `scanSourceNodeId`. Suposições declaradas:
nomes curtos 8.3 não são expandidos (caminhos vindos de `GetFinalPathNameByHandle` e dos diálogos
já vêm longos); junctions/symlinks não são resolvidos — dois caminhos pra mesma pasta via
junction são duas identidades (e o scanner pula reparse points).

### Apêndice A — a cópia da importação (item 26 do pedido)

```
origem  →  copiar para  managedRoot\.~import-<guid>\<layout final>
        →  validar (tamanhos batem; toda exceção de cópia aborta e apaga o temporário)
        →  renomear o temporário pro caminho final (mesmo volume: atômico; colisão → sufixo)
        →  commit no Manifest (nó novo; revision++) + bloco em inbox.v2\
        →  o plano mudou (fonte nova) → varredura (§8)
origem permanece intacta em todos os passos; nenhum passo apaga nada fora de managedRoot\.~import-*
```

Falha na cópia → sem nó, temporário apagado. Falha no commit → o caminho final fica sob a root
sem nenhum nó Managed que o cubra → é um **Managed Storage Orphan** pela definição de §7
(**Adotar** cria o nó com `origin: adopted`; **Apagar** remove a cópia) — nunca apagado sozinho.
Sem engine de transação: ordem segura e limpeza de `.~import-*` no start bastam.
