param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0.0",
    [string]$Publisher = "CN=Clip",
    [switch]$IncludeSymbols,
    [switch]$NoTrim,
    [switch]$NoSign
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$projectDir = Join-Path $root "src\Clip.CommandPalette"
$project = Join-Path $projectDir "Clip.CommandPalette.csproj"
$packageRoot = Join-Path $root "artifacts\command-palette"
$publishDir = Join-Path $packageRoot "package"
$outputDir = Join-Path $packageRoot "msix"
$msixPath = Join-Path $outputDir "Clip.CommandPalette_$Version.msix"

function Find-WindowsKitTool([string]$Name) {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $kitsRoot)) {
        throw "Windows Kit bin folder was not found: $kitsRoot"
    }

    $tool = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\$([regex]::Escape($Name))$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $tool) {
        throw "$Name was not found under $kitsRoot"
    }

    return $tool.FullName
}

function Ensure-DevCertificate([string]$Subject) {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if (-not $cert) {
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $Subject `
            -CertStoreLocation Cert:\CurrentUser\My `
            -KeyUsage DigitalSignature `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -NotAfter (Get-Date).AddYears(3)
    }

    return $cert
}

New-Item -ItemType Directory -Force -Path $packageRoot, $outputDir | Out-Null
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$publishTrimmed = (-not $NoTrim).ToString().ToLowerInvariant()

& $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=$publishTrimmed `
    -p:TrimMode=partial `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not $IncludeSymbols) {
    Get-ChildItem -LiteralPath $publishDir -Recurse -Filter *.pdb -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

$removableFiles = @(
    "createdump.exe",
    "clretwrc.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll",
    "mscordaccore.dll",
    "mscordaccore_*.dll",
    "mscordbi.dll",
    "sos.dll",
    "SOS.NETCore.dll"
)
Get-ChildItem -LiteralPath $publishDir -File -ErrorAction SilentlyContinue |
    Where-Object {
        $name = $_.Name
        $removableFiles | Where-Object { $name -like $_ }
    } |
    Remove-Item -Force

$manifestSource = Join-Path $projectDir "Package.appxmanifest"
$manifestTarget = Join-Path $publishDir "AppxManifest.xml"
$manifest = Get-Content -LiteralPath $manifestSource -Raw
$manifest = $manifest -creplace '(<Identity\b[^>]*\sPublisher=")[^"]+(")', "`${1}$Publisher`${2}"
$manifest = $manifest -creplace '(<Identity\b[^>]*\sVersion=")[^"]+(")', "`${1}$Version`${2}"
$manifest = $manifest.Replace('$targetnametoken$.exe', 'Clip.CommandPalette.exe')
$manifest = $manifest.Replace('$targetentrypoint$', 'Windows.FullTrustApplication')
Set-Content -LiteralPath $manifestTarget -Value $manifest -Encoding UTF8

$assetsSource = Join-Path $projectDir "Assets"
$assetsTarget = Join-Path $publishDir "Assets"
New-Item -ItemType Directory -Force -Path $assetsTarget | Out-Null
Copy-Item -LiteralPath (Join-Path $assetsSource "*") -Destination $assetsTarget -Force

if (Test-Path $msixPath) {
    Remove-Item -LiteralPath $msixPath -Force
}

$makeAppx = Find-WindowsKitTool "makeappx.exe"
& $makeAppx pack /d $publishDir /p $msixPath /o | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "makeappx failed with exit code $LASTEXITCODE."
}

if (-not $NoSign) {
    $cert = Ensure-DevCertificate $Publisher
    $signTool = Find-WindowsKitTool "signtool.exe"
    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $msixPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE."
    }
}

Write-Output "Created $msixPath"
