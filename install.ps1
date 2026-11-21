# Install the AutoCinema plugin DLL into the ETS2LA Plugins folder.
$ErrorActionPreference = 'Stop'
$src = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'dist\AutoCinema.dll'
$dst = Join-Path $env:LOCALAPPDATA 'ETS2LA\current\Plugins\AutoCinema.dll'
if (-not (Test-Path $src)) { throw "Built DLL not found: $src (run build.ps1 first)" }
Copy-Item $src $dst -Force
Write-Host "Installed to $dst"
Write-Host 'Restart ETS2LA, then enable "baobao-7799 Auto Cinema" in Settings -> Plugins.'
