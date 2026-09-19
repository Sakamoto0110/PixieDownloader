# Changelog

Versões seguem o `<Version>` do `.csproj` e as tags `vX.Y.Z`; só o que foi publicado entra aqui.
O bloco da versão é copiado para o corpo da GitHub Release pelo `scripts/release.ps1`.

## Plugins

Os plugins oficiais têm versão e release próprias, separadas das do app: a tag `<id>-vX.Y.Z` publica o zip e atualiza o catálogo na release [`plugins`](https://github.com/Sakamoto0110/PixieDownloader/releases/tag/plugins) do GitHub, de onde a aba Plugins instala. O histórico deles fica aqui, fora das versões do app.

- **Library 1.1.0** — 2026-09-16 — **pastas que abrem e fecham**: a lista da aba Biblioteca vem agrupada por pasta, cada pasta um cabeçalho com a contagem (fechadas de início — a lista de pastas é a visão geral, um clique abre uma; buscar abre todas as que têm resultado e limpar a busca devolve cada uma ao que estava). A ordem escolhida (recentes, título…) vale dentro de cada pasta; o toggle **Pastas** ao lado da ordem desliga o agrupamento e volta à lista única (fica salvo). **VLC em Opções**: ao lado de "Escolher programa…", **Usar VLC** passa a abrir os arquivos com o VLC que a máquina já tem — o instalado no Windows, se houver (procurado ao abrir a aba, nada fica salvo), senão o portátil em `tools\vlc` — e o **cone do VLC** baixa o VLC portátil oficial (o zip da VideoLAN, 80 MB; sem instalador, nada muda no Windows) pra `tools\vlc` ao lado do app — SHA-256 conferido contra o publicado, só o necessário extraído (~140 MB: sem os plugins de browser, sem o MSI, só a tradução do idioma do Windows) — e passa a usá-lo. Se já existe um VLC (instalado ou o portátil), o cone pergunta antes: usar esse, baixar mesmo assim, ou cancelar. Não existe VLC de um `.exe` só (o `vlc.exe` precisa do libvlc e da pasta `plugins\`), por isso é o zip.
- **TrackTracer 1.1.0** — 2026-09-15 — **aninhamento**: uma faixa da lista que tem link ganha o botão **Expandir**; o link é analisado como um vídeo (descrição, comentários, capítulos — o mesmo detector) e a tracklist dele aparece aninhada, recuada, debaixo da faixa — um mix dentro do mix, e assim por diante até o limite de profundidade (seletor na aba, padrão 3, fica salvo em `data\tracktracer\settings.json`). Link pra playlist vira um item por vídeo, cada um expansível. **Expandir tudo (N)** percorre os links um por vez (2–4 s cada) até o limite, com **Parar**; um vídeo que já está na árvore acima dele é marcado como ciclo e não é buscado; link que falhou (rate limit, HTTP) mostra o motivo e tem **Tentar de novo**; link sem tracklist diz "nada encontrado". Recolher/mostrar por nó. O que foi expandido vai inteiro pro `TXXX:PIXIE_TRACKLIST` do MP3 (mesmo formato, `payloadVersion` 1) — os capítulos `CHAP` continuam sendo só as faixas deste arquivo. Trocar de fonte não perde o que já foi expandido na outra.
- **Library 1.0.0** — 2026-09-15 — módulo 2 do roadmap: a aba **Biblioteca** cataloga o que já está em disco. Pastas nomeadas apontam pra pastas reais (Adicionar pasta, renomear, remover — nenhum arquivo é tocado) e tudo abaixo delas entra num índice com título, artista, álbum, duração, bitrate e o número de capítulos ID3 — um mix com a tracklist gravada mostra "24 faixas". Busca sem acento por título/artista/álbum/arquivo/pasta, filtro por tipo (áudio, vídeo, outros), ordem (recentes, título, artista, duração, tamanho, pasta), rodapé com contagem, duração e tamanho do que está na tela. Duplo clique ou **Abrir** abre no programa que você escolher em Opções (VLC é a recomendação) ou no padrão do Windows; **Pasta** mostra o arquivo no Explorer. Só áudio e vídeo entram por padrão; "Outras extensões" em Opções acrescenta o que quiser (gif, por exemplo) e a extensão aparece em cada linha. **O que o app baixa entra sozinho**: ao terminar um download, a pasta de saída vira uma pasta do catálogo se nenhuma a contém (dá pra desligar em Opções) e o arquivo aparece na hora. Abrir a aba confere as pastas e sincroniza só o que mudou — as tags são lidas uma vez e relidas só quando o arquivo muda; **Sincronizar** faz a varredura completa. Disco desligado não apaga nada: a pasta fica marcada como não encontrada e o que já estava no catálogo continua listado. Tudo em `data\library\` (`manifest.json` legível, `index.json`).
- **TrackTracer 1.0.0** — 2026-09-15 — a primeira release própria; o mesmo plugin que saiu junto com o app 1.7.0.

## [1.7.1] — 2026-09-15

### Alterado
- **Plugins têm release própria.** A aba Plugins passa a ler o catálogo da release `plugins` do GitHub (`releases/download/plugins/plugins.json`), que cada tag `<id>-vX.Y.Z` atualiza; a release do app deixa de levar zip de plugin e `plugins.json`. Um plugin novo ou atualizado não muda mais a versão do app. (O 1.7.0 lia o catálogo da última release do app e, a partir desta, não acha mais nada lá — atualize.)
- A linha de status da loja diz de quando é o catálogo em vez de qual release do app o trouxe.

## [1.7.0] — 2026-09-15

### Adicionado
- **Plugins oficiais** na aba Plugins: a lista dos plugins da última release, com descrição, e um botão **Instalar** (ou **Atualizar**, quando o instalado é mais velho). O app baixa o zip da release, confere o SHA256 e verifica que é um plugin que esta versão carrega antes de colocá-lo em `plugins\`. Se a versão atual estiver em uso, a nova fica guardada e entra no próximo start — a linha avisa. Sem internet, a seção só diz que o catálogo não está disponível. A release passa a trazer o `plugins.json` que a aba lê; `plugins.catalogUrl` no `settings.json` aponta pra outro catálogo com a mesma forma.

### Alterado
- O plugin **Tracklist virou TrackTracer** (`PixieDownloader-plugin-tracktracer-vX.Y.Z.zip`, pasta `plugins\tracktracer\`): "rastreia as faixas" de um mix — o nome antigo era o do resultado, não do plugin. A aba continua "Tracklist". Quem tem `plugins\tracklist\` da 1.6 apaga a pasta e instala o novo (pela aba, agora).
- A tracklist vai pro MP3 por uma cópia trocada no lugar do arquivo, nunca gravando por cima: fechar o app no segundo seguinte a um download não deixa mais um MP3 truncado — no pior caso, fica sem os capítulos.
- Documentação do SDK: a regra de versão diz o que pode entrar num minor de cada lado (o que o host implementa cresce; o que o plugin implementa só ganha interface opcional nova) e que o `apiVersion` cobre o `YtDlpCore` junto.

### Corrigido
- Plugin cujo `Configure` lançava depois de registrar uma capacidade deixava a capacidade viva e o próprio **Habilitar** falhava por colidir com ela — agora um `Configure` que lança não deixa nada pra trás e a nova tentativa começa do zero. A mensagem de "Falhou ao carregar" passa a ser a causa de verdade, não o "Exception has been thrown by the target of an invocation" genérico.
- Um plugin que registrasse uma capacidade de outra thread no instante do Desabilitar podia deixá-la registrada depois de desligado.
- Um callback de `ShutdownToken` que lançasse derrubava o app ao Desabilitar ou ao fechar; vai pro log.
- Handler de evento de plugin que segura a UI por mais de 250 ms é apontado nos logs.

## [1.6.1] — 2026-09-14

### Alterado
- A aba **Debug / Tests** fica escondida por padrão. A bolinha vermelha "debug" na barra de status (à esquerda de "yt-dlp") a libera: clique e segure por 5 segundos — ela fica verde e a aba aparece; um clique nela de novo esconde. Fica salvo.
- Ordem das abas: Baixar, Fila, Plugins, as abas dos plugins na ordem da pasta, Debug (quando liberada) e Logs sempre por último. Plugin desabilitado e reabilitado volta pra posição original entre os outros, não pro fim.
- O SDK passa a ser publicado no nuget.org a cada release (`PixieDownloader.Sdk`).

## [1.6.0] — 2026-09-14

### Adicionado
- **Plugin Tracklist** (`PixieDownloader-plugin-tracklist-v1.0.0.zip`, extraia em `plugins\`): ao analisar um vídeo, procura a tracklist na descrição, no comentário fixado, no comentário do canal e nos mais curtidos (os capítulos do YouTube ficam de reserva) e mostra tudo numa aba **Tracklist** — cada fonte com a contagem de faixas, trocar de fonte troca a lista inteira, faixa sem título aparece como tal, e um expander com o relatório cru da detecção. Com "Gravar no MP3 ao baixar" marcado, o download leva a lista pro arquivo como capítulos ID3 (`CHAP`/`CTOC`, que VLC, foobar2000 e mpv mostram) e a árvore completa em `TXXX:PIXIE_TRACKLIST`. Só MP3 por enquanto.
- **API 1.1 do SDK** (`PixieDownloader.Sdk.1.1.0.nupkg`): `AnalysisCompleted`, `DownloadCompleted` e `RequireAnalysisComments()` no `IPluginHost`. Plugin feito na 1.0 continua carregando.
- **`plugin.json` virou opcional**: sem ele, o app lê id (a pasta), nome, versão, API e a classe de entrada dos metadados do próprio `.dll`, sem carregar código. O arquivo continua valendo e é obrigatório só quando a convenção não basta (mais de uma classe de entrada, `dependsOn`, id diferente da pasta).
- **Plugin sem dependências é um `.dll` solto** em `plugins\`; só quem traz dependências próprias (o Tracklist, com o TagLib#) ganha pasta — assim a dependência de um nunca se mistura com a de outro. Um `.dll` solto que não é plugin (dependência perdida) é recusado com a dica de usar pasta.
- **Recarregar** na aba Plugins: olha a pasta de novo sem reiniciar — plugin recém-colocado entra e carrega, recusado com o problema corrigido também.

### Corrigido
- O caminho do arquivo entregue por um download simples (sem passo de ffmpeg) apontava pra pasta de staging, já apagada; agora é o caminho final.
- Nomes de arquivo com `｜` ou `⧸` (o que o yt-dlp usa no lugar de `|` e `/`) apareciam mutilados nos logs.

### Alterado
- A análise de vídeo único só busca comentários (+1–2 s) quando um plugin pede — sem o plugin Tracklist, nada muda.
- Painel de metadados sem o texto explicativo debaixo de "Incluir metadados".

## [1.5.0] — 2026-09-14

### Adicionado
- **Plugins**: o app passa a carregar extensões de `plugins\<id>\` (ao lado do `.exe`), cada uma com um `plugin.json` e o próprio assembly, num contexto de carga isolado. Um plugin pode contribuir uma aba, usar o serviço de download do app, guardar arquivos em `data\<id>\` e publicar/consumir capacidades por id — sem conhecer outro plugin. Plugin com `apiVersion` incompatível, manifesto inválido ou dependência ausente é recusado com o motivo nos logs, sem carregar código.
- **Aba Plugins**: lista o que há em `plugins\` com nome, versão, API, status e motivo; Habilitar / Desabilitar (efeito imediato) / Desinstalar (a pasta some no próximo start; dá pra desfazer até lá) / abrir pasta.
- **`PixieDownloader.Sdk` como pacote**: o contrato de plugin (`IPixiePlugin`, `IPluginHost`, `IUiContribution`, `PluginManifest`) sai na release como `PixieDownloader.Sdk.1.0.0.nupkg`, com o `YtDlpCore` dentro e docs XML. Tem versão própria (o `apiVersion`, hoje 1.0) — não é para o usuário final. Consumo: `dotnet nuget add source <pasta>` + `PackageReference` com `ExcludeAssets="runtime"`; README no pacote.
- `docs/ROADMAP.md`: a arquitetura de plugins e o plano 1.5 → 1.8.

### Alterado
- Além de `tools\`, `cache\` e `logs\`, o app cria `plugins\` e `data\` ao lado do `.exe`.
- Nenhum plugin vai no pacote: o app sai igual ao 1.4.1 até você instalar um.

## [1.4.1] — 2026-09-14

### Corrigido
- O app inteiro renderizava na Segoe UI clássica: "Segoe UI Variable" não é um nome de família que o WPF conheça (só "… Text", "… Display" e "… Small"), então caía no fallback. Agora usa a Segoe UI Variable Text do Windows 11, como o resto do sistema; no Windows 10 continua na Segoe UI.
- Nome do app na barra de título menor (13px), na escala do selo de versão e do ícone do GitHub em vez de competir com os títulos de seção.
- "Colar links": o texto começa no canto superior esquerdo da caixa em vez do meio (o mesmo valia pra caixa de argumentos da aba Debug), e a janela ganhou um botão de fechar — equivale a Cancelar.

## [1.4.0] — 2026-09-13

### Adicionado
- **Fila de downloads**: cada item vira um job com status e progresso próprios; vídeo que passa pelo ffmpeg aparece como *download → processamento* (linhas vinculadas, com % no re-encode). N em paralelo (slider "Downloads paralelos"), reordenar arrastando, cancelar um/todos, limpar concluídos, "Tentar de novo" para o que falhou (ex.: HTTP 403 do YouTube).
- **Pendências persistentes** (`pending-downloads.txt`): o que ficou na fila volta na próxima abertura com "Continuar". Se o app fechou com o download pronto e o ffmpeg no meio, retoma só o processamento, sem baixar de novo.
- **Metadados por campo** na pré-visualização (título, artista, data, descrição, link, gênero…) com checkbox e expander; desmarcou, a tag sai vazia. Escolha persistida.
- **Importar ▾**: além do `.txt`, "Colar links" numa janela, com numeração automática opcional (`1 - `, `2 - `…).
- Rodapé fixo com *Ver fila / Cancelar / Baixar* (não some ao rolar); aba **Fila (N)**; progresso da fila na barra de tarefas do Windows.
- Opções de vídeo (GIF, áudio, velocidade, recorte) reunidas no expander "Opções avançadas de vídeo".
- Selo de versão e ícone do GitHub no header; o `.exe` passa a carregar a versão de verdade.
- Primeira abertura baixa yt-dlp e ffmpeg sozinha quando faltam.
- Infra de release: `scripts/release.ps1` + workflow por tag `vX.Y.Z` gera os dois `.zip` e o `SHA256SUMS.txt`.

### Alterado
- Botão "Analisar" removido — a análise dispara ao colar a URL (Enter re-analisa).
- "Cancelar" só habilita com download/processamento em andamento e cancela o primeiro item ativo da fila.
- Cada download usa sua própria pasta em `.~downloads/`, apagada ao cancelar ou fechar; sobras de uma sessão interrompida são avisadas e limpas na abertura.
- Item de playlist baixa pela URL direta do vídeo, com os tokens de playlist do template já resolvidos.
- Logs: buffer circular de 1000 entradas, entregues em lote.
- A janela pode ser reduzida abaixo de 940×560: o conteúdo escala em vez de quebrar.
- Os pacotes de release não trazem mais yt-dlp/ffmpeg (o app baixa na primeira abertura).

### Corrigido
- O tamanho da janela salvo nunca era restaurado.

## [1.3] — 2026-07-27
- Publish single-file: `.exe` framework-dependent (~1 MB) e self-contained/portable (~62 MB).

## [1.0] — 2026-07-05
- Primeira release: MP3/MP4 via yt-dlp, playlists com seleção, importar `.txt`, velocidade do vídeo, recorte por tempo e extração de GIF, settings com auto-save, abas de logs e de debug.
