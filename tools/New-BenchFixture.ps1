<#
.SYNOPSIS
    Builds the frozen clipboard history the open-latency harness measures against.

.DESCRIPTION
    A timing run has to do the same work every time, and a real clipboard history does not hold
    still — it grows while you work, and whatever was copied last decides which preview the palette
    opens on. This writes a fixed history to its own Clip root (CLIP_ROOT) and leaves it alone
    afterwards, so a run today compares to a run tomorrow.

    Sizes are chosen to match a real history rather than a convenient one: a few hundred items, most
    of them text, with one file of each kind the preview pane treats differently.

    Rebuild it only when the shape being measured should change. Rebuilding between an optimization
    and its re-measurement invalidates the comparison.
#>
[CmdletBinding()]
param(
    [string]$FixtureRoot = (Join-Path $env:LOCALAPPDATA "Clip-bench-fixture"),
    [int]$TextItems = 140,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$watcher = Join-Path $root "src\Clip.Watcher\bin\Release\net8.0-windows10.0.19041.0\Clip.Watcher.exe"
if (-not (Test-Path $watcher)) {
    throw "Clip.Watcher.exe not found at $watcher. Run: dotnet build Clip.sln -c Release"
}

if (Test-Path $FixtureRoot) {
    if (-not $Force) {
        Write-Host "Fixture already exists at $FixtureRoot (use -Force to rebuild)."
        return
    }

    Remove-Item -LiteralPath $FixtureRoot -Recurse -Force
}

$assets = Join-Path $FixtureRoot "bench-assets"
New-Item -ItemType Directory -Path $assets -Force | Out-Null

# Every file the preview pane has a separate branch for. Content is generated rather than copied
# from the machine so the fixture is reproducible on any checkout.
$codeFile = Join-Path $assets "sample.cs"
$textFile = Join-Path $assets "sample.txt"
$htmlFile = Join-Path $assets "sample.html"
$pngFile  = Join-Path $assets "sample.png"

$code = @()
$code += "using System;"
$code += "namespace Bench;"
$code += "public static class Sample {"
foreach ($i in 1..120) {
    $code += "    public static int Value$i() => $i * 3 + 1;"
}
$code += "}"
Set-Content -LiteralPath $codeFile -Value ($code -join "`r`n") -Encoding utf8

$lines = foreach ($i in 1..400) { "line $i - the quick brown fox jumps over the lazy dog $i" }
Set-Content -LiteralPath $textFile -Value ($lines -join "`r`n") -Encoding utf8

$html = @()
$html += "<html><body><h1>Bench fixture</h1><ul>"
foreach ($i in 1..200) { $html += "<li>item $i</li>" }
$html += "</ul></body></html>"
Set-Content -LiteralPath $htmlFile -Value ($html -join "`r`n") -Encoding utf8

# A screenshot-sized PNG, which is what the image preview actually spends its time decoding.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 2560, 1440
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([System.Drawing.Color]::FromArgb(32, 40, 56))
for ($i = 0; $i -lt 2560; $i += 64) {
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, ($i % 255), 128, 200))
    $graphics.FillRectangle($brush, $i, ($i % 1200), 56, 180)
    $brush.Dispose()
}
$graphics.Dispose()
$bitmap.Save($pngFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Host "Seeding fixture history at $FixtureRoot ..."
$env:CLIP_ROOT = $FixtureRoot

function Add-Item([string[]]$CommandArgs) {
    $out = & $watcher @CommandArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "seed failed: $($CommandArgs -join ' ') -> $out"
    }
}

# Prove the redirect took before writing a hundred more. CLIP_ROOT is honoured by settings and the
# store, but a build that missed one of those paths would quietly seed the real clipboard history
# instead, and the only sign would be a hundred junk rows in somebody's palette.
$fixtureIndex = Join-Path $FixtureRoot "Clipboard History\history.index.json"
Add-Item @("perf-add-text", "bench fixture probe")
if (-not (Test-Path $fixtureIndex)) {
    Remove-Item Env:\CLIP_ROOT
    throw "CLIP_ROOT was ignored: nothing was written to $fixtureIndex. The seed went somewhere else - check ClipStoragePaths.Root is used by settings and the store, and clean up before retrying."
}

# Bulk text, oldest first, so the newest items are the interesting ones and the list has a
# realistic length to render and scroll.
foreach ($i in 1..$TextItems) {
    Add-Item @("perf-add-text", "bench text item $i - lorem ipsum dolor sit amet consectetur adipiscing elit $i")
}

Add-Item @("perf-add-file", $codeFile)
Add-Item @("perf-add-file", $textFile)
Add-Item @("perf-add-file", $htmlFile)
Add-Item @("perf-add-file", $pngFile)
Add-Item @("perf-add-image", $pngFile)

Remove-Item Env:\CLIP_ROOT

$index = Join-Path $FixtureRoot "Clipboard History\history.index.json"
$count = if (Test-Path $index) { (Get-Content $index -Raw | ConvertFrom-Json).Count } else { 0 }
Write-Host "Fixture ready: $count items at $FixtureRoot"
Write-Host "Missing on purpose: media/pdf/xlsx/word need real files - pass them with -Force after dropping them in $assets"
