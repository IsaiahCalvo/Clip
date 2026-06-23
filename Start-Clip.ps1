param(
    [switch]$Watchdog
)

$ErrorActionPreference = "Stop"
$logRoot = Join-Path $env:LOCALAPPDATA "Clip"
$launcherLog = Join-Path $logRoot "launcher.log"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

function Write-LauncherLog {
    param([string]$Message)
    Add-Content -Path $launcherLog -Value "$(Get-Date -Format o) $Message"
}

function Start-ClipOnce {
    param(
        [switch]$Show
    )

    $publishedWatcherExe = Join-Path $PSScriptRoot "Clip.Watcher.exe"
    $publishedLauncherExe = Join-Path $PSScriptRoot "Clip.Launcher.exe"
    $publishedAppExe = Join-Path $PSScriptRoot "Clip.exe"
    $publishedExe = Join-Path $PSScriptRoot "Clip.Shell.exe"
    $releaseWatcherExe = Join-Path $PSScriptRoot "src\Clip.Watcher\bin\Release\net8.0-windows10.0.19041.0\win-x64\Clip.Watcher.exe"
    $releaseLauncherExe = Join-Path $PSScriptRoot "src\Clip.Launcher\bin\Release\net8.0\win-x64\Clip.Launcher.exe"
    $debugWatcherExe = Join-Path $PSScriptRoot "src\Clip.Watcher\bin\Debug\net8.0-windows10.0.19041.0\Clip.Watcher.exe"
    $debugLauncherExe = Join-Path $PSScriptRoot "src\Clip.Launcher\bin\Debug\net8.0\Clip.Launcher.exe"
    $releaseExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Release\net8.0-windows10.0.19041.0\Clip.exe"
    $releaseShellExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Release\net8.0-windows10.0.19041.0\Clip.Shell.exe"
    $releaseRuntimeExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Release\net8.0-windows10.0.19041.0\win-x64\Clip.exe"
    $debugExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Debug\net8.0-windows10.0.19041.0\Clip.exe"
    $debugShellExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Debug\net8.0-windows10.0.19041.0\Clip.Shell.exe"
    $project = Join-Path $PSScriptRoot "src\Clip.Shell\Clip.Shell.csproj"
    $watcherExe = @($publishedWatcherExe, $releaseWatcherExe, $debugWatcherExe) | Where-Object { Test-Path $_ } | Select-Object -First 1
    $launcherExe = @($publishedLauncherExe, $releaseLauncherExe, $debugLauncherExe) | Where-Object { Test-Path $_ } | Select-Object -First 1
    $exe = @($publishedAppExe, $publishedExe, $releaseExe, $releaseShellExe, $debugExe, $debugShellExe, $releaseRuntimeExe) | Where-Object { Test-Path $_ } | Select-Object -First 1

    $running = @(Get-Process Clip -ErrorAction SilentlyContinue) + @(Get-Process Clip.Shell -ErrorAction SilentlyContinue) + @(Get-Process Clip.Watcher -ErrorAction SilentlyContinue)
    if ($running) {
        if ($Show -and $launcherExe) {
            Start-Process -FilePath $launcherExe -WindowStyle Hidden
            Write-LauncherLog "Requested Clip palette from launcher: $launcherExe"
            return
        }

        if ($Show -and $watcherExe -and $exe) {
            Start-Process -FilePath $exe -ArgumentList @("--palette-session") -WindowStyle Hidden
            Write-LauncherLog "Requested Clip palette from shell fallback: $exe"
            return
        }

        Write-LauncherLog "Clip already running pid=$($running.Id -join ',')"
        return
    }

    if ($watcherExe -and $exe) {
        $watcherArgs = @("watch")
        if ($Show) {
            $watcherArgs += "--show"
        }

        Start-Process -FilePath $watcherExe -ArgumentList $watcherArgs -WindowStyle Hidden
        Write-LauncherLog "Started Clip lightweight host: $watcherExe"
        return
    }

    if ($exe) {
        Start-Process -FilePath $exe -WindowStyle Hidden
        Write-LauncherLog "Started Clip shell exe: $exe"
        return
    }

    Start-Process -FilePath "dotnet" -ArgumentList "run --project `"$project`"" -WindowStyle Hidden
    Write-LauncherLog "Started Clip shell through dotnet run."
}

if (-not $Watchdog) {
    Start-ClipOnce -Show
    exit 0
}

$created = $false
$mutex = New-Object System.Threading.Mutex($true, "Global\ClipClipboardWatcherStartup", [ref]$created)
if (-not $created) {
    Write-LauncherLog "Watchdog already running."
    exit 0
}

Write-LauncherLog "Watchdog started."
while ($true) {
    try {
        Start-ClipOnce
    }
    catch {
        Write-LauncherLog "Watchdog error: $($_.Exception.Message)"
    }

    Start-Sleep -Seconds 15
}
