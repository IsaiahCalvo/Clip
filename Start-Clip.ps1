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
    $publishedExe = Join-Path $PSScriptRoot "Clip.Shell.exe"
    $releaseExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Release\net8.0-windows10.0.19041.0\Clip.Shell.exe"
    $releaseRuntimeExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Release\net8.0-windows10.0.19041.0\win-x64\Clip.Shell.exe"
    $debugExe = Join-Path $PSScriptRoot "src\Clip.Shell\bin\Debug\net8.0-windows10.0.19041.0\Clip.Shell.exe"
    $project = Join-Path $PSScriptRoot "src\Clip.Shell\Clip.Shell.csproj"
    $exe = @($publishedExe, $releaseExe, $debugExe, $releaseRuntimeExe) | Where-Object { Test-Path $_ } | Select-Object -First 1

    $running = Get-Process Clip.Shell -ErrorAction SilentlyContinue
    if ($running) {
        Write-LauncherLog "Clip already running pid=$($running.Id -join ',')"
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
    Start-ClipOnce
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
