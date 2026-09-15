<#
  Monta os pacotes de release do PixieDownloader — exatamente o que o workflow do GitHub publica.

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1            # os dois .zip
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1 -Flavor portable
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.4.0   # confere com o <Version> do .csproj

  Sai em bin/release/:
    PixieDownloader-vX.Y.Z-win-x64.zip            .exe pequeno, precisa do .NET 10 Desktop Runtime instalado
    PixieDownloader-vX.Y.Z-win-x64-portable.zip   .exe com o .NET dentro, roda em Windows pelado
    PixieDownloader.Sdk.A.B.C.nupkg               o SDK pra escrever plugins (versão própria = apiVersion, não a do app)
    PixieDownloader-plugin-tracktracer-vA.B.C.zip o plugin TrackTracer (versão própria); extrai em plugins\ ao lado do .exe
    PixieDownloader-plugin-library-vA.B.C.zip     o plugin Library (versão própria); idem
    plugins.json                                  o catálogo dos plugins desta release (id, nome, versão, API, descrição,
                                                  zip, sha256) — a aba Plugins do app lê de releases/latest/download/
    SHA256SUMS.txt                                hash de todos (formato do sha256sum)
    RELEASE_NOTES.md                              tabela dos assets, usada pelo workflow no corpo da release

  Cada .zip abre numa pasta PixieDownloader\ com o .exe, LICENSE e LEIA-ME.txt — e só. yt-dlp e ffmpeg não
  vão no pacote de propósito (o app baixa os dois sozinho na primeira abertura, direto das fontes originais;
  assim a release não redistribui o binário GPL do ffmpeg). Nada de cache/, logs/, settings ou staging vai
  junto: a pasta é montada do zero a partir do publish. Roda no Windows PowerShell 5.1 e no pwsh 7.
#>
[CmdletBinding()]
param(
    # Versão esperada (a do tag). Quando informada, precisa bater com o <Version> do .csproj.
    [string]$Version,
    [ValidateSet('all', 'normal', 'portable')]
    [string]$Flavor = 'all',
    [switch]$SkipTests,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo    = Split-Path -Parent $PSScriptRoot
$csproj  = Join-Path $repo 'src\PixieDownloader\PixieDownloader.csproj'
$sdkProj = Join-Path $repo 'src\PixieDownloader.Sdk\PixieDownloader.Sdk.csproj'
# Os plugins que saem na release, cada um como zip próprio (Hello é só de desenvolvimento e fica de fora).
$plugins = @(
    @{ Id = 'tracktracer'; Name = 'TrackTracer'; Dll = 'Pixie.TrackTracer.dll'; Proj = Join-Path $repo 'src\Plugins\Pixie.TrackTracer\Pixie.TrackTracer.csproj'; Out = Join-Path $repo 'src\Plugins\Pixie.TrackTracer\bin\Release\net10.0-windows' }
    @{ Id = 'library'; Name = 'Library'; Dll = 'Pixie.Library.dll'; Proj = Join-Path $repo 'src\Plugins\Pixie.Library\Pixie.Library.csproj'; Out = Join-Path $repo 'src\Plugins\Pixie.Library\bin\Release\net10.0-windows' }
)
$sln     = Join-Path $repo 'PixieDownloader.slnx'
$license = Join-Path $repo 'LICENSE'
if (-not $OutDir) { $OutDir = Join-Path $repo 'bin\release' }

function Step([string]$text) { Write-Host "== $text ==" -ForegroundColor Cyan }
function Write-Utf8([string]$path, [string]$text, [bool]$bom = $false) { [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $bom)) }
function Run([string]$description, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$description falhou (exit $LASTEXITCODE)" }
}

# ───── Versão: única fonte é o <Version> do .csproj; o tag só confirma ─────
$csprojVersion = (Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value.Trim()
if (-not $csprojVersion) { throw "Não achei <Version> em $csproj" }
if ($Version -and ($Version -ne $csprojVersion)) {
    throw "Versão pedida ($Version) não bate com o <Version> do .csproj ($csprojVersion). Ajuste o .csproj antes de taggear."
}
$Version = $csprojVersion
# O SDK tem versão própria (o apiVersion do contrato de plugin), lida do mesmo jeito.
$sdkVersion = (Select-String -Path $sdkProj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value.Trim()
if (-not $sdkVersion) { throw "Não achei <Version> em $sdkProj" }
Write-Host "PixieDownloader $Version (Sdk $sdkVersion)"

$flavors = @()
if ($Flavor -in 'all', 'normal')   { $flavors += @{ Name = 'normal';   Profile = 'FrameworkDependent'; PublishDir = 'framework-dependent'; Suffix = '' } }
if ($Flavor -in 'all', 'portable') { $flavors += @{ Name = 'portable'; Profile = 'SelfContained';      PublishDir = 'self-contained';      Suffix = '-portable' } }

# ───── 1. restore / build / test ─────
Step '1/4 restore + build (Release)'
Run 'dotnet restore' { dotnet restore $sln --nologo -v q }
Run 'dotnet build'   { dotnet build $sln -c Release --no-restore --nologo -v q }
if ($SkipTests) { Write-Host 'testes pulados (-SkipTests)' }
else {
    Step '1/4 testes'
    Run 'dotnet test' { dotnet test $sln -c Release --no-build --nologo -v q }   # os dois projetos de teste da solução
}

# ───── 2. publish (um .exe por sabor, via os profiles versionados do projeto) ─────
Step '2/4 publish'
foreach ($f in $flavors) {
    Run "publish $($f.Name)" { dotnet publish $csproj -c Release "-p:PublishProfile=$($f.Profile)" --nologo -v q }
    $f.Exe = Join-Path $repo "src\PixieDownloader\bin\publish\$($f.PublishDir)\PixieDownloader.exe"
    if (-not (Test-Path $f.Exe)) { throw "publish $($f.Name) não produziu $($f.Exe)" }
    $exeVersion = (Get-Item $f.Exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    if ($exeVersion -ne $Version) { throw "O .exe publicado diz $exeVersion, esperado $Version" }
    Write-Host ("  {0,-8} {1,6:N1} MB  {2}" -f $f.Name, ((Get-Item $f.Exe).Length / 1MB), $f.Exe)
}

# ───── 3. montar pastas e zipar ─────
Step '3/4 pacotes'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$sums = @()
$notes = @("## Assets", "", "| Arquivo | O que é | SHA256 |", "|---|---|---|")
foreach ($f in $flavors) {
    $zipName = "PixieDownloader-v$Version-win-x64$($f.Suffix).zip"
    $zipPath = Join-Path $OutDir $zipName
    $stage   = Join-Path $OutDir "stage-$($f.Name)"
    $pkg     = Join-Path $stage 'PixieDownloader'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $pkg | Out-Null

    Copy-Item $f.Exe   (Join-Path $pkg 'PixieDownloader.exe')
    Copy-Item $license (Join-Path $pkg 'LICENSE')

    $runtimeLine = if ($f.Name -eq 'portable') { 'Não precisa instalar nada: o .NET vai dentro do .exe.' }
                   else { 'Precisa do .NET 10 Desktop Runtime instalado: https://dotnet.microsoft.com/download/dotnet/10.0' }
    # Com BOM: é um .txt pra abrir no Notepad/PowerShell, que sem BOM leem como ANSI e mostram os acentos quebrados.
    Write-Utf8 (Join-Path $pkg 'LEIA-ME.txt') -bom $true -text (@(
        "PixieDownloader v$Version (win-x64$($f.Suffix))"
        "https://github.com/Sakamoto0110/PixieDownloader"
        ""
        "Extraia esta pasta onde quiser e abra PixieDownloader.exe."
        $runtimeLine
        ""
        "Na primeira abertura o app baixa sozinho o yt-dlp e o ffmpeg (~100 MB, precisa de internet) para a pasta tools\,"
        "e depois mantém o yt-dlp atualizado por conta própria (Verificar atualização)."
        ""
        "Em uso, o app cria ao lado do .exe: settings.json, tools\, cache\, logs\, plugins\, data\, .~downloads\ e pending-downloads.txt."
        ""
        "Plugins: a aba Plugins lista os oficiais da última release e instala com um clique (precisa de internet)."
        "Ou extraia o zip de um plugin (ex.: PixieDownloader-plugin-tracktracer-*.zip, na mesma release) dentro de"
        "plugins\ e reabra o app; a aba liga, desliga e desinstala cada um."
    ) -join "`r`n")

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $stage -Recurse -Force

    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash  $zipName"
    $what = if ($f.Name -eq 'portable') { 'Portable: .NET 10 embutido, roda em Windows pelado' } else { 'Precisa do .NET 10 Desktop Runtime instalado' }
    $notes += "| ``$zipName`` | $what | ``$hash`` |"
    Write-Host ("  {0,-45} {1,6:N1} MB  {2}" -f $zipName, ((Get-Item $zipPath).Length / 1MB), $hash)
}

# O SDK de plugins: um .nupkg só (contrato + YtDlpCore dentro), empacotado do build Release do passo 1 —
# --no-build pra ser exatamente o que foi testado. O host leva a mesma assembly dentro do .exe; o pacote é a
# superfície de compile-time de quem escreve plugin fora do repo (docs/ROADMAP.md, "Distribuição").
$nupkgName = "PixieDownloader.Sdk.$sdkVersion.nupkg"
$nupkgPath = Join-Path $OutDir $nupkgName
if (Test-Path $nupkgPath) { Remove-Item $nupkgPath -Force }
Run 'dotnet pack Sdk' { dotnet pack $sdkProj -c Release --no-build --nologo -v q -o $OutDir }
if (-not (Test-Path $nupkgPath)) { throw "dotnet pack não produziu $nupkgPath" }
$hash = (Get-FileHash $nupkgPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sums += "$hash  $nupkgName"
$notes += "| ``$nupkgName`` | SDK pra escrever plugins (``PackageReference``), não é pra usuário final | ``$hash`` |"
Write-Host ("  {0,-45} {1,6:N1} MB  {2}" -f $nupkgName, ((Get-Item $nupkgPath).Length / 1MB), $hash)

# Os plugins: o build Release do passo 1 já os deixou prontos (e testados). Cada zip abre numa pasta com o
# id do plugin, pra extrair direto em plugins\ ao lado do .exe. Sem .pdb. Cada um também vira uma entrada do
# plugins.json (abaixo), que é o que a aba Plugins do app lê pra oferecer "Instalar".
$apiVersion = ($sdkVersion -split '\.')[0..1] -join '.'   # os plugins do repo compilam contra o Sdk do repo
$catalogEntries = @()
foreach ($pl in $plugins) {
    $plVersion = (Select-String -Path $pl.Proj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value.Trim()
    if (-not $plVersion) { throw "Não achei <Version> em $($pl.Proj)" }
    if (-not (Test-Path (Join-Path $pl.Out $pl.Dll))) { throw "O plugin $($pl.Id) não foi buildado em $($pl.Out)" }
    # Nome e descrição vêm do csproj (AssemblyTitle é o que a aba Plugins mostra; Description é o texto da loja).
    $titleMatch = Select-String -Path $pl.Proj -Pattern '<AssemblyTitle>([^<]+)</AssemblyTitle>'
    $plName = if ($titleMatch) { $titleMatch.Matches[0].Groups[1].Value.Trim() } else { $pl.Name }
    $descMatch = Select-String -Path $pl.Proj -Pattern '<Description>([^<]+)</Description>'
    $plDescription = if ($descMatch) { $descMatch.Matches[0].Groups[1].Value.Trim() } else { '' }
    $zipName = "PixieDownloader-plugin-$($pl.Id)-v$plVersion.zip"
    $zipPath = Join-Path $OutDir $zipName
    $stage   = Join-Path $OutDir "stage-plugin-$($pl.Id)"
    $pkg     = Join-Path $stage $pl.Id
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $pkg | Out-Null
    Get-ChildItem $pl.Out -File | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $pkg
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $stage -Recurse -Force

    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash  $zipName"
    $notes += "| ``$zipName`` | Plugin **$plName** $plVersion — instale pela aba Plugins do app, ou extraia dentro de ``plugins\`` ao lado do .exe e reabra | ``$hash`` |"
    Write-Host ("  {0,-45} {1,6:N1} MB  {2}" -f $zipName, ((Get-Item $zipPath).Length / 1MB), $hash)
    $catalogEntries += [ordered]@{
        id = $pl.Id; name = $plName; version = $plVersion; apiVersion = $apiVersion; description = $plDescription
        asset = $zipName; sha256 = $hash; size = (Get-Item $zipPath).Length
    }
}

# O catálogo da loja: o app baixa releases/latest/download/plugins.json, mostra cada entrada em "Plugins
# oficiais" e instala o zip conferindo o sha256 — mesma release, mesmo hash do SHA256SUMS.txt, nada escrito à
# mão. schemaVersion muda se o formato mudar de um jeito que um app antigo não entenda (PluginStore recusa).
$catalog = [ordered]@{ schemaVersion = 1; app = $Version; plugins = @($catalogEntries) }
$catalogName = 'plugins.json'
$catalogPath = Join-Path $OutDir $catalogName
Write-Utf8 $catalogPath ((ConvertTo-Json $catalog -Depth 5) + "`n")
$hash = (Get-FileHash $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sums += "$hash  $catalogName"
$notes += "| ``$catalogName`` | O catálogo que a aba Plugins do app lê (não precisa baixar) | ``$hash`` |"
Write-Host ("  {0,-45} {1,6:N1} KB  {2}" -f $catalogName, ((Get-Item $catalogPath).Length / 1KB), $hash)

# ───── 4. SHA256SUMS + notas (bloco da versão no CHANGELOG.md, se houver, seguido da tabela de assets) ─────
Step '4/4 SHA256SUMS.txt + RELEASE_NOTES.md'
Write-Utf8 (Join-Path $OutDir 'SHA256SUMS.txt') (($sums -join "`n") + "`n")
$notes += ""
$notes += "Os dois .zip do app trazem só o app: na primeira abertura ele baixa o yt-dlp e o ffmpeg sozinho (precisa de internet). Plugin é opcional: instale pela aba Plugins do app, ou extraia o zip dele em ``plugins\`` ao lado do .exe. O .nupkg é o SDK pra escrever plugins (versão própria, o apiVersion). Confira os hashes com ``SHA256SUMS.txt``."

$changelog = @()
$changelogPath = Join-Path $repo 'CHANGELOG.md'
if (Test-Path $changelogPath) {
    $inSection = $false
    foreach ($line in Get-Content $changelogPath -Encoding UTF8) {
        if ($line -match '^## \[') { $inSection = ($line -match ("^## \[" + [regex]::Escape($Version) + "\]")); continue }
        if ($inSection) { $changelog += $line }
    }
    if ($changelog.Count -eq 0) { Write-Host "  (CHANGELOG.md não tem um bloco ## [$Version] — notas só com a tabela de assets)" }
}
Write-Utf8 (Join-Path $OutDir 'RELEASE_NOTES.md') ((($changelog + $notes) -join "`n").Trim() + "`n")
Write-Host "pronto: $OutDir"
