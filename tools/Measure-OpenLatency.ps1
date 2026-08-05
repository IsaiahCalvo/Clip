<#
.SYNOPSIS
    Times how long each Clip page takes to open, cold and warm, and reports median and p95.

.DESCRIPTION
    Drives src/Clip.Shell/OpenBenchHarness.cs. Every run is measured off screen against a frozen
    fixture history (tools/New-BenchFixture.ps1), so two runs on different days compare.

    Cold and warm are different questions and are measured differently:

      cold - the first open after the process started, so one fresh process per sample. This is what
             the first Alt+V after a reboot costs.
      warm - opens two and after in one process, with the list marked stale as though a clip had
             arrived in between, which is why the palette is usually being opened at all.

    Reports the median and the p95 rather than a best-of: the number worth fixing is the open that
    felt slow, and an average hides it. Every sample is kept in the JSON.

.EXAMPLE
    ./tools/Measure-OpenLatency.ps1 -Label baseline
    ./tools/Measure-OpenLatency.ps1 -Label after-focus-fix -Pages palette
#>
[CmdletBinding()]
param(
    [string[]]$Pages = @("palette", "preview-text", "preview-image", "preview-code", "preview-html", "preview-textfile", "settings"),
    [int]$ColdRuns = 5,
    [int]$WarmRuns = 10,
    [string]$Label = "run",
    [string]$FixtureRoot = (Join-Path $env:LOCALAPPDATA "Clip-bench-fixture"),
    [string]$OutDir = "",
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $root "src\Clip.Shell\bin\Release\net8.0-windows10.0.19041.0\Clip.exe"
}
if (-not (Test-Path $ExePath)) {
    throw "Clip.exe not found at $ExePath. Run: dotnet build Clip.sln -c Release"
}
if (-not (Test-Path (Join-Path $FixtureRoot "Clipboard History\history.index.json"))) {
    throw "No fixture history at $FixtureRoot. Run: ./tools/New-BenchFixture.ps1"
}
if (-not $OutDir) {
    $OutDir = Join-Path $root ".claudehelper\perf"
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = [double[]]($Values | Sort-Object)
    if ($sorted.Count -eq 1) { return [math]::Round($sorted[0], 1) }
    $rank = ($Percentile / 100.0) * ($sorted.Count - 1)
    $low = [math]::Floor($rank)
    $high = [math]::Ceiling($rank)
    $value = if ($low -eq $high) { $sorted[$low] } else { $sorted[$low] + (($sorted[$high] - $sorted[$low]) * ($rank - $low)) }
    return [math]::Round($value, 1)
}

function Invoke-Bench([string]$Page, [int]$Runs) {
    $out = Join-Path $env:TEMP ("clip-bench-" + [guid]::NewGuid().ToString("N") + ".json")
    try {
        $arguments = @("--open-bench", "--page=$Page", "--runs=$Runs", "--out=$out")
        $process = Start-Process -FilePath $ExePath -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(180000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw "bench for $Page did not finish within 180s"
        }
        if (-not (Test-Path $out)) {
            throw "bench for $Page produced no result file (exit $($process.ExitCode))"
        }
        return (Get-Content $out -Raw | ConvertFrom-Json).samples
    }
    finally {
        Remove-Item $out -ErrorAction SilentlyContinue
    }
}

# The measured process reads its history from here. Set for this shell only; children inherit it.
$env:CLIP_ROOT = $FixtureRoot

$locked = $null -ne (Get-Process LogonUI -ErrorAction SilentlyContinue)
$results = @()

foreach ($page in $Pages) {
    Write-Host ""
    Write-Host "=== $page" -ForegroundColor Cyan

    # Cold: one process per sample, because "cold" means the first open the process ever does.
    $cold = @()
    for ($i = 0; $i -lt $ColdRuns; $i++) {
        $sample = @(Invoke-Bench $page 1)[0]
        if ($sample.OpenMs -ge 0) { $cold += [double]$sample.OpenMs }
        Write-Host ("  cold {0,2}: {1,7:F1} ms  (shown {2:F0}, rows {3:F0}, focus {4:F0})" -f `
            ($i + 1), $sample.OpenMs, $sample.Shown, $sample.RowsFirst, $sample.Focused)
    }

    # Warm: opens two and after in a single process; run one is the cold one and is dropped.
    $warmSamples = @(Invoke-Bench $page ($WarmRuns + 1))
    $warm = @()
    foreach ($sample in $warmSamples | Select-Object -Skip 1) {
        if ($sample.OpenMs -ge 0) { $warm += [double]$sample.OpenMs }
    }
    Write-Host ("  warm x{0}: {1}" -f $warm.Count, (($warm | ForEach-Object { "{0:F0}" -f $_ }) -join ", "))

    $ready = @($warmSamples | Where-Object { $null -ne $_.ReadyMs -and $_.ReadyMs -ge 0 } | ForEach-Object { [double]$_.ReadyMs })

    $results += [pscustomobject]@{
        Page          = $page
        ColdMedianMs  = Get-Percentile $cold 50
        ColdP95Ms     = Get-Percentile $cold 95
        WarmMedianMs  = Get-Percentile $warm 50
        WarmP95Ms     = Get-Percentile $warm 95
        ReadyMedianMs = Get-Percentile $ready 50
        ColdSamples   = $cold
        WarmSamples   = $warm
        ReadySamples  = $ready
    }
}

Remove-Item Env:\CLIP_ROOT

$payload = [pscustomobject]@{
    Label        = $Label
    TakenUtc     = (Get-Date).ToUniversalTime().ToString("o")
    Commit       = (& git -C $root rev-parse --short HEAD 2>$null)
    Exe          = $ExePath
    FixtureRoot  = $FixtureRoot
    SessionLocked = $locked
    ColdRuns     = $ColdRuns
    WarmRuns     = $WarmRuns
    Results      = $results
}

$jsonPath = Join-Path $OutDir "$Label.json"
$payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding utf8

Write-Host ""
Write-Host "== $Label ==" -ForegroundColor Green
$results |
    Select-Object Page,
        @{n = 'cold med'; e = { $_.ColdMedianMs }},
        @{n = 'cold p95'; e = { $_.ColdP95Ms }},
        @{n = 'warm med'; e = { $_.WarmMedianMs }},
        @{n = 'warm p95'; e = { $_.WarmP95Ms }},
        @{n = 'page ready med'; e = { $_.ReadyMedianMs }} |
    Format-Table -AutoSize

Write-Host "written to $jsonPath"
