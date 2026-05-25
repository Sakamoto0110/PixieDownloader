<#
  Smoke test completo do PixieDownloader (rode da raiz do repo ou de qualquer lugar):
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/smoke.ps1

  Passos: 1) encerra a instância em execução (libera o lock da DLL)  2) build da solução
          3) boot test do app (sobe ~6s e fecha)  4) harness A (offline) + B (online, se test.txt tiver URL).
  Sai com 0 se tudo passou, !=0 caso contrário.
#>
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== [1/4] Encerrando instancia em execucao ==" -ForegroundColor Cyan
Get-Process PixieDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

Write-Host "== [2/4] Build da solucao ==" -ForegroundColor Cyan
dotnet build PixieDownloader.slnx -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FALHOU" -ForegroundColor Red; exit 1 }

Write-Host "== [3/4] Boot test (6s) ==" -ForegroundColor Cyan
$exe = Join-Path $root "src\PixieDownloader\bin\Debug\net10.0-windows\PixieDownloader.exe"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
if ($p.HasExited) { Write-Host "BOOT FALHOU (saiu cedo, exit $($p.ExitCode))" -ForegroundColor Red; exit 1 }
$null = $p.CloseMainWindow()
Start-Sleep -Seconds 3
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Write-Host "boot ok" -ForegroundColor Green

Write-Host "== [4/4] Harness (A offline + B online) ==" -ForegroundColor Cyan
dotnet run --project src\SmokeTest\SmokeTest.csproj -c Debug -v q
$harness = $LASTEXITCODE

Write-Host ""
if ($harness -eq 0) { Write-Host "SMOKE OK" -ForegroundColor Green } else { Write-Host "SMOKE FALHOU (exit $harness)" -ForegroundColor Red }
exit $harness
