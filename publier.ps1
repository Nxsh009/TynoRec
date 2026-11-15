$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Test-Path .git)) { Write-Host "Ce dossier n'est pas un depot git."; exit 1 }

Write-Host "Verification de la compilation..."
dotnet build -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "ERREUR : le code ne compile pas. Rien n'a ete publie."; exit 1 }

$msg = Read-Host "Message de la mise a jour"
if (-not $msg) { $msg = "Mise a jour" }

$last = git tag --list 'v*' | ForEach-Object { try { [version]($_.TrimStart('v')) } catch { } } | Sort-Object | Select-Object -Last 1
if ($last) { $ver = "v{0}.{1}.{2}" -f $last.Major, $last.Minor, ($last.Build + 1) } else { $ver = "v1.0.0" }
Write-Host "Nouvelle version : $ver"
$ok = Read-Host "Publier ? (o/n)"
if ($ok -ne 'o') { Write-Host "Annule."; exit 0 }

git add .
git commit -m "$ver - $msg"
if ($LASTEXITCODE -ne 0) { Write-Host "Rien a publier (aucune modification)."; exit 1 }
git push
if ($LASTEXITCODE -ne 0) { Write-Host "ERREUR pendant git push."; exit 1 }
git tag $ver
git push --tags
if ($LASTEXITCODE -ne 0) { Write-Host "ERREUR pendant l'envoi du tag."; exit 1 }

Write-Host ""
Write-Host "Envoye ! Suis la compilation ici : https://github.com/Nxsh009/TynoRec/actions"
