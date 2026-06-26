param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [switch]$FrameworkDependent,
    [switch]$NoZip,
    [switch]$NoInstaller,
    [switch]$UseNativeLauncher,
    [switch]$RequireNativeLauncher,
    [switch]$NoNativeLauncher,
    [switch]$NoNetFxLauncher
)

if (-not $Version) {
    $tag = $env:GITHUB_REF_NAME
    if ($tag -match '^v?(\d+\.\d+\.\d+)') {
        $Version = $Matches[1]
    } else {
        # Default above the latest published GitHub release so local builds don't perpetually
        # prompt to "update" to an older release.
        $Version = "1.1.0"
    }
}
$deploymentMode = if ($FrameworkDependent) { "framework-dependent" } else { "self-contained" }
Write-Output "Building Clip $Version ($deploymentMode)"

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishRoot = Join-Path $root "artifacts\publish"
$publishName = if ($FrameworkDependent) { "Clip-$Runtime-framework-dependent" } else { "Clip-$Runtime" }
$publishDir = Join-Path $publishRoot $publishName
$zipPath = Join-Path $publishRoot "$publishName.zip"

if ($FrameworkDependent -and -not $NoInstaller) {
    Write-Warning "Framework-dependent publish requires the .NET Desktop Runtime and is emitted as a folder/zip only. Skipping installer."
    $NoInstaller = $true
}

if ($RequireNativeLauncher) {
    $UseNativeLauncher = $true
}

if ($NoNativeLauncher -and ($UseNativeLauncher -or $RequireNativeLauncher)) {
    throw "-NoNativeLauncher cannot be combined with -UseNativeLauncher or -RequireNativeLauncher."
}

function Copy-PublishedFiles {
    param(
        [string]$SourceDir,
        [string]$DestinationDir,
        [string[]]$Names
    )

    foreach ($name in $Names) {
        $source = Join-Path $SourceDir $name
        if (Test-Path $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $DestinationDir $name) -Force
        }
    }
}

function Test-NativeAotToolchain {
    if (Get-Command link.exe -ErrorAction SilentlyContinue) {
        return $true
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        return $false
    }

    $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    return -not [string]::IsNullOrWhiteSpace($installPath)
}

function Get-NetFxCscPath {
    $candidates = @(
        (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
        (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
    )

    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Remove-ManagedLauncherSidecars {
    param([string]$DestinationDir)

    foreach ($name in @("Clip.Launcher.dll", "Clip.Launcher.deps.json", "Clip.Launcher.runtimeconfig.json")) {
        $managedSidecar = Join-Path $DestinationDir $name
        if (Test-Path $managedSidecar) {
            Remove-Item -LiteralPath $managedSidecar -Force
        }
    }
}

if (-not $NoNativeLauncher -and -not $UseNativeLauncher -and (Test-NativeAotToolchain)) {
    $UseNativeLauncher = $true
    Write-Output "Native launcher toolchain detected; publishing native launcher."
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

$selfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()
dotnet publish (Join-Path $root "src\Clip.Shell\Clip.Shell.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContained `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o $publishDir

$windowsHistoryPublishDir = Join-Path $publishRoot "_windows-history"
if (Test-Path $windowsHistoryPublishDir) {
    Remove-Item -LiteralPath $windowsHistoryPublishDir -Recurse -Force
}

dotnet publish (Join-Path $root "src\Clip.WindowsHistory\Clip.WindowsHistory.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContained `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o $windowsHistoryPublishDir

Get-ChildItem -LiteralPath $windowsHistoryPublishDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "Clip.WindowsHistory.*" -or $_.Name -eq "Microsoft.Windows.SDK.NET.dll" -or $_.Name -eq "WinRT.Runtime.dll" } |
    Copy-Item -Destination $publishDir -Force

Remove-Item -LiteralPath $windowsHistoryPublishDir -Recurse -Force

$launcherPublishDir = Join-Path $publishRoot "_launcher"
if (Test-Path $launcherPublishDir) {
    Remove-Item -LiteralPath $launcherPublishDir -Recurse -Force
}

dotnet publish (Join-Path $root "src\Clip.Launcher\Clip.Launcher.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContained `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version `
    -p:FileVersion=$Version `
    -p:InformationalVersion=$Version `
    -o $launcherPublishDir

foreach ($name in @("Clip.Launcher.exe", "Clip.Launcher.dll", "Clip.Launcher.deps.json", "Clip.Launcher.runtimeconfig.json")) {
    $source = Join-Path $launcherPublishDir $name
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $publishDir $name) -Force
    }
}

Remove-Item -LiteralPath $launcherPublishDir -Recurse -Force

# AssemblyName=Clip in Clip.Shell.csproj, so publish already produces Clip.exe + Clip.dll.
# Keep Clip.Watcher.exe as the background host and Clip.Launcher.exe as the no-window shortcut path.

# The watcher is a hot path. ReadyToRun only for that binary gives
# a cold-start win without the package-size hit of compiling the full WPF shell.
if (-not $FrameworkDependent) {
    $watcherReadyToRunDir = Join-Path $publishRoot "_watcher-r2r"
    if (Test-Path $watcherReadyToRunDir) {
        Remove-Item -LiteralPath $watcherReadyToRunDir -Recurse -Force
    }

    dotnet publish (Join-Path $root "src\Clip.Watcher\Clip.Watcher.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:Version=$Version `
        -p:AssemblyVersion=$Version `
        -p:FileVersion=$Version `
        -p:InformationalVersion=$Version `
        -o $watcherReadyToRunDir

    foreach ($name in @("Clip.Watcher.exe", "Clip.Watcher.dll", "Clip.Watcher.deps.json", "Clip.Watcher.runtimeconfig.json", "Clip.Core.dll")) {
        $source = Join-Path $watcherReadyToRunDir $name
        if (Test-Path $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $publishDir $name) -Force
        }
    }

    Remove-Item -LiteralPath $watcherReadyToRunDir -Recurse -Force
}

# The launcher is the shortcut handoff path. Keep it native/tiny for both self-contained
# and framework-dependent downloads so the lightweight package does not feel slower.
$nativeLauncherPublished = $false
if ($UseNativeLauncher) {
    if (Test-NativeAotToolchain) {
        $launcherNativeDir = Join-Path $publishRoot "_launcher-native"
        if (Test-Path $launcherNativeDir) {
            Remove-Item -LiteralPath $launcherNativeDir -Recurse -Force
        }

        dotnet publish (Join-Path $root "src\Clip.Launcher\Clip.Launcher.csproj") `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -p:PublishAot=true `
            -p:StripSymbols=true `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -p:Version=$Version `
            -p:AssemblyVersion=$Version `
            -p:FileVersion=$Version `
            -p:InformationalVersion=$Version `
            -o $launcherNativeDir

        if ($LASTEXITCODE -ne 0) {
            if ($RequireNativeLauncher) {
                throw "Native launcher publish failed (exit $LASTEXITCODE)."
            }

            Write-Warning "Native launcher publish failed (exit $LASTEXITCODE). Falling back to ReadyToRun launcher."
        }
        else {
            Copy-PublishedFiles $launcherNativeDir $publishDir @("Clip.Launcher.exe")
            Remove-ManagedLauncherSidecars $publishDir

            $nativeLauncherPublished = $true
            Write-Output "Published native AOT launcher."
        }

        if (Test-Path $launcherNativeDir) {
            Remove-Item -LiteralPath $launcherNativeDir -Recurse -Force
        }
    }
    elseif ($RequireNativeLauncher) {
        throw "Native launcher requested, but the Visual Studio C++ linker toolchain was not found."
    }
    else {
        Write-Warning "Native launcher requested, but the Visual Studio C++ linker toolchain was not found. Falling back to ReadyToRun launcher."
    }
}

$netFxLauncherPublished = $false
if (-not $nativeLauncherPublished -and -not $NoNetFxLauncher) {
    $csc = Get-NetFxCscPath
    if ($csc) {
        $launcherNetFxExe = Join-Path $publishDir "Clip.Launcher.exe"
        & $csc /nologo /optimize+ /target:winexe /platform:x64 "/out:$launcherNetFxExe" (Join-Path $root "src\Clip.Launcher.NetFx\Program.cs")
        if ($LASTEXITCODE -eq 0) {
            Remove-ManagedLauncherSidecars $publishDir
            $netFxLauncherPublished = $true
            Write-Output "Published .NET Framework launcher."
        }
        else {
            Write-Warning ".NET Framework launcher compile failed (exit $LASTEXITCODE). Falling back to ReadyToRun launcher."
        }
    }
    else {
        Write-Warning ".NET Framework compiler was not found. Falling back to ReadyToRun launcher."
    }
}

if (-not $nativeLauncherPublished -and -not $netFxLauncherPublished) {
    $launcherReadyToRunDir = Join-Path $publishRoot "_launcher-r2r"
    if (Test-Path $launcherReadyToRunDir) {
        Remove-Item -LiteralPath $launcherReadyToRunDir -Recurse -Force
    }

    dotnet publish (Join-Path $root "src\Clip.Launcher\Clip.Launcher.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -p:AssemblyVersion=$Version `
        -p:FileVersion=$Version `
        -p:InformationalVersion=$Version `
        -o $launcherReadyToRunDir

    Copy-PublishedFiles $launcherReadyToRunDir $publishDir @("Clip.Launcher.exe", "Clip.Launcher.dll", "Clip.Launcher.deps.json", "Clip.Launcher.runtimeconfig.json")

    Remove-Item -LiteralPath $launcherReadyToRunDir -Recurse -Force
}

# Keep the downloadable app lean: Clip ships English UI text and does not need design-time
# assemblies or crash-debug helper binaries for normal user installs.
$satelliteResourceDirs = @(
    "cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "zh-Hans", "zh-Hant"
)
foreach ($dir in $satelliteResourceDirs) {
    $path = Join-Path $publishDir $dir
    if (Test-Path $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

$removableFiles = @(
    "System.Windows.Forms.Design.dll",
    "System.Windows.Forms.Design.Editors.dll",
    "System.Design.dll",
    "System.Drawing.Design.dll",
    "Microsoft.VisualBasic.dll",
    "Microsoft.VisualBasic.Core.dll",
    "Microsoft.VisualBasic.Forms.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll",
    "PresentationFramework.Aero.dll",
    "PresentationFramework.AeroLite.dll",
    "PresentationFramework.Classic.dll",
    "PresentationFramework.Luna.dll",
    "PresentationFramework.Royale.dll",
    "PresentationFramework-SystemCore.dll",
    "PresentationFramework-SystemData.dll",
    "PresentationFramework-SystemDrawing.dll",
    "PresentationFramework-SystemXmlLinq.dll",
    "ReachFramework.dll",
    "clretwrc.dll",
    "createdump.exe",
    "mscordaccore.dll",
    "mscordaccore_*.dll",
    "mscordbi.dll",
    "System.Diagnostics.EventLog.dll",
    "System.Diagnostics.EventLog.Messages.dll",
    "System.DirectoryServices.dll",
    "System.Linq.Parallel.dll",
    "PresentationUI.dll",
    "System.Printing.dll",
    "System.ServiceModel.Web.dll",
    "System.ServiceProcess.dll",
    "System.Web.dll",
    "System.Web.HttpUtility.dll",
    "sos.dll",
    "SOS.NETCore.dll"
)
Get-ChildItem -LiteralPath $publishDir -File -ErrorAction SilentlyContinue |
    Where-Object {
        $name = $_.Name
        $removableFiles | Where-Object { $name -like $_ }
    } |
    Remove-Item -Force

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
        & $iscc "/DMyAppVersion=$Version" $issPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compile failed (exit $LASTEXITCODE)."
        }

        $setupExe = Join-Path $publishRoot "Clip_$Version-Setup.exe"
        if (Test-Path $setupExe) {
            Write-Output "Created $setupExe"
        }
    }
}

Write-Output "Published to $publishDir"
