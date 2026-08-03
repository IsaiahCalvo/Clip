<#
.SYNOPSIS
Fetches the video decoder Clip's picture-in-picture player uses.

.DESCRIPTION
The decoder is a set of native libraries, too large to keep in the repository, so the build fetches
them instead. Only the five needed to play a file are kept; the filter, capture-device and
post-processing libraries in the same archive are another 36 MB and Clip asks for none of them.

The version matters and is not cosmetic. FlyleafLib is compiled against a specific FFmpeg major
version and a mismatched pair fails at run time rather than at build time — Clip then falls back to
the browser player, quietly, which is easy to mistake for the decoder simply being poor. If
FlyleafLib is upgraded, check which FFmpeg it now expects and change $Version to match.

Run once after cloning. Already-present files are left alone unless -Force is given.
#>
[CmdletBinding()]
param(
    # FlyleafLib 3.10.4 binds to FFmpeg 7.1 (avcodec 61, avutil 59, avformat 61).
    [string] $Version = 'n7.1',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$destination = Join-Path $PSScriptRoot 'src\Clip.Shell\FFmpeg'
$needed = @(
    'avcodec-61.dll',
    'avformat-61.dll',
    'avutil-59.dll',
    'swresample-5.dll',
    'swscale-8.dll'
)

$missing = $needed | Where-Object { -not (Test-Path (Join-Path $destination $_)) }
if (-not $Force -and $missing.Count -eq 0) {
    Write-Host "Decoder already present in $destination."
    return
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "clip-ffmpeg-$Version"
$archive = Join-Path $staging 'ffmpeg.zip'
New-Item -ItemType Directory -Force $staging | Out-Null

if ($Force -or -not (Test-Path $archive)) {
    $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-$Version-latest-win64-gpl-shared-$($Version.TrimStart('n')).zip"
    Write-Host "Downloading decoder $Version..."
    Invoke-WebRequest $url -OutFile $archive -UseBasicParsing
}

Expand-Archive $archive (Join-Path $staging 'unpacked') -Force

New-Item -ItemType Directory -Force $destination | Out-Null
foreach ($file in $needed) {
    $found = Get-ChildItem (Join-Path $staging 'unpacked') -Recurse -Filter $file | Select-Object -First 1
    if (-not $found) {
        throw "The decoder archive did not contain $file. Check that FFmpeg $Version is what FlyleafLib expects."
    }

    Copy-Item $found.FullName $destination -Force
}

$size = (Get-ChildItem $destination | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Decoder ready in {0} ({1:N0} MB)." -f $destination, $size)
