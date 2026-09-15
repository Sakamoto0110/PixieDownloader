# Changelog

Versões seguem o `<Version>` do `.csproj` e as tags `vX.Y.Z`; só o que foi publicado entra aqui.
O bloco da versão é copiado para o corpo da GitHub Release pelo `scripts/release.ps1`.

## [1.6.1] — 2026-09-14

### Alterado
- A aba **Debug / Tests** fica escondida por padrão. Uma bolinha vermelha na barra de status (à esquerda de "yt-dlp") a libera: clique e segure por 5 segundos — ela fica verde e a aba aparece; um clique nela de novo esconde. Fica salvo.
- Ordem das abas: Baixar, Fila, Plugins, as abas dos plugins na ordem da pasta, Debug (quando liberada) e Logs sempre por último. Plugin desabilitado e reabilitado volta pra posição original entre os outros, não pro fim.

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
