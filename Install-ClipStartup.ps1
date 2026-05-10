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

$sourceDir = if (Test-Path (Join-Path $root "Clip.exe")) { $root } else { $publishDir }
$sourceExe = Join-Path $sourceDir "Clip.exe"
if (-not (Test-Path $sourceExe)) {
    $sourceExe = Join-Path $sourceDir "Clip.Shell.exe"
}
$sourceIcon = Join-Path $sourceDir "assets\app-icons\clip-tile-light.ico"

if (-not (Test-Path $sourceExe)) {
    throw "Clip executable was not found."
}

$installDir = Join-Path $env:LOCALAPPDATA "Programs\Clip"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

Get-Process Clip, Clip.Shell -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item (Join-Path $sourceDir "*") $installDir -Recurse -Force

$exe = Join-Path $installDir (Split-Path $sourceExe -Leaf)
$icon = Join-Path $installDir "assets\app-icons\clip-tile-light.ico"
if (-not (Test-Path $icon)) {
    $icon = $exe
}
$quotedExe = "`"$exe`""

New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Clip" -Value $quotedExe

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
    $shortcut.TargetPath = $exe
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
New-ClipShortcut -Path $startupShortcut -Description "Start Clip at sign-in"
New-ClipShortcut -Path $startMenuShortcut -Description "Start Clip"

Start-Process -FilePath $exe -WindowStyle Hidden

Write-Output "Clip installed to $installDir"
Write-Output "Desktop shortcut: $desktopShortcut"
Write-Output "Startup enabled for this Windows user."
