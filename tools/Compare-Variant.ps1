<#
.SYNOPSIS
    A/B two variants of one build by alternating between them, for a machine that will not hold still.

.DESCRIPTION
    Comparing a run taken now against a run taken an hour ago is worthless on a real desktop. During
    this work a background file sync and an updater each took a whole core, and an unchanged build
    measured 60% slower than it had earlier - which was very nearly written up as a regression
    caused by a code change.

    So both arms run alternately, A B A B, within the same few minutes, and only the paired medians
    are compared. Whatever the machine is doing, it is doing it to both.

    The variant is selected by an environment variable the shell reads, so one build serves both
    arms and no build difference can creep in.

.EXAMPLE
    ./tools/Compare-Variant.ps1 -EnvVar CLIP_WARM_WEBVIEW -Page palette -Rounds 5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EnvVar,
    [string]$Page = "palette",
    [int]$Rounds = 5,
    [int]$RunsPerRound = 6,
    [string]$FixtureRoot = (Join-Path $env:LOCALAPPDATA "Clip-bench-fixture"),
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) {
    $ExePath = Join-Path $root "src\Clip.Shell\bin\Release\net8.0-windows10.0.19041.0\Clip.exe"
}

function Get-Median([double[]]$v) {
    if (-not $v -or $v.Count -eq 0) { return $null }
    $s = [double[]]($v | Sort-Object)
    $m = [int][math]::Floor($s.Count / 2)
    $val = if ($s.Count % 2 -eq 1) { $s[$m] } else { ($s[$m - 1] + $s[$m]) / 2 }
    return [math]::Round($val, 1)
}

function Invoke-Arm([string]$Value) {
    $out = Join-Path $env:TEMP ("clip-ab-" + [guid]::NewGuid().ToString("N") + ".json")
    try {
        $env:CLIP_ROOT = $FixtureRoot
        if ($Value -eq "") { Remove-Item Env:\$EnvVar -ErrorAction SilentlyContinue }
        else { Set-Item -Path "Env:\$EnvVar" -Value $Value }

        $p = Start-Process -FilePath $ExePath -ArgumentList @("--open-bench", "--page=$Page", "--runs=$RunsPerRound", "--out=$out") -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(180000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; throw "arm did not finish" }
        if (-not (Test-Path $out)) { throw "no result file" }

        # Drop run 0: it is the cold one, and this compares the steady state.
        $samples = @((Get-Content $out -Raw | ConvertFrom-Json).samples | Select-Object -Skip 1)
        return @($samples | Where-Object { $_.OpenMs -ge 0 } | ForEach-Object { [double]$_.OpenMs })
    }
    finally {
        Remove-Item $out -ErrorAction SilentlyContinue
        Remove-Item Env:\$EnvVar -ErrorAction SilentlyContinue
    }
}

$off = @(); $on = @()
for ($r = 1; $r -le $Rounds; $r++) {
    $a = Invoke-Arm ""
    $b = Invoke-Arm "1"
    $off += $a
    $on += $b
    Write-Host ("round {0}: off median {1,6} ms   on median {2,6} ms" -f $r, (Get-Median $a), (Get-Median $b))
}

Remove-Item Env:\CLIP_ROOT -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("$EnvVar off : median {0} ms over {1} samples" -f (Get-Median $off), $off.Count)
Write-Host ("$EnvVar on  : median {0} ms over {1} samples" -f (Get-Median $on), $on.Count)
$delta = (Get-Median $on) - (Get-Median $off)
Write-Host ("delta       : {0}{1} ms with it on" -f $(if ($delta -ge 0) { "+" } else { "" }), [math]::Round($delta, 1))
