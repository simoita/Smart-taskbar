[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = Join-Path $root ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$project = Join-Path $root "src\Nebula\Nebula.csproj"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectText = [System.IO.File]::ReadAllText($project)
    $match = [regex]::Match($projectText, '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>')
    if (-not $match.Success) {
        throw "Non trovo <Version> in src\Nebula\Nebula.csproj."
    }
    $Version = $match.Groups[1].Value
}

$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$outputDir = Join-Path $root "artifacts\setup"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

if (-not $SkipPublish) {
    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Write-Host "Pubblicazione self-contained $Version ($Runtime)..." -ForegroundColor Cyan
    & $dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
        -p:Version=$Version -o $publishDir -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish non è riuscito."
    }
}

foreach ($required in @("Nebula.exe", "Nebula.dll", "Nebula.Watchdog.exe",
                        "Nebula.Watchdog.dll", "Nebula.Watchdog.deps.json",
                        "Nebula.Watchdog.runtimeconfig.json")) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "File mancante nel publish: $required"
    }
}

$watchdogRuntime = Get-Content (Join-Path $publishDir "Nebula.Watchdog.runtimeconfig.json") -Raw |
    ConvertFrom-Json
if (-not $watchdogRuntime.runtimeOptions.includedFrameworks) {
    throw "Il watchdog è framework-dependent: su un PC senza .NET 8 non potrebbe ripristinare la taskbar."
}

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 non è installato. Installa 'Inno Setup 6' per creare il setup."
}

$script = Join-Path $root "installer\Nebula.iss"
Write-Host "Creazione del setup con Inno Setup..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" $script
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup non è riuscito a creare il setup."
}

$setup = Join-Path $outputDir "Nebula-Setup-$Version-x64.exe"
if (-not (Test-Path $setup)) {
    throw "Setup non trovato: $setup"
}

$sizeMb = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Setup pronto: $setup ($sizeMb MB)" -ForegroundColor Green
