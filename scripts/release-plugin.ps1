<#
.SYNOPSIS
  Empacota UM plugin oficial e atualiza o catálogo da loja — a release de plugin, separada da release do app.

.DESCRIPTION
  Um plugin tem versão própria (o <Version> do csproj dele) e sai por conta própria: a tag <id>-vX.Y.Z dispara
  .github/workflows/release-plugin.yml, que roda este script e sobe o resultado na release fixa `plugins` do
  GitHub (pré-release, então nunca vira a "latest" do app) — é de lá que a aba Plugins do app lê o
  plugins.json e baixa os zips. O app só tem release quando o app muda.

  Passos: build Release do plugin + testes dele; zip do plugin (uma pasta <id>\ com o .dll, as dependências
  próprias e o THIRD-PARTY.txt, sem .pdb); a entrada do catálogo (id, nome = <AssemblyTitle>, versão,
  apiVersion = major.minor do Sdk deste commit, descrição = <Description>, zip, sha256, size) mesclada no
  plugins.json que veio da release (-Catalog) — troca a entrada do mesmo id, mantém as outras; e as notas da
  release `plugins` (tabela do catálogo inteiro), que o workflow grava com `gh release edit`.

  Sai em bin/release/:
    PixieDownloader-plugin-<id>-vX.Y.Z.zip   o plugin; extrai em plugins\ ao lado do .exe
    plugins.json                             o catálogo inteiro, com esta entrada atualizada
    PLUGINS_NOTES.md                         corpo da release `plugins`

  Uso local: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release-plugin.ps1 -Id library
  (sem -Catalog o catálogo sai só com este plugin — serve pra testar a loja com Settings.Plugins.CatalogUrl
  apontando pra um `python -m http.server` em bin/release). Roda no Windows PowerShell 5.1 e no pwsh 7.
#>
[CmdletBinding()]
param(
    # Id do plugin: a pasta plugins\<id>\ e o prefixo da tag (<id>-vX.Y.Z).
    [Parameter(Mandatory = $true)]
    [string]$Id,
    # Versão esperada (a do tag). Quando informada, precisa bater com o <Version> do csproj do plugin.
    [string]$Version,
    # O plugins.json atual da release `plugins`; sem ele, o catálogo nasce só com este plugin.
    [string]$Catalog,
    [switch]$SkipTests,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo    = Split-Path -Parent $PSScriptRoot
$sdkProj = Join-Path $repo 'src\PixieDownloader.Sdk\PixieDownloader.Sdk.csproj'
if (-not $OutDir) { $OutDir = Join-Path $repo 'bin\release' }

# Os plugins oficiais (Hello é só de desenvolvimento e fica de fora). Out é o bin\Release do projeto do plugin.
$plugins = @{
    'tracktracer' = @{ Proj = 'src\Plugins\Pixie.TrackTracer\Pixie.TrackTracer.csproj'; Dll = 'Pixie.TrackTracer.dll'; Tests = 'tests\Pixie.TrackTracer.Tests\Pixie.TrackTracer.Tests.csproj' }
    'library'     = @{ Proj = 'src\Plugins\Pixie.Library\Pixie.Library.csproj';         Dll = 'Pixie.Library.dll';     Tests = 'tests\Pixie.Library.Tests\Pixie.Library.Tests.csproj' }
}

function Step([string]$text) { Write-Host "== $text ==" -ForegroundColor Cyan }
function Write-Utf8([string]$path, [string]$text) { [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false)) }
function Run([string]$description, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$description falhou (exit $LASTEXITCODE)" }
}
function Read-CsprojTag([string]$path, [string]$tag) {
    $m = Select-String -Path $path -Pattern "<$tag>([^<]+)</$tag>"
    if ($m) { $m.Matches[0].Groups[1].Value.Trim() } else { '' }
}

if (-not $plugins.ContainsKey($Id)) { throw "Plugin desconhecido: '$Id'. Conhecidos: $($plugins.Keys -join ', ')" }
$pl = $plugins[$Id]
$proj  = Join-Path $repo $pl.Proj
$tests = Join-Path $repo $pl.Tests
$out   = Join-Path (Split-Path -Parent $proj) 'bin\Release\net10.0-windows'

# ───── Versão: única fonte é o <Version> do csproj do plugin; o tag só confirma ─────
$plVersion = Read-CsprojTag $proj 'Version'
if (-not $plVersion) { throw "Não achei <Version> em $proj" }
if ($Version -and ($Version -ne $plVersion)) {
    throw "Versão pedida ($Version) não bate com o <Version> do csproj do plugin ($plVersion). Ajuste o csproj antes de taggear."
}
$Version = $plVersion
$sdkVersion = Read-CsprojTag $sdkProj 'Version'
if (-not $sdkVersion) { throw "Não achei <Version> em $sdkProj" }
$apiVersion = ($sdkVersion -split '\.')[0..1] -join '.'   # o plugin compila contra o Sdk deste commit
$plName = Read-CsprojTag $proj 'AssemblyTitle'
if (-not $plName) { $plName = $Id }
$plDescription = Read-CsprojTag $proj 'Description'
Write-Host "$plName $Version (API $apiVersion)"

# ───── 1. build + testes (só deste plugin; o projeto de testes builda o plugin por referência) ─────
Step '1/3 build (Release) + testes'
Run 'dotnet restore' { dotnet restore $tests --nologo -v q }
Run 'dotnet build'   { dotnet build $tests -c Release --no-restore --nologo -v q }
if ($SkipTests) { Write-Host 'testes pulados (-SkipTests)' }
else { Run 'dotnet test' { dotnet test $tests -c Release --no-build --nologo -v q --logger 'console;verbosity=normal' } }   # the logger: a failure on the runner must say which assertion, not only which test
if (-not (Test-Path (Join-Path $out $pl.Dll))) { throw "O build não deixou $($pl.Dll) em $out" }

# ───── 2. zip: uma pasta <id>\ pra extrair direto em plugins\ ao lado do .exe; sem .pdb ─────
Step '2/3 zip'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$zipName = "PixieDownloader-plugin-$Id-v$Version.zip"
$zipPath = Join-Path $OutDir $zipName
$stage   = Join-Path $OutDir "stage-plugin-$Id"
$pkg     = Join-Path $stage $Id
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null
Get-ChildItem $out -File | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $pkg
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zipPath).Length
Write-Host ("  {0,-45} {1,6:N1} MB  {2}" -f $zipName, ($size / 1MB), $hash)

# ───── 3. catálogo: a entrada deste plugin mesclada no plugins.json da release ─────
# schemaVersion muda se o formato mudar de um jeito que um app antigo não entenda (PluginStore recusa).
Step '3/3 plugins.json + PLUGINS_NOTES.md'
$entries = @{}
if ($Catalog) {
    if (-not (Test-Path $Catalog)) { throw "Catálogo não encontrado: $Catalog" }
    $current = Get-Content $Catalog -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($current.schemaVersion -ne 1) { throw "plugins.json com schemaVersion $($current.schemaVersion); este script escreve a 1" }
    foreach ($e in @($current.plugins)) {
        if (-not $e.id) { continue }
        $entries[[string]$e.id] = [ordered]@{
            id = [string]$e.id; name = [string]$e.name; version = [string]$e.version; apiVersion = [string]$e.apiVersion
            description = [string]$e.description; asset = [string]$e.asset; sha256 = [string]$e.sha256; size = [long]$e.size
        }
    }
    Write-Host "  catálogo atual: $($entries.Count) plugin(s)"
}
$entries[$Id] = [ordered]@{
    id = $Id; name = $plName; version = $Version; apiVersion = $apiVersion
    description = $plDescription; asset = $zipName; sha256 = $hash; size = $size
}
$ordered = @($entries.Keys | Sort-Object | ForEach-Object { $entries[$_] })
$updatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$catalogOut = [ordered]@{ schemaVersion = 1; updatedAt = $updatedAt; plugins = $ordered }
$catalogPath = Join-Path $OutDir 'plugins.json'
Write-Utf8 $catalogPath ((ConvertTo-Json $catalogOut -Depth 5) + "`n")
Write-Host ("  {0,-45} {1,6:N1} KB  {2} plugin(s)" -f 'plugins.json', ((Get-Item $catalogPath).Length / 1KB), $ordered.Count)

$notes = @(
    "Os plugins oficiais do PixieDownloader, cada um com versão própria. **Instale pela aba Plugins do app** (ela lê o ``plugins.json`` daqui e confere o SHA256) ou extraia o zip dentro de ``plugins\`` ao lado do .exe e reabra."
    ""
    "Esta release é o catálogo: cada tag ``<id>-vX.Y.Z`` (``$Id-v$Version`` foi a última, $updatedAt) substitui a entrada do plugin e sobe o zip novo; os zips de versões anteriores continuam aqui."
    ""
    "| Plugin | Versão | API | Arquivo | SHA256 |"
    "|---|---|---|---|---|"
)
foreach ($e in $ordered) {
    $notes += "| **$($e.name)** (``$($e.id)``) — $($e.description) | $($e.version) | $($e.apiVersion) | ``$($e.asset)`` | ``$($e.sha256)`` |"
}
$notes += ""
$notes += "``API`` é a versão do PixieDownloader.Sdk contra a qual o plugin foi compilado; o app recusa (mostra Incompatível) um plugin de API maior que a dele — nesse caso atualize o app."
Write-Utf8 (Join-Path $OutDir 'PLUGINS_NOTES.md') (($notes -join "`n") + "`n")
Write-Host "pronto: $OutDir"
