param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$NoZip,
    [switch]$NoInstaller
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
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -o $publishDir

# AssemblyName=Clip in Clip.Shell.csproj, so publish already produces Clip.exe + Clip.dll.

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

if (-not $NoInstaller) {
    $iscc = @(
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $iscc) {
        Write-Warning "Inno Setup (ISCC.exe) not found. Skipping installer build. Install from https://jrsoftware.org/isdl.php or run with -NoInstaller."
    }
    else {
        $issPath = Join-Path $root "installer\Clip.iss"
        & $iscc $issPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compile failed (exit $LASTEXITCODE)."
        }

        $setupExe = Join-Path $publishRoot "Clip-Setup.exe"
        if (Test-Path $setupExe) {
            Write-Output "Created $setupExe"
        }
    }
}

Write-Output "Published to $publishDir"
