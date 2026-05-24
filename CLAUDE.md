# PixieDownloader

App desktop **WPF / .NET 10** (Windows) — GUI para o `yt-dlp` baixar áudio (MP3). Tema dark purple, janela custom (WindowChrome).

## Estrutura
- `src/YtDlpCore/` — biblioteca core (`net10.0`, **sem WPF**): serviço do yt-dlp, parser, settings, logger.
- `src/PixieDownloader/` — app WPF (`net10.0-windows`): Views, ViewModels, tema.
- Solução: `PixieDownloader.slnx`. Build: `dotnet build PixieDownloader.slnx -v q --nologo`.

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
