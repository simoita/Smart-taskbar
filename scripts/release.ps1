[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version,

    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path ".git")) {
    throw "Questa cartella non è un repository Git. Segui docs/aggiornamenti.md per il primo setup GitHub."
}

$changes = ((& git status --porcelain) -join "`n").Trim()
if ($changes.Length -gt 0) {
    throw "Ci sono modifiche non committate. Fai prima il commit del codice, poi esegui questo script per la release."
}

$remote = ((& git remote get-url origin 2>$null) -join "`n").Trim()
if (-not $NoPush -and $remote.Length -eq 0) {
    throw "Nessun remote 'origin'. Aggiungi il repository GitHub prima della release: git remote add origin https://github.com/OWNER/REPO.git"
}

$projectPath = Join-Path $root "src\Nebula\Nebula.csproj"
$project = [System.IO.File]::ReadAllText($projectPath)
$versionMatch = [regex]::Match($project, '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>')
if (-not $versionMatch.Success) {
    throw "Non trovo <Version> in src\Nebula\Nebula.csproj."
}

$current = $versionMatch.Groups[1].Value
if ([string]::IsNullOrWhiteSpace($Version)) {
    $parts = $current.Split(".")
    $Version = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
}

$Version = $Version.Trim().TrimStart("v", "V")
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Versione non valida: '$Version'. Usa il formato MAJOR.MINOR.PATCH."
}
if ($Version -eq $current) {
    throw "La versione $Version è già quella nel progetto. Indica una versione successiva."
}

$tag = "v$Version"
if ((& git tag --list $tag)) {
    throw "Il tag $tag esiste già."
}

$updated = [regex]::Replace($project, '(<Version>)\s*[0-9]+\.[0-9]+\.[0-9]+\s*(</Version>)', "`$1$Version`$2", 1)
[System.IO.File]::WriteAllText($projectPath, $updated, [System.Text.UTF8Encoding]::new($false))

& git add "src/Nebula/Nebula.csproj"
& git commit -m "chore(release): $tag"
& git tag $tag

Write-Host "Creato commit e tag $tag." -ForegroundColor Green

if ($NoPush) {
    Write-Host "Nessun push richiesto (-NoPush). Quando vuoi pubblicare: git push origin HEAD; git push origin $tag" -ForegroundColor Yellow
    return
}

& git push origin HEAD
if ($LASTEXITCODE -ne 0) { throw "Push del branch non riuscito." }
& git push origin $tag
if ($LASTEXITCODE -ne 0) { throw "Push del tag non riuscito." }

Write-Host "Release $tag inviata. GitHub Actions compilerà e pubblicherà ZIP e update.json." -ForegroundColor Green
