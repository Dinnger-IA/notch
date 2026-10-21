# Trae a la rama agent-manager los cambios de main del repositorio público (GitHub Dinnger-IA/notch).
#   .\sincronizar-notch.ps1        -> configura remotos y hook, actualiza main y lo fusiona en agent-manager
# No sube nada: revisa el resultado y haz «git push» (agent-manager va solo a Azure, remoto origin).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$notchUrl = "https://github.com/Dinnger-IA/notch.git"
$azureUrl = "https://dev.azure.com/grupodistelsa/innovation-sandbox/_git/agent-manager-notch"

function Invoke-Git { git @args; if ($LASTEXITCODE -ne 0) { throw "git $args falló" } }

# Remotos: notch (GitHub, solo main) y origin (Azure, agent-manager). Ninguna URL de GitHub como destino de origin.
if (-not (git remote | Select-String -SimpleMatch -Pattern "notch" -Quiet)) { Invoke-Git remote add notch $notchUrl }
Invoke-Git remote set-url notch $notchUrl
Invoke-Git remote set-url origin $azureUrl
foreach ($u in @(git remote get-url --push --all origin)) { if ($u -ne $azureUrl) { Invoke-Git remote set-url --delete --push origin $u } }
Invoke-Git config branch.agent-manager.remote origin
Invoke-Git config branch.agent-manager.merge refs/heads/agent-manager

# Hook que impide subir a GitHub otra rama que no sea main
Copy-Item .githooks\pre-push .git\hooks\pre-push -Force

if (git status --porcelain --untracked-files=no) { throw "Hay cambios sin guardar: haz commit o guárdalos antes de sincronizar." }
$branch = git rev-parse --abbrev-ref HEAD

Invoke-Git fetch notch
if (git rev-parse --verify --quiet main) { Invoke-Git checkout main; Invoke-Git merge --ff-only notch/main }
else { Invoke-Git checkout -b main notch/main }
Invoke-Git branch --set-upstream-to=notch/main main

Invoke-Git checkout agent-manager
git merge main --no-edit
if ($LASTEXITCODE -ne 0) { Write-Host "Hay conflictos: resuélvelos, haz commit y luego git push." -ForegroundColor Yellow; exit 1 }
if ($branch -ne "agent-manager" -and $branch -ne "main") { Invoke-Git checkout $branch }
Write-Host ""
Write-Host "Listo: main = notch/main y fusionado en agent-manager. Revisa y haz 'git push' (va a Azure)." -ForegroundColor Green
