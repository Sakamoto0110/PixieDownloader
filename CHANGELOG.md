# Changelog

Versões seguem o `<Version>` do `.csproj` e as tags `vX.Y.Z`; só o que foi publicado entra aqui.
O bloco da versão é copiado para o corpo da GitHub Release pelo `scripts/release.ps1`.

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
