# PixieDownloader

App desktop **WPF / .NET 10** (Windows) — uma interface gráfica para o [`yt-dlp`](https://github.com/yt-dlp/yt-dlp) baixar **áudio (MP3)** ou **vídeo (MP4)** do YouTube e afins. Tema *dark purple*, janela custom (sem a barra de título padrão do Windows), e foco em ser um binário enxuto.

> ⚠️ Ferramenta de uso pessoal/educacional. Respeite os termos de uso das plataformas e os direitos autorais do conteúdo que você baixar.

---

## Recursos

- 🎵 **Áudio (MP3)** com bitrate ajustável (128/192/320k), thumbnail e metadados embutidos.
- 🎬 **Vídeo (MP4)** com opções de manter/remover áudio, extrair um MP3 separado, e **mudar a velocidade** (0.1×–8×, slowmo ou acelerado).
- 📋 **Playlists**: analisa a playlist e mostra os itens em uma lista com checkboxes (thumbnails + preview) pra você escolher o que baixar.
- 📄 **Importar `.txt`**: uma URL por linha vira uma lista revisável; a 1ª linha (`# Download as mp3|mp4`) define o modo, e o nome do arquivo vira a subpasta de saída.
- 🔎 **Seleção por posição** na busca: `%[1:20]` seleciona os itens 1 a 20, `%[5:]`, `%[:10]`, `%[7]`.
- 🗂️ **Organização da saída** por templates (sem subpastas, por playlist, por canal, prefixo por data) ou template customizado montado por "tokens".
- ⚙️ Resolve e instala `yt-dlp`/`ffmpeg` automaticamente (de `./tools/` ou do PATH), com checagem de atualização do yt-dlp.
- 🪵 Aba de logs, aba de debug (rodar comandos crus do yt-dlp) e settings persistidas com auto-save.

---

## Como rodar

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

## Problemas conhecidos (em análise)

- **Modo vídeo + falha no passo de ffmpeg:** se a mudança de velocidade (ffmpeg) falhar e o arquivo baixado não estiver em `.mp4` (ex.: `.webm`/`.mkv`), ele pode não ser entregue na pasta de saída.
- **Cancelar importação:** ainda não dá pra cancelar a análise de uma lista `.txt` enquanto as URLs estão sendo processadas (o botão de cancelar só atua durante o download).

## Licença

[MIT](LICENSE).
