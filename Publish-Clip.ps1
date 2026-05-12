param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishRoot = Join-Path $root "artifacts\publish"
$publishDir = Join-Path $publishRoot "Clip-$Runtime"
$zipPath = Join-Path $publishRoot "Clip-$Runtime.zip"

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish (Join-Path $root "src\Clip.Shell\Clip.Shell.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir

$shellExe = Join-Path $publishDir "Clip.Shell.exe"
$appExe = Join-Path $publishDir "Clip.exe"
if (Test-Path $shellExe) {
    Copy-Item $shellExe $appExe -Force
    Remove-Item -LiteralPath $shellExe -Force
}

Copy-Item (Join-Path $root "README.md") $publishDir -Force
Copy-Item (Join-Path $root "LICENSE") $publishDir -Force
Copy-Item (Join-Path $root "PRIVACY.md") $publishDir -Force
Copy-Item (Join-Path $root "Start-Clip.ps1") $publishDir -Force
Copy-Item (Join-Path $root "Install-ClipStartup.ps1") $publishDir -Force

Get-ChildItem -Path $publishDir -Recurse -Include *.pdb,*.xml | Remove-Item -Force

if (-not $NoZip) {
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
    Write-Output "Created $zipPath"
}

Write-Output "Published to $publishDir"
