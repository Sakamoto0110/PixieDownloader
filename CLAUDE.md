# PixieDownloader

App desktop **WPF / .NET 10** (Windows) — GUI para o `yt-dlp` baixar áudio (MP3) ou vídeo (MP4). Tema dark purple, janela custom (WindowChrome).

## Comandos
- Build: `dotnet build PixieDownloader.slnx -v q --nologo`
- Rodar o app: `dotnet run --project src/PixieDownloader -c Release`
- Testes unitários (`tests/YtDlpCore.Tests`, xunit — só cobre o `YtDlpOutputParser`): `dotnet test tests/YtDlpCore.Tests`
  - Um teste só: `dotnet test tests/YtDlpCore.Tests --filter "FullyQualifiedName~TryParseProgress_parses_percent_speed_eta"`
- Smoke test de ponta a ponta (offline + online opcional; fecha a instância em execução, builda, faz boot test e roda o harness): `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/smoke.ps1`
  - `src/SmokeTest` **não está na `.slnx`** e não é referenciado pelo app — existe só pra exercitar `YtDlpService.BuildSpeedPassArgs`/o pipeline de vídeo via `InternalsVisibleTo`. Harness B (online) só roda se `test.txt` (raiz do repo, git-ignored) tiver uma URL.

## Estrutura
- `src/YtDlpCore/` — biblioteca core (`net10.0`, **sem WPF**): serviço do yt-dlp, parser, settings, logger, cache de thumbnail.
- `src/PixieDownloader/` — app WPF (`net10.0-windows`): Views, ViewModels, tema.
- `src/SmokeTest/` — harness de fumaça dev-only (veja acima).
- `tests/YtDlpCore.Tests/` — testes do parser.
- Solução: `PixieDownloader.slnx` (inclui só os dois projetos "de produção" + os testes xunit — `SmokeTest` fica de fora de propósito).

## Arquitetura
- **`IYtDlpService` é a única fronteira que a UI WPF conhece** — nenhum `Process` é chamado fora de `YtDlpCore`. `YtDlpService` compõe `YtDlpProcessRunner` (exec + captura de stdout/stderr), `BinaryManager` (resolve/baixa/atualiza `yt-dlp`/`ffmpeg` de `./tools/` ou do PATH), `UpdateChecker` e `ThumbnailCache`. Toda operação é async/cancelável e emite `LogEntry` via evento `LogEmitted` (consumido pela aba de logs) além de persistir no `SessionLogger`, se houver.
- **Pipeline de download de vídeo com passo de ffmpeg** (`YtDlpService.DownloadAsync`/`PostProcessAndDeliverAsync`): quando o vídeo precisa de mudança de velocidade ou de "vídeo mudo + mp3 separado" (`VideoNeedsFfmpegPass`), o yt-dlp baixa para uma pasta de trabalho privada (`home` e `temp` do yt-dlp apontam pra lá) em vez do diretório de saída final. Depois, os arquivos são localizados no disco por extensão (não por parsing do log — robusto a título com emoji/Unicode), processados com um passo de `ffmpeg` próprio (`BuildSpeedPassArgs`: `setpts` pro vídeo, `atempo` encadeado pro áudio — o `--recode` do yt-dlp faz no-op num arquivo já `.mp4`, então não dá pra confiar nele) e só então movidos pra pasta de saída real, preservando as subpastas do template. A pasta de trabalho é sempre apagada no `finally`.
- **`MainViewModel` é um VM único e grande** (`src/PixieDownloader/ViewModels/MainViewModel.cs`, ~1400 linhas) que cobre todas as abas do app: análise/playlist, seleção por range, montagem incremental do template de saída (stack de tokens), download/batch com progresso, aba de debug (comandos crus do yt-dlp) e aba de logs. Ao adicionar uma feature de UI, o padrão é uma nova seção comentada (`// ───── Nome ─────`) dentro desse mesmo arquivo, não um VM novo.
- **Single instance**: `App.xaml.cs` usa um `Mutex` nomeado + `EventWaitHandle` pra focar a janela existente em vez de abrir uma segunda instância.
- **Settings com auto-save**: `SettingsService` assina `PropertyChanged` de todos os nós de `AppSettings` (`Ui`, `Paths`, `Audio`, `Video`, `Advanced`, `Tools`, `RecentUrls`) e agenda um save debounced (500ms) a cada mudança; salva em `settings.json` com backup (`settings.json.bak`) e fallback pro backup se o arquivo principal estiver corrompido no load.

## Convenções / decisões já tomadas
- **MVVM escrito à mão (sem CommunityToolkit.Mvvm).** Base `ObservableObject` em `src/YtDlpCore/Mvvm/ObservableObject.cs` (expõe `SetProperty`/`OnPropertyChanged`); comandos `RelayCommand`/`RelayCommand<T>`/`AsyncRelayCommand` em `src/PixieDownloader/Mvvm/RelayCommand.cs`. Sem geradores de código — toda propriedade observável é explícita (`get`/`set => SetProperty(...)`) e cada comando é uma propriedade pública inicializada no construtor do VM. `CanExecuteChanged` é roteado pelo `CommandManager.RequerySuggested`. A saída tem só 2 DLLs (`PixieDownloader.dll`, `YtDlpCore.dll`).
- **Sem DI container.** A composição é manual no `App.xaml.cs` (`Microsoft.Extensions.Hosting` foi removido de propósito — deixava 27 DLLs inúteis na saída). Grafo: `SessionLogger → SettingsService → YtDlpService → MainViewModel → MainWindow`, disposto no `OnExit`.
- **Settings:** `settings.json` ao lado do `.exe`, auto-save com debounce (`SettingsService`) que depende do `PropertyChanged` borbular dos nós de `AppSettings`. Se mexer em settings, garanta que os setters disparam `PropertyChanged`.
- **Staging de download:** arquivos intermediários do yt-dlp vão pra uma pasta oculta `.~downloads` **ao lado do `.exe`** (`-P temp/home`); só o arquivo final vai pra pasta de saída.
- **Logo:** `Assets/logo.ico` (embedado como Resource WPF + `ApplicationIcon`). Não há mais `logo.png`.
- Binários `yt-dlp`/`ffmpeg`: resolvidos de `./tools/` ou do PATH.

## Preferências do dono
- Specs e conversa em **PT-BR**.
- Prefere **código explícito e legível** a "mágica" (geradores de código). Tende a implementar coisas à mão para aprender — ok, mas avise se ele estiver reconstruindo um framework inteiro.
