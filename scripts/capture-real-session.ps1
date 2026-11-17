param(
    [string]$Dist = ".\dist\Nebula.exe",
    [string]$OutputRoot = ".\artifacts\real-session"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$root = Join-Path $OutputRoot $stamp
$data = Join-Path $root "data"
New-Item -ItemType Directory -Force -Path $data | Out-Null
$manifestPath = Join-Path $root "capture-manifest.txt"
$donePath = Join-Path $root "capture-done.txt"
$distPath = (Resolve-Path $Dist).Path
$rootPath = (Resolve-Path $root).Path
$dataPath = (Resolve-Path $data).Path

$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
$workBefore = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Bottom
$rects = @{}
$captureErrors = New-Object System.Collections.Generic.List[string]
$process = $null

function Capture-Rect([string]$key, [string]$file, [int]$pad = 14) {
    if (-not $script:rects.ContainsKey($key)) {
        $script:captureErrors.Add("missing rect: $key")
        return
    }

    $r = $script:rects[$key]
    $left = [Math]::Max($script:screen.Left, $r.L - $pad)
    $top = [Math]::Max($script:screen.Top, $r.T - $pad)
    $right = [Math]::Min($script:screen.Right, $r.R + $pad)
    $bottom = [Math]::Min($script:screen.Bottom, $r.B + $pad)
    $width = $right - $left
    $height = $bottom - $top
    if ($width -lt 2 -or $height -lt 2) {
        $script:captureErrors.Add("empty rect: $key")
        return
    }

    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $script:rootPath $file), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    $env:NEBULA_DATA_DIR = $dataPath
    $env:NEBULA_CAPTURE_DIR = $rootPath

    $process = Start-Process -FilePath $distPath -ArgumentList "--capture-demo" -PassThru
    $deadline = (Get-Date).AddSeconds(70)
    while (-not (Test-Path $manifestPath) -and (Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            throw "Nebula è terminata prima della manifest (exit $($process.ExitCode))."
        }
        Start-Sleep -Milliseconds 300
    }

    if (-not (Test-Path $manifestPath)) {
        throw "Timeout in attesa della manifest di cattura."
    }

    Get-Content $manifestPath | ForEach-Object {
        if ($_ -match '^([A-Z0-9]+)=(-?\d+),(-?\d+),(-?\d+),(-?\d+)$') {
            $rects[$matches[1]] = @{
                L = [int]$matches[2]
                T = [int]$matches[3]
                R = [int]$matches[4]
                B = [int]$matches[5]
            }
        }
    }

    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new(20, 400)
    Start-Sleep -Milliseconds 450
    Capture-Rect "ISLAND" "island-expanded.png" 16

    $tabKeys = @($rects.Keys | Where-Object { $_ -like "TAB*" } | Sort-Object)
    for ($i = 0; $i -lt $tabKeys.Count; $i++) {
        Capture-Rect $tabKeys[$i] ("liquid-tabs-{0}.png" -f $i) 18
    }

    $full = [System.Drawing.Bitmap]::new($screen.Width, $screen.Height)
    $fullGraphics = [System.Drawing.Graphics]::FromImage($full)
    try {
        $fullGraphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $full.Size)
        $full.Save((Join-Path $rootPath "full-screen.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $fullGraphics.Dispose()
        $full.Dispose()
    }

    if ($rects.ContainsKey("DOCK")) {
        $dock = $rects["DOCK"]
        $y = [int](($dock.T + $dock.B) / 2)
        $leftX = [int]($dock.L + ($dock.R - $dock.L) * 0.28)
        $rightX = [int]($dock.L + ($dock.R - $dock.L) * 0.72)

        [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($leftX, $y)
        Start-Sleep -Milliseconds 500
        Capture-Rect "DOCK" "bar-sheen-left.png" 10

        [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new($rightX, $y)
        Start-Sleep -Milliseconds 500
        Capture-Rect "DOCK" "bar-sheen-right.png" 10
    }

    New-Item -ItemType File -Force -Path $donePath | Out-Null
    if (-not $process.WaitForExit(45000)) {
        $captureErrors.Add("Nebula non è uscita entro 45 s dopo la cattura")
    }
}
catch {
    $captureErrors.Add($_.Exception.Message)
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(8000) | Out-Null } catch { }
    }

    Remove-Item Env:NEBULA_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:NEBULA_CAPTURE_DIR -ErrorAction SilentlyContinue
}

$workAfter = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Bottom
if ($workAfter -ne $workBefore) {
    for ($i = 0; $i -lt 20 -and $workAfter -ne $workBefore; $i++) {
        Start-Sleep -Milliseconds 500
        $workAfter = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Bottom
    }
}

$nebulaProcesses = @(Get-Process -Name "Nebula" -ErrorAction SilentlyContinue)
$watchdogProcesses = @(Get-Process -Name "Nebula.Watchdog" -ErrorAction SilentlyContinue)
$pendingPath = Join-Path $env:APPDATA "Nebula\taskbar.pending"

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("REAL_SESSION=$stamp")
$lines.Add("WORK_AREA_BEFORE=$workBefore")
$lines.Add("WORK_AREA_AFTER=$workAfter")
$lines.Add("NEBULA_PROCESSES=$($nebulaProcesses.Count)")
$lines.Add("WATCHDOG_PROCESSES=$($watchdogProcesses.Count)")
$lines.Add("PENDING=" + (Test-Path $pendingPath))
$lines.Add("CAPTURE_ERRORS=$($captureErrors.Count)")
foreach ($errorText in $captureErrors) {
    $lines.Add("ERROR=$errorText")
}
if (Test-Path $manifestPath) {
    Get-Content $manifestPath | ForEach-Object {
        if ($_ -match '^(STYLE|TABS_STYLE|ISLAND_STYLE|TAB_GROUPS|ISLAND_EXPANDED)=') {
            $lines.Add($_)
        }
    }
}
$lines | Set-Content (Join-Path $root "session.txt")

Write-Host "CAPTURE_ROOT=$root"
Write-Host "WORK_AREA=$workBefore->$workAfter"
Write-Host "NEBULA_PROCESSES=$($nebulaProcesses.Count)"
Write-Host "WATCHDOG_PROCESSES=$($watchdogProcesses.Count)"
Write-Host "PENDING=$(Test-Path $pendingPath)"
Write-Host "CAPTURE_ERRORS=$($captureErrors.Count)"
Get-ChildItem $root -Filter "*.png" | Select-Object Name, Length | Format-Table -AutoSize
