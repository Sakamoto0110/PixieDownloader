# PixieDownloader.Sdk

O contrato de plugin do [PixieDownloader](https://github.com/Sakamoto0110/PixieDownloader).
Um plugin é uma pasta `plugins/<id>/` ao lado do `PixieDownloader.exe` com um
`plugin.json` e um assembly; o app carrega cada uma num `AssemblyLoadContext`
próprio e conversa com o plugin só por estes tipos:

- `IPixiePlugin` — o ponto de entrada: `Configure(IPluginHost)`.
- `IPluginHost` — o que o host oferece: `Downloads` (`IYtDlpService`, o mesmo
  serviço que o app usa), `DataDirectory`, `Log`, `ShutdownToken`, o `Manifest`
  do próprio plugin e o registro de capacidades. Desde a API 1.1: os eventos
  `AnalysisCompleted` (toda análise que o usuário fez) e `DownloadCompleted`
  (arquivo final entregue), e `RequireAnalysisComments()` pra análise trazer os
  comentários enquanto o plugin quiser.
- `IUiContribution` — implemente na mesma classe se o plugin tem uma aba.
- `PluginManifest` — o `plugin.json` tipado.

Nenhum plugin conhece outro. Tudo passa pelo host.

## Referenciar

O pacote traz o `PixieDownloader.Sdk.dll` e o `YtDlpCore.dll` (a API inclui o
core). Os dois já existem dentro do app, então **não copie nenhum para a pasta do
plugin** — `ExcludeAssets="runtime"` cuida disso:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>                       <!-- só se o plugin tiver aba -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PixieDownloader.Sdk" Version="1.1.0" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

Se o pacote veio como asset da release em vez do nuget.org:
`dotnet nuget add source <pasta-onde-está-o-.nupkg> --name pixie`.

## `plugin.json`

```json
{
  "id": "hello",
  "name": "Hello",
  "version": "0.1.0",
  "apiVersion": "1.1",
  "assemblyFile": "Pixie.Hello.dll",
  "entryType": "Pixie.Hello.HelloPlugin",
  "dependsOn": []
}
```

`apiVersion` é a versão deste pacote (major.minor). O host aceita o plugin quando
o major é o mesmo e o minor não é mais novo que o dele — e recusa, com o motivo
nos logs, antes de carregar qualquer código.

## Plugin mínimo

```csharp
using System.Windows;
using System.Windows.Controls;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace Pixie.Hello;

public sealed class HelloPlugin : IPixiePlugin, IUiContribution
{
    private IPluginHost _host = null!;

    public void Configure(IPluginHost host) => _host = host;

    public string TabHeader => "Hello";

    public FrameworkElement CreateView()
    {
        var button = new Button { Content = "Ferramentas" };
        button.Click += async (_, _) =>
        {
            var status = await _host.Downloads.CheckYtDlpAsync(_host.ShutdownToken);
            _host.Log(LogLevel.Info, $"yt-dlp: {status}");
        };
        return button;
    }
}
```

## Ciclo de vida

- **Desabilitar** é imediato: a aba some, `ShutdownToken` é cancelado, as
  capacidades registradas são removidas. O assembly continua na memória até o
  app fechar — WPF não permite descarregar de verdade.
- **Desinstalar** apaga a pasta `plugins/<id>/` no próximo start (o arquivo fica
  em uso até lá). `data/<id>/` fica: é do usuário.
