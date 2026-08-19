# -Elevated registers the autostart task to run Clip with highest privileges.
#
# Windows blocks a medium-integrity process from sending synthetic input to a higher-integrity
# window (UIPI), which is why pasting into an app started as administrator silently does nothing.
# Running Clip elevated puts it on the same side of that line, so those pastes work. A scheduled
# task with RunLevel Highest launches elevated without a UAC prompt at every logon.
#
# The cost is real, so this is opt-in:
#   * Drag and drop from Explorer into Clip stops working - UIPI blocks that direction too.
#   * Anything Clip opens or launches inherits administrator.
#   * Registering the task needs an elevated shell once.
# Prefer this over the uiAccess route only because uiAccess needs a signing certificate and the
# install moved into Program Files. See .claudehelper/HANDOFF.md.
param([switch]$Elevated)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishScript = Join-Path $root "Publish-Clip.ps1"
$publishDir = Join-Path $root "artifacts\publish\Clip-win-x64"

if (-not (Test-Path (Join-Path $root "Clip.exe")) -and -not (Test-Path (Join-Path $publishDir "Clip.exe"))) {
    if (-not (Test-Path $publishScript)) {
        throw "Clip.exe was not found, and the publish script was not found."
    }

    & $publishScript -NoZip
}

$sourceDir = if (Test-Path (Join-Path $publishDir "Clip.exe")) { $publishDir } else { $root }
$sourceExe = Join-Path $sourceDir "Clip.exe"
if (-not (Test-Path $sourceExe)) {
    $sourceExe = Join-Path $sourceDir "Clip.Shell.exe"
}

if (-not (Test-Path $sourceExe)) {
    throw "Clip executable was not found."
}

$installDir = Join-Path $env:APPDATA "Programs\Clip"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

Get-Process Clip, Clip.Shell, Clip.Watcher, Clip.Launcher, Clip.WindowsHistory -ErrorAction SilentlyContinue | Stop-Process -Force
Get-ChildItem -LiteralPath $installDir -Force | Remove-Item -Recurse -Force
Copy-Item (Join-Path $sourceDir "*") $installDir -Recurse -Force

$exe = Join-Path $installDir (Split-Path $sourceExe -Leaf)
# Autostart the standalone shell itself (Clip.exe, no args). It owns Alt+V, the tray, clipboard
# capture and the UI in one process. The old Clip.Watcher.exe "watch" host + hidden prewarmed
# window proved unreliable (the hidden window got stuck and never showed), so we don't use it.
$hostExe = $exe
$launcherExe = $exe
$icon = Join-Path $installDir "assets\app-icons\clip-tile-light.ico"
if (-not (Test-Path $icon)) {
    $icon = $exe
}

# Autostart via a logon Scheduled Task. Windows 11 throttles/delays HKCU "Run" startup apps
# (they often don't launch promptly, or at all, after a reboot), so use a logon task instead.
# Remove any legacy Run-key entry so the two mechanisms can't both fire and double-launch.
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Clip" -ErrorAction SilentlyContinue
# --ensure-running makes a duplicate launch exit quietly instead of popping the palette,
# so the watchdog trigger below can refire while Clip is already up without side effects.
$autoAction    = New-ScheduledTaskAction -Execute $hostExe -Argument "--ensure-running" -WorkingDirectory $installDir
$autoTrigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$autoTrigger.Delay = "PT3S"
# Watchdog: something on this machine (EDR, most likely) can hard-kill Clip without any trace.
# Refiring every 30 minutes relaunches it; when it is already running the fire is a no-op.
$watchdogTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 30) -RepetitionDuration (New-TimeSpan -Days 3650)
$autoSettings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$runLevel      = if ($Elevated) { "Highest" } else { "Limited" }
$autoPrincipal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel $runLevel
if ($Elevated) {
    Write-Output "Autostart registered elevated. Drag and drop into Clip will stop working, and anything Clip launches inherits administrator."
}
Register-ScheduledTask -TaskName "Clip Autostart" -Action $autoAction -Trigger @($autoTrigger, $watchdogTrigger) -Settings $autoSettings -Principal $autoPrincipal -Force | Out-Null

$oldTaskName = "Clip Clipboard Watcher"
if (Get-ScheduledTask -TaskName $oldTaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $oldTaskName -Confirm:$false
}

$shell = New-Object -ComObject WScript.Shell

function New-ClipShortcut {
    param(
        [string]$Path,
        [string]$Description
    )

    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $launcherExe
    $shortcut.Arguments = ""
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = $icon
    $shortcut.WindowStyle = 7
    $shortcut.Description = $Description
    $shortcut.Save()
}

$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "Clip.lnk"
$startupShortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "Clip.lnk"
$startMenuDir = Join-Path ([Environment]::GetFolderPath("Programs")) "Clip"
$startMenuShortcut = Join-Path $startMenuDir "Clip.lnk"

New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
New-ClipShortcut -Path $desktopShortcut -Description "Start Clip"
New-ClipShortcut -Path $startMenuShortcut -Description "Start Clip"
if (Test-Path $startupShortcut) {
    Remove-Item -LiteralPath $startupShortcut -Force
}

Start-Process -FilePath $hostExe -WindowStyle Hidden

Write-Output "Clip installed to $installDir"
Write-Output "Desktop shortcut: $desktopShortcut"
Write-Output "Startup enabled for this Windows user."
