# Crea el instalador de Agent Manager Notch: .\instalador\AgentManagerNotch-Setup-X.Y.Z.exe
#
# Es el propio programa compilado como un único .exe autónomo (no necesita .NET instalado): al abrirlo con ese
# nombre se comporta como instalador (ver Services\Installer.cs). Se instala por usuario, sin permisos de
# administrador, con la opción de iniciar con Windows; se desinstala desde «Aplicaciones instaladas».
#
# No se comprime a propósito: el ejecutable comprimido ocupa la mitad, pero tarda ~0,9 s en cada
# arranque (y Claude Code lo lanza en cada herramienta, modo --hook) frente a ~0,2 s sin comprimir.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = Get-ChildItem versiones\*.md |
    ForEach-Object { $v = $null; if ([version]::TryParse($_.BaseName, [ref]$v)) { $v } } |
    Sort-Object -Descending | Select-Object -First 1
$branch = (git rev-parse --abbrev-ref HEAD).Trim()

$build = Join-Path $env:TEMP "agent-manager-notch-instalador"
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
dotnet publish AgentManagerNotch.csproj -c Release -r win-x64 -o $build --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "La compilación falló" }

$out = Join-Path $PSScriptRoot "instalador"
New-Item -ItemType Directory -Force $out | Out-Null
$setup = Join-Path $out "AgentManagerNotch-Setup-$($version.ToString(3)).exe"
Copy-Item (Join-Path $build "AgentManagerNotch.exe") $setup -Force
Remove-Item $build -Recurse -Force

Write-Host ""
Write-Host "Listo: $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB, rama $branch)" -ForegroundColor Green
