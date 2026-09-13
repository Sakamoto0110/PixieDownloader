# PixieDownloader

App desktop **WPF / .NET 10** (Windows) — uma interface gráfica para o [`yt-dlp`](https://github.com/yt-dlp/yt-dlp) baixar **áudio (MP3)** ou **vídeo (MP4)** do YouTube e afins. Tema *dark purple*, janela custom (sem a barra de título padrão do Windows), e foco em ser um binário enxuto.

> ⚠️ Ferramenta de uso pessoal/educacional. Respeite os termos de uso das plataformas e os direitos autorais do conteúdo que você baixar.

---

## Recursos

- 🎵 **Áudio (MP3)** com bitrate ajustável (128/192/320k), thumbnail e metadados embutidos.
- 🎬 **Vídeo (MP4)** com opções de manter/remover áudio, extrair um MP3 separado, **mudar a velocidade** (0.1×–8×, slowmo ou acelerado) e **recortar por janela de tempo** (início/fim precisos).
- 🖼️ **Extrair GIF** de um trecho do vídeo, com controle fino de início/duração.
- 📋 **Playlists**: analisa a playlist e mostra os itens em uma lista com checkboxes (thumbnails + preview) pra você escolher o que baixar.
- 📄 **Importar**: um `.txt` (uma URL por linha; a 1ª linha `# Download as mp3|mp4` define o modo e o nome do arquivo vira a subpasta de saída) ou **colar links** numa janela, com numeração automática opcional (`1 - `, `2 - `…) — os dois viram uma lista revisável.
- 🧾 **Fila de downloads**: cada item é um job com status e % próprios (download → processamento ffmpeg vinculados quando há re-encode), roda N em paralelo, dá pra reordenar arrastando, cancelar um/todos e limpar os concluídos. O que ficou pendente é salvo em `pending-downloads.txt` e retomado na próxima abertura — e se o app fechou com o download pronto e só o ffmpeg no meio, retoma direto do processamento, sem baixar de novo. Item que falhou (ex.: HTTP 403 do YouTube) tem "Tentar de novo".
- 🏷️ **Metadados por campo**: a pré-visualização lista o que o yt-dlp vai embutir (título, artista, data, descrição, link…) com checkbox por campo — desmarcou, a tag sai vazia. A escolha fica salva.
- 🧹 **Cancelar e fechar limpam tudo**: cada download tem sua pasta temporária em `.~downloads/`, apagada ao cancelar/fechar; se a sessão anterior morreu no meio, o app avisa e limpa.
- 🔎 **Seleção por posição** na busca: `%[1:20]` seleciona os itens 1 a 20, `%[5:]`, `%[:10]`, `%[7]`.
- 🗂️ **Organização da saída** por templates (sem subpastas, por playlist, por canal, prefixo por data) ou template customizado montado por "tokens".
- ⚙️ Resolve `yt-dlp`/`ffmpeg` de `./tools/` ou do PATH; o que faltar é baixado sozinho na primeira abertura, com checagem de atualização do yt-dlp depois.
- 🪵 Aba de logs (ring buffer de 1000, entregue em lote), aba de debug (rodar comandos crus do yt-dlp) e settings persistidas com auto-save.
- 🔍 A janela pode ser reduzida abaixo do tamanho de projeto — o conteúdo escala uniformemente em vez de quebrar.

---

## Baixar

Na página de [Releases](https://github.com/Sakamoto0110/PixieDownloader/releases), cada versão traz dois `.zip` — extraia e abra `PixieDownloader.exe`:

| Arquivo | Precisa de |
|---|---|
| `PixieDownloader-vX.Y.Z-win-x64.zip` | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) instalado |
| `PixieDownloader-vX.Y.Z-win-x64-portable.zip` | nada — o .NET vai dentro do `.exe` |

Nenhum dos dois traz `yt-dlp`/`ffmpeg`: na primeira abertura o app baixa os dois sozinho (~100 MB) para `tools\` e depois mantém o yt-dlp atualizado. `SHA256SUMS.txt` na release confere os pacotes.

## Como rodar (dev)

Pré-requisitos: **.NET 10 SDK** (Windows). `yt-dlp` e `ffmpeg` podem ser instalados pelo próprio app na primeira execução, ou colocados em `./tools/`.

```bash
# build
dotnet build PixieDownloader.slnx -c Release

# rodar
dotnet run --project src/PixieDownloader -c Release
```

### Estrutura

| Projeto | O quê |
|---|---|
| `src/YtDlpCore` (`net10.0`) | Biblioteca core, **sem dependência de WPF**: serviço do yt-dlp, parser de saída, settings, logger, cache de thumbnails. |
| `src/PixieDownloader` (`net10.0-windows`) | App WPF: Views, ViewModels, tema. |
| `tests/YtDlpCore.Tests` | Testes do parser. |
| `src/SmokeTest` | Harness de fumaça (offline + online opcional) que valida o pipeline de velocidade/ffmpeg de ponta a ponta. |
| `scripts/release.ps1` + `.github/workflows/release.yml` | Monta os dois `.zip` da release (local ou ao fazer push de um tag `vX.Y.Z`). |

---

## Sobre o desenvolvimento com IA

Esse projeto foi construído em par com o **Claude (Anthropic)** via [Claude Code](https://claude.com/claude-code). Não foi um "gerei tudo de um prompt e colei" — meu papel aqui foi de **arquiteto e orquestrador**: defini a direção, tomei as decisões de design, revisei cada mudança e mandei refazer o que não estava bom. A IA foi a ferramenta que acelerou a implementação; as decisões foram minhas.

Alguns exemplos de direção que dei e que moldaram o código (estão registradas no [`CLAUDE.md`](CLAUDE.md) e no histórico de commits):

- **Cortar peso desnecessário.** Removi o `Microsoft.Extensions.Hosting`/DI e o `CommunityToolkit.Mvvm` de propósito — deixavam dezenas de DLLs inúteis na saída. A composição é manual no `App.xaml.cs` e o MVVM é escrito à mão (`ObservableObject`/`RelayCommand`), pra saída ter só 2 DLLs.
- **Resolver problemas reais, não o caminho fácil.** A mudança de velocidade do vídeo não funcionava via `--recode` do yt-dlp (ele faz no-op num arquivo já em mp4). Direcionei a solução: baixar numa pasta de trabalho privada, localizar o arquivo no disco (robusto a títulos com emoji/Unicode) e aplicar `setpts`/`atempo` num passo de ffmpeg próprio.
- **Detalhes de produto.** Staging oculto pros arquivos intermediários, auto-save de settings com debounce, seleção de playlist por range na busca, importar listas como algo revisável em vez de baixar direto.
- **Verificação.** Pedi um harness de smoke-test que valida o pipeline de fato (gera um clipe, roda o passo real de ffmpeg, confere a duração com ffprobe), além de testes unitários do parser.

Em resumo: a IA escreveu muito do código, mas quem decidiu *o que* construir, *como* estruturar e *o que rejeitar* fui eu.

---

## Licença

[MIT](LICENSE).
