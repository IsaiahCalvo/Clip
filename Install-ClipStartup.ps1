$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$starter = Join-Path $root "Start-Clip.ps1"
$taskName = "Clip Clipboard Watcher"
$taskCommand = "powershell.exe"
$taskArgs = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$starter`" -Watchdog"

if (-not (Test-Path $starter)) {
    throw "Start script not found: $starter"
}

$runValue = "$taskCommand $taskArgs"
New-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Clip" -Value $runValue

$action = New-ScheduledTaskAction -Execute $taskCommand -Argument $taskArgs
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 0)
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description "Starts Clip clipboard watcher at Windows sign-in." -Force | Out-Null
    Write-Output "Scheduled task installed."
}
catch {
    Write-Output "Scheduled task install skipped: $($_.Exception.Message)"
}

$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "Clip.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $taskCommand
$shortcut.Arguments = $taskArgs
$shortcut.WorkingDirectory = $root
$shortcut.WindowStyle = 7
$shortcut.Description = "Start Clip clipboard watcher"
$shortcut.Save()

Write-Output "Clip startup installed."
