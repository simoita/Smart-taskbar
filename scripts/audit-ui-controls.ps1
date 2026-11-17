param(
    [string]$Output = "artifacts/selftest/control-audit.txt"
)

$root = Split-Path -Parent $PSScriptRoot
$settingsXaml = Join-Path $root "src\Nebula\Views\SettingsWindow.xaml"
$settingsCode = Join-Path $root "src\Nebula\Views\SettingsWindow.xaml.cs"
$iconsXaml = Join-Path $root "src\Nebula\Views\SystemIconsPage.xaml"
$iconsCode = Join-Path $root "src\Nebula\Views\SystemIconsPage.xaml.cs"

$eventNames = @('Click', 'Checked', 'Unchecked', 'MouseLeftButtonDown', 'MouseLeftButtonUp',
    'MouseDoubleClick', 'TextChanged', 'SelectionChanged', 'ValueChanged', 'Drop', 'DragOver',
    'KeyDown', 'GotFocus', 'LostFocus', 'SizeChanged', 'Loaded')

$script:AuditLines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$line) { $script:AuditLines.Add($line) }

function Audit-Xaml([string]$xamlPath, [string]$codePath, [string]$label) {
    $lines = Get-Content $xamlPath
    $code = Get-Content $codePath -Raw
    $page = "TitleBar/Navigazione"
    $bindings = New-Object System.Collections.Generic.List[object]
    $handlers = New-Object System.Collections.Generic.List[object]

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'x:Name="(Page[A-Za-z]+)"') { $page = $Matches[1] }
        foreach ($m in [regex]::Matches($lines[$i], 'Binding Settings\.([A-Za-z0-9_]+)')) {
            $bindings.Add([pscustomobject]@{
                Setting = $m.Groups[1].Value
                Page = $page
                Line = $i + 1
                TwoWay = $lines[$i] -match 'Mode=TwoWay'
                Text = $lines[$i].Trim()
            })
        }
        foreach ($m in [regex]::Matches($lines[$i], '(?<event>' + ($eventNames -join '|') + ')="(?<handler>[A-Za-z0-9_]+)"')) {
            $handlers.Add([pscustomobject]@{
                Event = $m.Groups['event'].Value
                Handler = $m.Groups['handler'].Value
                Page = $page
                Line = $i + 1
            })
        }
    }

    Add-Line "=== $label ==="
    Add-Line "xaml: $xamlPath"
    $uniqueHandlers = @($handlers | Select-Object -ExpandProperty Handler -Unique)
    $missing = @()
    foreach ($handler in $uniqueHandlers) {
        if ($code -notmatch ('\b' + [regex]::Escape($handler) + '\s*\(')) { $missing += $handler }
    }

    $editorGroups = $bindings | Where-Object TwoWay | Group-Object Setting
    $multiPage = @()
    foreach ($g in $editorGroups) {
        $pages = @($g.Group | Select-Object -ExpandProperty Page -Unique)
        if ($pages.Count -gt 1) {
            $multiPage += [pscustomobject]@{ Setting = $g.Name; Pages = ($pages -join ', ') }
        }
    }

    $samePage = $bindings | Group-Object Setting, Page | Where-Object Count -gt 1
    $samePageSummary = @()
    foreach ($g in $samePage) {
        $samePageSummary += [pscustomobject]@{
            Setting = $g.Group[0].Setting
            Page = $g.Group[0].Page
            Total = $g.Count
            Editors = @($g.Group | Where-Object TwoWay).Count
            ValueLabels = @($g.Group | Where-Object { -not $_.TwoWay }).Count
        }
    }

    Add-Line "settings_bindings: $($bindings.Count)"
    Add-Line "settings_editors_twoway: $(($bindings | Where-Object TwoWay).Count)"
    Add-Line "event_attributes: $($handlers.Count)"
    Add-Line "unique_handlers: $($uniqueHandlers.Count)"
    Add-Line "missing_handlers: $($missing.Count)"
    if ($missing.Count -gt 0) { Add-Line "MISSING: $($missing -join ', ')" }
    Add-Line "settings_with_editors_on_multiple_pages: $($multiPage.Count)"
    foreach ($d in $multiPage) { Add-Line "MULTI_PAGE: $($d.Setting) -> $($d.Pages)" }
    Add-Line "same_page_repeated_bindings: $($samePageSummary.Count)"
    foreach ($d in $samePageSummary) {
        Add-Line "  $($d.Page) / $($d.Setting): total=$($d.Total), editors=$($d.Editors), value_labels=$($d.ValueLabels)"
    }
    Add-Line ""
    Add-Line "-- handlers --"
    foreach ($h in $handlers) { Add-Line ("  {0,-18} {1,-22} line {2}" -f $h.Page, ($h.Event + '=' + $h.Handler), $h.Line) }
    Add-Line ""
    Add-Line "-- settings by page --"
    foreach ($g in ($bindings | Group-Object Page)) {
        $names = ($g.Group | Select-Object -ExpandProperty Setting -Unique) -join ', '
        Add-Line "  $($g.Name): $names"
    }
    Add-Line ""

    return ($missing.Count -eq 0 -and $multiPage.Count -eq 0)
}

New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null
$report = New-Object System.Collections.Generic.List[string]
$report.Add("Nebula UI control audit - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$report.Add("Static audit of every settings binding and event handler in the Studio and system-icons page.")
$report.Add("A setting editor (Mode=TwoWay) may appear on exactly one page.")
$report.Add("Repeated same-page bindings are allowed only as read-only value labels next to their slider.")
$report.Add("")

$ok = $true
$studio = Audit-Xaml $settingsXaml $settingsCode "Studio (SettingsWindow + all pages)"
$report.AddRange($script:AuditLines)
$script:AuditLines.Clear()
if (-not $studio) { $ok = $false }

$icons = Audit-Xaml $iconsXaml $iconsCode "System icons page"
$report.AddRange($script:AuditLines)
$script:AuditLines.Clear()
if (-not $icons) { $ok = $false }

$report.Add("RESULT: " + $(if ($ok) { "PASS" } else { "FAIL" }))
$report | Set-Content $Output -Encoding UTF8
$report | ForEach-Object { Write-Output $_ }
if (-not $ok) { exit 1 }
