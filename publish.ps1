# Compila Agent Manager Notch en .\publish (todo queda dentro de la carpeta del proyecto).
#   .\publish.ps1                 -> requiere .NET 10 Desktop Runtime en el equipo destino (~1 MB)
#   .\publish.ps1 -SelfContained  -> un único .exe autónomo, no requiere nada instalado (~70 MB)
param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot "publish"
Get-Process AgentManagerNotch -ErrorAction SilentlyContinue | Stop-Process -Force

$common = @("publish", "AgentManagerNotch.csproj", "-c", "Release", "-r", "win-x64", "-o", $out)
if ($SelfContained) {
    dotnet @common --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
} else {
    dotnet @common --self-contained false
}
if ($LASTEXITCODE -ne 0) { throw "La compilación falló" }
Write-Host ""
Write-Host "Listo: $out\AgentManagerNotch.exe" -ForegroundColor Green
