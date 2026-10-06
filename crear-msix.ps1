# Crea el paquete de Microsoft Store: .\instalador\AgentManagerNotch-X.Y.Z.0.msix
#   .\crear-msix.ps1          -> paquete para subir a Partner Center (la Store lo firma; no hace falta certificado)
#   .\crear-msix.ps1 -Probar  -> además lo registra en este equipo sin empaquetar, para probar la edición de la
#                                Store (requiere el modo de desarrollador de Windows). Quitarlo:
#                                Get-AppxPackage *AgentManagerNotch* | Remove-AppxPackage
#
# La identidad (nombre del paquete y editor) sale de store\identidad.json; la versión, de la mayor de versiones\.
# Necesita makeappx.exe del SDK de Windows 10/11.
param([switch]$Probar)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = Get-ChildItem versiones\*.md |
    ForEach-Object { $v = $null; if ([version]::TryParse($_.BaseName, [ref]$v)) { $v } } |
    Sort-Object -Descending | Select-Object -First 1
$msixVersion = "$($version.ToString(3)).0" # la Store exige revisión 0
$id = Get-Content store\identidad.json -Raw -Encoding utf8 | ConvertFrom-Json

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "No se encontró makeappx.exe: instala el SDK de Windows (winget install Microsoft.WindowsSDK.10.0.26100)" }

# Copia sin empaquetar: el .exe autónomo (sin archivo único: el MSIX ya va comprimido) + manifiesto + logotipos
$layout = Join-Path $PSScriptRoot "obj\msix\layout"
if ($Probar) { Get-AppxPackage -Name $id.Name | Remove-AppxPackage -ErrorAction SilentlyContinue }
Get-Process AgentManagerNotch -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$layout*" } | Stop-Process -Force
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
dotnet publish AgentManagerNotch.csproj -c Release -r win-x64 -o $layout --self-contained true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "La compilación falló" }

$manifest = (Get-Content store\AppxManifest.xml -Raw -Encoding utf8).
    Replace("{{Name}}", $id.Name).Replace("{{Publisher}}", $id.Publisher).
    Replace("{{PublisherDisplayName}}", $id.PublisherDisplayName).Replace("{{DisplayName}}", $id.DisplayName).
    Replace("{{Version}}", $msixVersion)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, [Text.UTF8Encoding]::new($false))
Copy-Item store\Assets (Join-Path $layout "Assets") -Recurse -Force

$out = Join-Path $PSScriptRoot "instalador"
New-Item -ItemType Directory -Force $out | Out-Null
$msix = Join-Path $out "AgentManagerNotch-$msixVersion.msix"
& $makeappx.FullName pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx no pudo crear el paquete" }
Write-Host ""
Write-Host "Listo: $msix ($([math]::Round((Get-Item $msix).Length / 1MB)) MB, $($id.Name))" -ForegroundColor Green

if ($Probar) {
    Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml")
    Write-Host "Registrado para probar: búscalo en el menú Inicio como «$($id.DisplayName)»." -ForegroundColor Green
}
