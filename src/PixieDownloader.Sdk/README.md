# PixieDownloader.Sdk

O contrato de plugin do [PixieDownloader](https://github.com/Sakamoto0110/PixieDownloader).
Um plugin mora em `plugins/` ao lado do `PixieDownloader.exe` — **um `.dll`
solto** quando ele não precisa de mais nada (`plugins/Pixie.Hello.dll`), ou
**uma pasta própria** quando traz dependências (`plugins/tracklist/` com o
`TagLibSharp.dll` dele dentro), pra dependência de um nunca se misturar com
outro. O app carrega cada um num `AssemblyLoadContext` próprio e conversa com
o plugin só por estes tipos:

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

No nuget.org é `dotnet add package PixieDownloader.Sdk`. Se preferir o asset
da release: `dotnet nuget add source <pasta-onde-está-o-.nupkg> --name pixie`.

## Como o app reconhece o plugin

Sem arquivo nenhum além do `.dll`: o host lê os metadados do assembly (sem
carregar código) e tira dali tudo que precisa —

| o quê | de onde |
|---|---|
| id | o nome da pasta, ou do arquivo sem `.dll` |
| assembly | o único `.dll` da pasta que referencia `PixieDownloader.Sdk` |
| classe de entrada | a única classe que implementa `IPixiePlugin` |
| nome | `<AssemblyTitle>` do csproj (sem ele, o id) |
| versão | `<Version>` do csproj |
| apiVersion | a versão deste pacote que você referenciou |

O host aceita o plugin quando o major do apiVersion é o mesmo e o minor não é
mais novo que o dele — e recusa, com o motivo nos logs e na aba Plugins, antes
de carregar qualquer código.

Um `plugin.json` na pasta **substitui** tudo isso, e é obrigatório quando a
convenção não basta: mais de uma classe `IPixiePlugin` no assembly, dependência
de outro plugin (`dependsOn`), id diferente da pasta. (Só em pasta: um `.dll`
solto vai sempre pela convenção.)

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
- **Desinstalar** apaga o `.dll` (ou a pasta) no próximo start — o arquivo fica
  em uso até lá. `data/<id>/` fica: é do usuário.
- **Recarregar** (aba Plugins) olha a pasta de novo sem reiniciar: plugin novo
  entra e carrega, um recusado com o problema corrigido também.
