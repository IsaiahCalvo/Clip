param(
    [string]$ExePath = "",
    [int]$FirstLoadLimitMs = 50,
    [int]$PaletteLimitMs = 50,
    [int]$SettingsLimitMs = 50,
    [int]$SecondaryUiLimitMs = 50,
    [int]$SearchLimitMs = 50,
    [int]$ClipboardCaptureLimitMs = 2000,
    [int]$WatcherClipboardStoreLimitMs = 50,
    [int]$CommandJsonProcessLimitMs = 450,
    [int]$CommandJsonQueryLimitMs = 60,
    [int]$WatcherHotkeyShowLimitMs = 50,
    [int]$ShortcutSignalShowLimitMs = 250,
    [int]$ShortcutSignalAppLimitMs = 50,
    [int]$ShortcutShowLimitMs = 4000,
    [int]$ShortcutLauncherToShowLimitMs = 1200,
    [int]$ShortcutPaletteLoadLimitMs = 100,
    [int]$HiddenPrivateMemoryLimitMB = 180,
    [int]$WatcherPrivateMemoryLimitMB = 50,
    [int]$WatcherCapturePrivateMemoryLimitMB = 70,
    [int]$PackageSizeLimitMB = 165,
    [int]$FrameworkDependentPackageSizeLimitMB = 35,
    [int]$CommandPalettePackageSizeLimitMB = 50,
    [int]$CommandPalettePrivateMemoryLimitMB = 30,
    [double]$HiddenCpuDeltaLimit = 0.1,
    [double]$WatcherCpuDeltaLimit = 0.1,
    [double]$CommandPaletteCpuDeltaLimit = 0.1,
    [string]$SearchText = "invoice",
    [switch]$SkipSearch,
    [switch]$SkipSettingsOpen,
    [switch]$SkipSecondaryUi,
    [switch]$SkipClipboardCapture,
    [switch]$SkipCommandJsonList,
    [switch]$SkipWatcherHotkeyShow,
    [switch]$SkipCommandPaletteIdle,
    [switch]$SkipShortcutSignalShow,
    [switch]$SkipPaletteSession,
    [switch]$SkipShortcutShow,
    [switch]$SkipWatcherIdle,
    [switch]$NoSuspendInstalledClip
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $ExePath) {
    $publishedExe = Join-Path $root "artifacts\publish\Clip-win-x64\Clip.exe"
    $debugExe = Join-Path $root "src\Clip.Shell\bin\Debug\net8.0-windows10.0.19041.0\Clip.exe"
    $ExePath = if (Test-Path $publishedExe) { $publishedExe } else { $debugExe }
}

if (-not (Test-Path $ExePath)) {
    throw "Clip executable not found: $ExePath. Run dotnet build Clip.sln --no-restore first."
}

$packageDir = Split-Path -Parent $ExePath

$logPath = Join-Path $env:LOCALAPPDATA "Clip\shell.log"
$watcherLogPath = Join-Path $env:LOCALAPPDATA "Clip\debug.log"
$historyIndexPath = Join-Path $env:LOCALAPPDATA "Clip\Clipboard History\history.index.json"
$installedClipDir = Join-Path $env:APPDATA "Programs\Clip"

if (-not ("ClipPerfWindows" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class ClipPerfWindows
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static string[] VisibleWindowsForProcess(uint pid)
    {
        var result = new List<string>();
        EnumWindows((hWnd, lParam) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid == pid && IsWindowVisible(hWnd))
            {
                Rect rect;
                if (!GetWindowRect(hWnd, out rect) ||
                    rect.Right <= 0 ||
                    rect.Bottom <= 0 ||
                    rect.Left < -1000 ||
                    rect.Top < -1000)
                {
                    return true;
                }

                var title = new StringBuilder(512);
                GetWindowText(hWnd, title, title.Capacity);
                var titleText = title.ToString();
                if (!string.IsNullOrWhiteSpace(titleText))
                {
                    result.Add(string.Format("0x{0:X}:{1}", hWnd.ToInt64(), titleText));
                }
            }

            return true;
        }, IntPtr.Zero);

        return result.ToArray();
    }

    public static IntPtr[] WindowsForProcess(uint pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, lParam) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid == pid)
            {
                result.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);

        return result.ToArray();
    }
}
'@
}

function Get-VisibleWindowsForProcess([int]$ProcessId) {
    [ClipPerfWindows]::VisibleWindowsForProcess([uint32]$ProcessId)
}

function Get-WindowsForProcess([int]$ProcessId) {
    [ClipPerfWindows]::WindowsForProcess([uint32]$ProcessId)
}

function Get-LocalClipProcesses {
    (@(Get-Process -Name Clip -ErrorAction SilentlyContinue) + @(Get-Process -Name Clip.Watcher -ErrorAction SilentlyContinue) + @(Get-Process -Name Clip.Command -ErrorAction SilentlyContinue)) |
        Where-Object {
            $_.Path -and (
                $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
                $_.Path.StartsWith($packageDir, [StringComparison]::OrdinalIgnoreCase) -or
                ((Test-Path $installedClipDir) -and $_.Path.StartsWith($installedClipDir, [StringComparison]::OrdinalIgnoreCase)))
        }
}

function Get-InstalledClipProcesses {
    (@(Get-Process -Name Clip -ErrorAction SilentlyContinue) + @(Get-Process -Name Clip.Shell -ErrorAction SilentlyContinue) + @(Get-Process -Name Clip.Watcher -ErrorAction SilentlyContinue) + @(Get-Process -Name Clip.Command -ErrorAction SilentlyContinue)) |
        Where-Object {
            $_.Path -and
            (Test-Path $installedClipDir) -and
            $_.Path.StartsWith($installedClipDir, [StringComparison]::OrdinalIgnoreCase)
        }
}

function Get-ClipOpenMode {
    $settingsPath = Join-Path $env:LOCALAPPDATA "Clip\settings.json"
    if (-not (Test-Path $settingsPath)) {
        return "Standalone"
    }

    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($settings.OpenMode -eq 1 -or [string]$settings.OpenMode -eq "CommandPalette") {
            return "CommandPalette"
        }
    }
    catch {
    }

    return "Standalone"
}

function Test-CommandPaletteAltVBinding($Hotkey) {
    return $null -ne $Hotkey -and
        $Hotkey.win -eq $false -and
        $Hotkey.ctrl -eq $false -and
        $Hotkey.alt -eq $true -and
        $Hotkey.shift -eq $false -and
        [int]$Hotkey.code -eq 86
}

function Test-CommandPaletteAltVHotkeyOwned {
    if (-not ("ClipPerfHotkeyProbe" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ClipPerfHotkeyProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static int TryRegisterAltV()
    {
        const uint modAlt = 0x0001;
        const uint keyV = 0x56;
        const int id = 9917;
        var registered = RegisterHotKey(IntPtr.Zero, id, modAlt, keyV);
        var error = Marshal.GetLastWin32Error();
        if (registered)
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }

        return registered ? 0 : error;
    }
}
'@
    }

    return [ClipPerfHotkeyProbe]::TryRegisterAltV() -eq 1409
}

function Get-CommandPalettePackageFullNameFromPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    if ($Path -match "\\WindowsApps\\(Clip\.CommandPalette_[^\\]+)\\") {
        return $Matches[1]
    }

    return $null
}

function Measure-CommandPaletteHotkeyReadiness {
    $settingsPath = Join-Path $env:LOCALAPPDATA "Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\settings.json"
    $package = $null
    try {
        $package = Get-AppxPackage -Name Clip.CommandPalette -ErrorAction Stop | Select-Object -First 1
    }
    catch {
    }
    $settings = $null
    if (Test-Path $settingsPath) {
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        }
        catch {
        }
    }

    $hotkeyConfigured = $false
    if ($settings -and $settings.CommandHotkeys) {
        foreach ($item in @($settings.CommandHotkeys)) {
            if ([string]$item.CommandId -eq "clip.history" -and (Test-CommandPaletteAltVBinding $item.Hotkey)) {
                $hotkeyConfigured = $true
                break
            }
        }
    }

    $providerEnabled = $false
    if ($settings -and $settings.ProviderSettings) {
        foreach ($property in $settings.ProviderSettings.PSObject.Properties) {
            if ($property.Name -like "Clip.CommandPalette_*" -and $property.Value.IsEnabled -ne $false) {
                $providerEnabled = $true
                break
            }
        }
    }

    $extensionProcess = Get-Process -Name Clip.CommandPalette -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path -like "*\WindowsApps\Clip.CommandPalette_*" } |
        Select-Object -First 1

    $packageFullName = if ($package) { $package.PackageFullName } elseif ($extensionProcess) { Get-CommandPalettePackageFullNameFromPath $extensionProcess.Path } else { $null }
    $altVOwned = Test-CommandPaletteAltVHotkeyOwned
    $ready = -not [string]::IsNullOrWhiteSpace($packageFullName) -and $hotkeyConfigured -and $providerEnabled -and $null -ne $extensionProcess -and $altVOwned
    if (-not $ready) {
        throw "Command Palette hotkey is not ready. package=$(-not [string]::IsNullOrWhiteSpace($packageFullName)) hotkey=$hotkeyConfigured provider=$providerEnabled extensionProcess=$($null -ne $extensionProcess) altVOwned=$altVOwned settings=$settingsPath"
    }

    return [pscustomobject]@{
        Ready = $ready
        PackageFullName = $packageFullName
        SettingsPath = $settingsPath
        HotkeyConfigured = $hotkeyConfigured
        ProviderEnabled = $providerEnabled
        ExtensionProcessRunning = $null -ne $extensionProcess
        ExtensionPrivateMB = if ($extensionProcess) { [math]::Round($extensionProcess.PrivateMemorySize64 / 1MB, 1) } else { $null }
        AltVOwned = $altVOwned
    }
}

function Stop-InstalledClipProcesses {
    $processes = @(Get-InstalledClipProcesses)
    foreach ($process in $processes) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    foreach ($process in $processes) {
        try {
            $process.WaitForExit(5000) | Out-Null
        }
        catch {
        }
    }

    return $processes.Count
}

function Get-ClipStartupCommand {
    try {
        return (Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Clip" -ErrorAction Stop).Clip
    }
    catch {
        return $null
    }
}

function Split-StartupCommand([string]$Command) {
    $trimmed = $Command.Trim()
    if ($trimmed.Length -eq 0) {
        return $null
    }

    if ($trimmed[0] -eq '"') {
        $endQuote = $trimmed.IndexOf('"', 1)
        if ($endQuote -lt 1) {
            return $null
        }

        return [pscustomobject]@{
            FilePath = $trimmed.Substring(1, $endQuote - 1)
            Arguments = $trimmed.Substring($endQuote + 1).Trim()
        }
    }

    $firstSpace = $trimmed.IndexOf(' ')
    if ($firstSpace -lt 0) {
        return [pscustomobject]@{ FilePath = $trimmed; Arguments = "" }
    }

    return [pscustomobject]@{
        FilePath = $trimmed.Substring(0, $firstSpace)
        Arguments = $trimmed.Substring($firstSpace + 1).Trim()
    }
}

function Start-InstalledClipFromStartup {
    $command = Get-ClipStartupCommand
    if ([string]::IsNullOrWhiteSpace($command)) {
        return $false
    }

    $parsed = Split-StartupCommand $command
    if (-not $parsed -or -not (Test-Path $parsed.FilePath)) {
        return $false
    }

    if ([string]::IsNullOrWhiteSpace($parsed.Arguments)) {
        Start-Process -FilePath $parsed.FilePath -WindowStyle Hidden | Out-Null
    }
    else {
        Start-Process -FilePath $parsed.FilePath -ArgumentList $parsed.Arguments -WindowStyle Hidden | Out-Null
    }

    return $true
}

function Wait-LocalClipProcessesExited([int]$TimeoutMilliseconds = 5000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMilliseconds)
    do {
        $remaining = @(Get-LocalClipProcesses)
        if ($remaining.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    $remaining = @(Get-LocalClipProcesses | ForEach-Object { "$($_.ProcessName):$($_.Id):$($_.Path)" })
    throw "Local Clip processes did not exit: $($remaining -join '; ')"
}

function Stop-LocalClipProcesses {
    $processes = @(Get-LocalClipProcesses)

    foreach ($process in $processes) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    foreach ($process in $processes) {
        try {
            $process.WaitForExit(5000) | Out-Null
        }
        catch {
        }
    }

    Wait-LocalClipProcessesExited
}

function Start-ClipProcess([switch]$DebugVisible, [string]$DebugSearch = "", [switch]$PaletteSession, [switch]$DebugOpenSettings, [string]$DebugOpenSurface = "", [int]$DebugAutoConcealMs = 0) {
    $arguments = @("--debug-perf")
    if ($PaletteSession) {
        $arguments += "--palette-session"
    }

    if ($DebugOpenSettings) {
        $arguments += "--debug-open-settings"
    }

    if (-not [string]::IsNullOrWhiteSpace($DebugOpenSurface)) {
        $arguments += "--debug-open-surface"
        $arguments += $DebugOpenSurface
    }

    if ($DebugVisible) {
        $arguments += "--debug-visible"
    }

    if (-not [string]::IsNullOrWhiteSpace($DebugSearch)) {
        $arguments += "--debug-search"
        $arguments += $DebugSearch
    }

    if ($DebugAutoConcealMs -gt 0) {
        $arguments += "--debug-auto-conceal-ms"
        $arguments += $DebugAutoConcealMs.ToString([Globalization.CultureInfo]::InvariantCulture)
    }

    if ($arguments.Count -gt 0) {
        return Start-Process -FilePath $ExePath -ArgumentList $arguments -PassThru
    }

    return Start-Process -FilePath $ExePath -PassThru
}

function Recent-LogLines([datetime]$Since) {
    if (-not (Test-Path $logPath)) {
        return @()
    }

    $sinceOffset = [DateTimeOffset]$Since
    Get-Content -Tail 220 $logPath |
        Where-Object {
            $timestamp = ($_.Split(" ", 2))[0]
            $lineTime = [DateTimeOffset]::MinValue
            [DateTimeOffset]::TryParse(
                $timestamp,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None,
                [ref]$lineTime) -and $lineTime -ge $sinceOffset
        }
}

function Recent-WatcherLogLines([datetime]$Since) {
    if (-not (Test-Path $watcherLogPath)) {
        return @()
    }

    $sinceOffset = ([DateTimeOffset]$Since).ToUniversalTime().AddSeconds(-1)
    Get-Content -Tail 220 $watcherLogPath |
        Where-Object {
            if ($_ -match "^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2})Z") {
                $lineTime = [DateTimeOffset]::MinValue
                [DateTimeOffset]::TryParseExact(
                    "$($Matches[1]) $($Matches[2])Z",
                    "yyyy-MM-dd HH:mm:ss'Z'",
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::AssumeUniversal,
                    [ref]$lineTime) -and $lineTime -ge $sinceOffset
            }
            else {
                $false
            }
        }
}

function WatcherLogLength {
    if (-not (Test-Path $watcherLogPath)) {
        return 0
    }

    return (Get-Item -LiteralPath $watcherLogPath).Length
}

function New-WatcherLogLines([long]$Offset) {
    if (-not (Test-Path $watcherLogPath)) {
        return @()
    }

    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.File]::Open($watcherLogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        if ($Offset -gt 0) {
            $stream.Seek([Math]::Min($Offset, $stream.Length), [System.IO.SeekOrigin]::Begin) | Out-Null
        }

        $reader = [System.IO.StreamReader]::new($stream)
        $text = $reader.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($text)) {
            return @()
        }

        return $text -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }
    finally {
        if ($reader) {
            $reader.Dispose()
        }
        elseif ($stream) {
            $stream.Dispose()
        }
    }
}

function Wait-ForNewWatcherLogLine([long]$Offset, [string]$Pattern, [int]$TimeoutSeconds = 12, [System.Diagnostics.Process]$Process = $null) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $line = New-WatcherLogLines $Offset | Where-Object { $_ -match $Pattern } | Select-Object -Last 1
        if ($line) {
            return $line
        }

        if ($Process) {
            $Process.Refresh()
            if ($Process.HasExited) {
                return $null
            }
        }

        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    return $null
}

function Value-AfterKey([string]$Line, [string]$Key) {
    $escapedKey = [regex]::Escape($Key)
    if ($Line -match "(^|\s)$escapedKey=([0-9]+)(\s|$)") {
        return [int]$Matches[2]
    }

    return $null
}

function Assert-Under([string]$Name, [int]$Actual, [int]$Limit) {
    if ($Actual -gt $Limit) {
        throw "$Name was ${Actual}ms, limit is ${Limit}ms."
    }
}

function Measure-PackageSizeMB {
    if (-not (Test-Path $packageDir)) {
        return $null
    }

    $size = (Get-ChildItem $packageDir -Recurse -File | Measure-Object Length -Sum).Sum
    return [math]::Round($size / 1MB, 1)
}

function Measure-FrameworkDependentPackageSizeMB {
    $frameworkDependentDir = Join-Path $root "artifacts\publish\Clip-win-x64-framework-dependent"
    if (-not (Test-Path $frameworkDependentDir)) {
        return $null
    }

    $size = (Get-ChildItem $frameworkDependentDir -Recurse -File | Measure-Object Length -Sum).Sum
    return [math]::Round($size / 1MB, 1)
}

function Measure-CommandPalettePackageSizeMB {
    $msixRoot = Join-Path $root "artifacts\command-palette\msix"
    if (-not (Test-Path $msixRoot)) {
        return $null
    }

    $package = Get-ChildItem -LiteralPath $msixRoot -Filter "*.msix" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $package) {
        return $null
    }

    return [math]::Round($package.Length / 1MB, 2)
}

function Find-CommandPaletteExtensionExe {
    $candidate = Join-Path $root "artifacts\command-palette\package\Clip.CommandPalette.exe"
    if (Test-Path $candidate) {
        return $candidate
    }

    return $null
}

function Wait-ForLogLine([datetime]$Since, [string]$Pattern, [int]$TimeoutSeconds = 12, [System.Diagnostics.Process]$Process = $null) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $line = Recent-LogLines $Since | Where-Object { $_ -match $Pattern } | Select-Object -Last 1
        if ($line) {
            return $line
        }

        if ($Process) {
            $Process.Refresh()
            if ($Process.HasExited) {
                return $null
            }
        }

        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    return $null
}

function Wait-ForWatcherLogLine([datetime]$Since, [string]$Pattern, [int]$TimeoutSeconds = 12, [System.Diagnostics.Process]$Process = $null) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $line = Recent-WatcherLogLines $Since | Where-Object { $_ -match $Pattern } | Select-Object -Last 1
        if ($line) {
            return $line
        }

        if ($Process) {
            $Process.Refresh()
            if ($Process.HasExited) {
                return $null
            }
        }

        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    return $null
}

function Find-WatcherCommand {
    $candidates = @(
        (Join-Path $packageDir "Clip.Watcher.exe"),
        (Join-Path $installedClipDir "Clip.Watcher.exe"),
        (Join-Path $root "artifacts\publish\Clip-win-x64\Clip.Watcher.exe"),
        (Join-Path $root "src\Clip.Watcher\bin\Release\net8.0-windows10.0.19041.0\win-x64\Clip.Watcher.exe"),
        (Join-Path $root "src\Clip.Watcher\bin\Debug\net8.0-windows10.0.19041.0\Clip.Watcher.exe")
    )

    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Find-LauncherCommand {
    $candidates = @(
        (Join-Path $packageDir "Clip.Launcher.exe"),
        (Join-Path $installedClipDir "Clip.Launcher.exe"),
        (Join-Path $root "artifacts\publish\Clip-win-x64\Clip.Launcher.exe"),
        (Join-Path $root "src\Clip.Launcher\bin\Release\net8.0\win-x64\Clip.Launcher.exe"),
        (Join-Path $root "src\Clip.Launcher\bin\Debug\net8.0\Clip.Launcher.exe")
    )

    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Find-ShortcutLaunchCommand {
    $launcher = Find-LauncherCommand
    if ($launcher) {
        return [pscustomobject]@{
            FilePath = $launcher
            Arguments = [string[]]@()
            Name = "Clip.Launcher"
        }
    }

    $watcher = Find-WatcherCommand
    if ($watcher) {
        return [pscustomobject]@{
            FilePath = $ExePath
            Arguments = @("--palette-session")
            Name = "Clip"
        }
    }

    return $null
}

function Start-ShortcutProcess($Shortcut) {
    if ($Shortcut.Arguments -and $Shortcut.Arguments.Count -gt 0) {
        return Start-Process -FilePath $Shortcut.FilePath -ArgumentList $Shortcut.Arguments -WindowStyle Hidden -PassThru
    }

    return Start-Process -FilePath $Shortcut.FilePath -WindowStyle Hidden -PassThru
}

function Find-JsonListCommand {
    $candidates = @(
        (Join-Path $packageDir "Clip.Command.exe"),
        (Join-Path $installedClipDir "Clip.Command.exe"),
        (Join-Path $root "artifacts\publish\Clip-win-x64\Clip.Command.exe"),
        (Join-Path $root "src\Clip.Command\bin\Release\net8.0\win-x64\Clip.Command.exe"),
        (Join-Path $root "src\Clip.Command\bin\Debug\net8.0\Clip.Command.exe"),
        (Find-WatcherCommand)
    )

    return $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

function Find-HistoryItemByMarker([string]$Marker) {
    if (-not (Test-Path $historyIndexPath)) {
        return $null
    }

    try {
        $items = Get-Content $historyIndexPath -Raw | ConvertFrom-Json
        return $items |
            Where-Object { $_.Preview -eq $Marker -or $_.Text -eq $Marker } |
            Select-Object -First 1
    }
    catch {
        return $null
    }
}

function Test-ClipboardStillHasText([string]$ExpectedText) {
    try {
        return (Get-Clipboard -Raw -Format Text -ErrorAction Stop) -eq $ExpectedText
    }
    catch {
        return $false
    }
}

function Set-ClipboardTextWithRetry([string]$Value, [int]$Attempts = 20) {
    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Set-Clipboard -Value $Value -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds ([Math]::Min(750, 75 * $attempt))
        }
    }

    throw "Set-Clipboard failed after $Attempts attempts: $($lastError.Exception.Message)"
}

function Invoke-PerfHistoryCommand([string]$Command, [string]$Value) {
    $watcher = Find-WatcherCommand
    if (-not $watcher) {
        throw "Could not find Clip.Watcher.exe to seed perf history."
    }

    $output = & $watcher $Command $Value 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Perf history command '$Command' failed: $($output -join '; ')"
    }

    $id = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    if (-not $id) {
        throw "Perf history command '$Command' did not return an item id."
    }

    return [string]$id
}

function Add-HistoryTextMarker([string]$Marker) {
    Invoke-PerfHistoryCommand "perf-add-text" $Marker
}

function Add-HistoryFileMarker([string]$Path) {
    Invoke-PerfHistoryCommand "perf-add-file" $Path
}

function Get-ProcessDiagnostics([int]$ProcessId) {
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        $commandLine = ""
        try {
            $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId").CommandLine
        }
        catch {
        }

        $modules = @()
        try {
            $modules = @($process.Modules | Select-Object -ExpandProperty ModuleName | Sort-Object)
        }
        catch {
        }

        $interestingModules = @($modules | Where-Object {
            $_ -match "^(Clip|Presentation|WindowsBase|System\.Windows|Microsoft\.Web\.WebView2|WebView2|coreclr|wpfgfx)"
        })

        $children = @()
        try {
            $children = @(Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $ProcessId } | ForEach-Object { "$($_.Name):$($_.ProcessId)" })
        }
        catch {
        }

        return [pscustomobject]@{
            ProcessId = $ProcessId
            Path = $process.Path
            CommandLine = $commandLine
            InterestingModules = $interestingModules -join ","
            ChildProcesses = $children -join ","
        }
    }
    catch {
        return [pscustomobject]@{
            ProcessId = $ProcessId
            Path = ""
            CommandLine = ""
            InterestingModules = ""
            ChildProcesses = ""
        }
    }
}

function Test-ProcessModuleLoaded([int]$ProcessId, [string]$Pattern) {
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        return @($process.Modules | Where-Object { $_.ModuleName -match $Pattern }).Count -gt 0
    }
    catch {
        return $false
    }
}

function Remove-TestHistoryItem([string]$Id) {
    if ([string]::IsNullOrWhiteSpace($Id)) {
        return
    }

    $watcher = Find-WatcherCommand
    if (-not $watcher) {
        throw "Could not find Clip.Watcher.exe to remove perf test item $Id."
    }

    & $watcher delete $Id | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to remove perf test item $Id."
    }
}

function Remove-TestHistoryMarkers {
    if (-not (Test-Path $historyIndexPath)) {
        return
    }

    try {
        $items = Get-Content $historyIndexPath -Raw | ConvertFrom-Json
        $markers = @($items | Where-Object { $_.Preview -like "clip-perf-*" -or $_.Text -like "clip-perf-*" })
        foreach ($item in $markers) {
            Remove-TestHistoryItem $item.Id
        }
    }
    catch {
    }
}

function Measure-CommandJsonList {
    $command = Find-JsonListCommand
    if (-not $command) {
        throw "Could not find Clip.Command.exe or Clip.Watcher.exe to measure JSON list command."
    }

    $marker = "clip-perf-json-" + [guid]::NewGuid().ToString("N")
    $itemId = $null
    try {
        $itemId = Add-HistoryTextMarker $marker
        Wait-LocalClipProcessesExited

        $samples = @()
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $startInfo = [Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = $command
            $startInfo.Arguments = "list --json --query $marker --limit 25"
            $startInfo.UseShellExecute = $false
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            $startInfo.CreateNoWindow = $true

            $timer = [Diagnostics.Stopwatch]::StartNew()
            $process = [Diagnostics.Process]::Start($startInfo)
            if (-not $process.WaitForExit(5000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                throw "JSON list command did not exit within 5 seconds."
            }

            $timer.Stop()
            $stdout = $process.StandardOutput.ReadToEnd()
            $stderr = $process.StandardError.ReadToEnd()

            if ($process.ExitCode -ne 0) {
                throw "JSON list command failed with exit code $($process.ExitCode): $stderr"
            }

            $result = $stdout | ConvertFrom-Json
            $items = @($result.items)
            if ($result.source -ne "summary-search") {
                throw "JSON list command used unexpected source '$($result.source)'."
            }

            if (-not ($items | Where-Object { $_.id -eq $itemId })) {
                throw "JSON list command did not return the seeded test item."
            }

            $leftovers = @(Get-LocalClipProcesses)
            if ($leftovers.Count -gt 0) {
                $leftoverText = ($leftovers | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join '; '
                throw "JSON list command left local Clip processes running: $leftoverText"
            }

            $samples += [pscustomobject]@{
                ProcessMs = [int]$timer.ElapsedMilliseconds
                QueryMs = [int]$result.elapsedMs
                Count = [int]$result.count
                Bytes = $stdout.Length
                Exe = Split-Path -Leaf $command
            }
        }

        $bestProcess = $samples | Sort-Object ProcessMs | Select-Object -First 1
        $bestQuery = $samples | Sort-Object QueryMs | Select-Object -First 1
        return [pscustomobject]@{
            ProcessMs = $bestProcess.ProcessMs
            QueryMs = $bestQuery.QueryMs
            Count = $bestQuery.Count
            Bytes = $bestQuery.Bytes
            Exe = $bestProcess.Exe
            Samples = (($samples | ForEach-Object { $_.ProcessMs }) -join ",")
            QuerySamples = (($samples | ForEach-Object { $_.QueryMs }) -join ",")
        }
    }
    finally {
        if ($itemId) {
            Remove-TestHistoryItem $itemId
        }
    }
}

function Measure-ExternalIdle([string]$Path, [string[]]$Arguments = @(), [int]$WarmupSeconds = 5, [int]$SampleSeconds = 5) {
    $process = $null
    try {
        $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds $WarmupSeconds
        $process.Refresh()
        if ($process.HasExited) {
            throw "$Path exited before idle could be measured."
        }

        $before = Get-Process -Id $process.Id
        $cpuBefore = $before.CPU
        Start-Sleep -Seconds $SampleSeconds
        $after = Get-Process -Id $process.Id

        return [pscustomobject]@{
            PrivateMB = [math]::Round($after.PrivateMemorySize64 / 1MB, 1)
            CpuDelta = [math]::Round($after.CPU - $cpuBefore, 4)
            ModuleCount = $after.Modules.Count
            WebView2Loaded = Test-ProcessModuleLoaded $after.Id "Microsoft\.Web\.WebView2"
            Diagnostics = Get-ProcessDiagnostics $after.Id
        }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

function Measure-ClipboardCapture {
    $oldText = $null
    try {
        $oldText = Get-Clipboard -Raw -Format Text -ErrorAction Stop
    }
    catch {
    }

    $process = $null
    $itemId = $null
    try {
        $startup = Get-Date
        $process = Start-ClipProcess
        $readyLine = Wait-ForLogLine $startup "window initialized" 12 $process
        if (-not $readyLine) {
            $recent = Recent-LogLines $startup
            throw "No hidden clipboard listener readiness line found. Recent log: $($recent -join '; ')"
        }

        $lastCaptureStart = Get-Date
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $marker = "clip-perf-" + [guid]::NewGuid().ToString("N")
            $captureStart = Get-Date
            $lastCaptureStart = $captureStart
            Set-ClipboardTextWithRetry $marker
            $deadline = $captureStart.AddSeconds([Math]::Max(8, [Math]::Ceiling($ClipboardCaptureLimitMs / 1000) + 3))
            do {
                Start-Sleep -Milliseconds 150
                $hit = Find-HistoryItemByMarker $marker
                if ($hit) {
                    $itemId = $hit.Id
                    return [int]((Get-Date) - $captureStart).TotalMilliseconds
                }

                $process.Refresh()
                if ($process.HasExited) {
                    throw "Clip exited during clipboard capture measurement."
                }

                if (((Get-Date) - $captureStart).TotalMilliseconds -gt 700 -and -not (Test-ClipboardStillHasText $marker)) {
                    break
                }
            } while ((Get-Date) -lt $deadline)

            if ($attempt -lt 3) {
                Start-Sleep -Milliseconds 250
            }
        }

        $recent = Recent-LogLines $lastCaptureStart
        throw "Clipboard capture marker did not appear in history index within timeout. Recent log: $($recent -join '; ')"
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }

        if ($oldText -ne $null) {
            Set-ClipboardTextWithRetry $oldText
        }

        if ($itemId) {
            Remove-TestHistoryItem $itemId
        }
    }
}

function Measure-WatcherClipboardCapture {
    $watcher = Find-WatcherCommand
    if (-not $watcher) {
        throw "Could not find Clip.Watcher.exe to measure watcher clipboard capture."
    }

    $oldText = $null
    try {
        $oldText = Get-Clipboard -Raw -Format Text -ErrorAction Stop
    }
    catch {
    }

    $process = $null
    $itemId = $null
    try {
        $watcherStart = Get-Date
        $process = Start-Process -FilePath $watcher -ArgumentList @("watch") -PassThru -WindowStyle Hidden
        $readyLine = Wait-ForWatcherLogLine $watcherStart "Clip watcher started" 12 $process
        $process.Refresh()
        if (-not $readyLine) {
            $recent = Recent-WatcherLogLines $watcherStart
            if ($process.HasExited) {
                throw "Clip watcher exited before clipboard capture measurement. Recent watcher log: $($recent -join '; ')"
            }

            throw "Clip watcher did not report readiness before clipboard capture measurement. Recent watcher log: $($recent -join '; ')"
        }

        $lastCaptureStart = Get-Date
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            $marker = "clip-perf-" + [guid]::NewGuid().ToString("N")
            $captureStart = Get-Date
            $lastCaptureStart = $captureStart
            Set-ClipboardTextWithRetry $marker
            $deadline = $captureStart.AddSeconds([Math]::Max(8, [Math]::Ceiling($ClipboardCaptureLimitMs / 1000) + 3))
            do {
                Start-Sleep -Milliseconds 150
                $hit = Find-HistoryItemByMarker $marker
                if ($hit) {
                    $itemId = $hit.Id
                    $pollMs = [int]((Get-Date) - $captureStart).TotalMilliseconds
                    $captureLine = Recent-WatcherLogLines $watcherStart |
                        Where-Object { $_ -match "Clipboard captured" -and $_ -like "*$marker*" } |
                        Select-Object -Last 1
                    $appMs = if ($captureLine) { Value-AfterKey $captureLine "elapsedMs" } else { $null }
                    $storeMs = if ($captureLine) { Value-AfterKey $captureLine "storeMs" } else { $null }
                    Start-Sleep -Seconds 2
                    $process.Refresh()
                    return [pscustomobject]@{
                        AppMs = $appMs
                        PollMs = $pollMs
                        StoreMs = $storeMs
                        PrivateMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
                    }
                }

                $process.Refresh()
                if ($process.HasExited) {
                    throw "Clip watcher exited during clipboard capture measurement."
                }

                if (((Get-Date) - $captureStart).TotalMilliseconds -gt 700 -and -not (Test-ClipboardStillHasText $marker)) {
                    break
                }
            } while ((Get-Date) -lt $deadline)

            if ($attempt -lt 3) {
                Start-Sleep -Milliseconds 250
            }
        }

        $recent = Recent-WatcherLogLines $lastCaptureStart
        throw "Watcher clipboard capture marker did not appear in history index within timeout. Recent watcher log: $($recent -join '; ')"
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }

        if ($oldText -ne $null) {
            Set-ClipboardTextWithRetry $oldText
        }

        if ($itemId) {
            Remove-TestHistoryItem $itemId
        }
    }
}

function Measure-WatcherHotkeyShowOnce {
    $watcher = Find-WatcherCommand
    if (-not $watcher) {
        throw "Could not find Clip.Watcher.exe to measure watcher hotkey show."
    }

    Stop-LocalClipProcesses
    $process = $null
    try {
        $watcherLogOffset = WatcherLogLength
        $watcherStart = Get-Date
        $process = Start-Process -FilePath $watcher -ArgumentList @("watch") -PassThru -WindowStyle Hidden
        $readyLine = Wait-ForNewWatcherLogLine $watcherLogOffset "Clip watcher started" 12 $process
        if (-not $readyLine) {
            $recent = New-WatcherLogLines $watcherLogOffset
            throw "Clip watcher did not report readiness before hotkey show measurement. Recent watcher log: $($recent -join '; ')"
        }

        $windows = @(Get-WindowsForProcess $process.Id)
        if ($windows.Count -eq 0) {
            throw "Clip watcher did not expose a message window for hotkey measurement."
        }

        $showStart = Get-Date
        foreach ($window in $windows) {
            [ClipPerfWindows]::PostMessage($window, 0x0312, [IntPtr]7001, [IntPtr]::Zero) | Out-Null
        }

        $deadline = $showStart.AddSeconds(5)
        do {
            $visibleProcess = Get-LocalClipProcesses |
                Where-Object { $_.ProcessName -eq "Clip" } |
                Sort-Object StartTime -Descending |
                ForEach-Object {
                    $windows = @(Get-VisibleWindowsForProcess $_.Id)
                    if ($windows.Count -gt 0) {
                        [pscustomobject]@{
                            Process = $_
                            Window = $windows[0]
                        }
                    }
                } |
                Select-Object -First 1

            if ($visibleProcess) {
                $showMs = [int]((Get-Date) - $showStart).TotalMilliseconds
                Start-Sleep -Milliseconds 250
                $process.Refresh()
                $timingLine = New-WatcherLogLines $watcherLogOffset |
                    Where-Object { $_ -match "Shell palette requested elapsedMs" } |
                    Select-Object -Last 1
                $shellLine = Recent-LogLines $showStart |
                    Where-Object { $_ -match "palette shown" } |
                    Select-Object -Last 1
                return [pscustomobject]@{
                    ShowMs = $showMs
                    Window = $visibleProcess.Window
                    ProcessName = $visibleProcess.Process.ProcessName
                    PrivateMB = [math]::Round($visibleProcess.Process.PrivateMemorySize64 / 1MB, 1)
                    AppMs = if ($shellLine) { Value-AfterKey $shellLine "elapsedMs" } elseif ($timingLine) { Value-AfterKey $timingLine "elapsedMs" } else { $null }
                    PrewarmMs = $null
                }
            }

            $process.Refresh()
            if ($process.HasExited) {
                throw "Clip watcher exited during hotkey show measurement."
            }

            Start-Sleep -Milliseconds 2
        } while ((Get-Date) -lt $deadline)

        $recent = New-WatcherLogLines $watcherLogOffset
        throw "Clip watcher hotkey did not create a visible shell palette. Recent watcher log: $($recent -join '; ')"
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

function Measure-WatcherHotkeyShow([int]$Attempts = 3) {
    $samples = @()
    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $sample = Measure-WatcherHotkeyShowOnce
            $sample | Add-Member -NotePropertyName Attempt -NotePropertyValue $attempt
            $samples += $sample
        }
        catch {
            $lastError = $_
        }

        if ($attempt -lt $Attempts) {
            Start-Sleep -Milliseconds 250
        }
    }

    if ($samples.Count -eq 0) {
        if ($lastError) {
            throw $lastError
        }

        throw "Watcher hotkey show produced no samples."
    }

    $worst = $samples | Sort-Object ShowMs -Descending | Select-Object -First 1
    $worst | Add-Member -NotePropertyName Samples -NotePropertyValue (($samples | ForEach-Object { $_.ShowMs }) -join ",")
    return $worst
}

function Measure-PaletteSession {
    $process = $null
    try {
        $sessionStart = Get-Date
        $debugSearch = if (-not [string]::IsNullOrWhiteSpace($SearchText)) { $SearchText } else { "" }
        $process = Start-ClipProcess -PaletteSession -DebugSearch $debugSearch -DebugAutoConcealMs 1000

        $sessionLine = Wait-ForLogLine $sessionStart "window initialized.*session=True" 12 $process
        if (-not $sessionLine) {
            $recent = Recent-LogLines $sessionStart
            throw "No palette-session readiness line found. Recent log: $($recent -join '; ')"
        }

        $paletteLine = Wait-ForLogLine $sessionStart "palette shown" 12 $process
        if (-not $paletteLine) {
            $recent = Recent-LogLines $sessionStart
            throw "No palette-session show timing line found. Recent log: $($recent -join '; ')"
        }

        $loadLine = Wait-ForLogLine $sessionStart "load items reason=show-refresh" 12 $process
        if (-not $loadLine) {
            $recent = Recent-LogLines $sessionStart
            throw "No palette-session load timing line found. Recent log: $($recent -join '; ')"
        }

        $exitLine = Wait-ForLogLine $sessionStart "palette session exiting" 12 $process
        if (-not $exitLine) {
            $recent = Recent-LogLines $sessionStart
            throw "No palette-session exit line found. Recent log: $($recent -join '; ')"
        }

        $deadline = (Get-Date).AddSeconds(8)
        do {
            $process.Refresh()
            if ($process.HasExited) {
                break
            }

            Start-Sleep -Milliseconds 100
        } while ((Get-Date) -lt $deadline)

        if (-not $process.HasExited) {
            throw "Palette session did not exit after auto-conceal."
        }

        return [pscustomobject]@{
            PaletteMs = Value-AfterKey $paletteLine "elapsedMs"
            FirstLoadMs = Value-AfterKey $loadLine "elapsedMs"
            Exited = $true
        }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

function Measure-ShortcutShowLaunch {
    $shortcut = Find-ShortcutLaunchCommand
    if (-not $shortcut) {
        throw "Could not find Clip.Launcher.exe or Clip.Watcher.exe to measure shortcut show launch."
    }

    Stop-LocalClipProcesses
    $process = $null
    try {
        $watcherLogOffset = WatcherLogLength
        $showStart = Get-Date
        $process = Start-ShortcutProcess $shortcut
        $deadline = $showStart.AddSeconds([Math]::Max(8, [Math]::Ceiling($ShortcutShowLimitMs / 1000) + 4))
        do {
            Start-Sleep -Milliseconds 2
            $visibleProcess = Get-LocalClipProcesses |
                Where-Object { $_.ProcessName -eq "Clip" } |
                Sort-Object StartTime -Descending |
                ForEach-Object {
                    $windows = @(Get-VisibleWindowsForProcess $_.Id)
                    if ($windows.Count -gt 0) {
                        [pscustomobject]@{
                            Process = $_
                            Window = $windows[0]
                        }
                    }
                } |
                Select-Object -First 1

            if ($visibleProcess) {
                $shownProcess = $visibleProcess.Process
                $visibleMs = [int]((Get-Date) - $showStart).TotalMilliseconds
                $paletteLoadLine = Wait-ForLogLine $showStart "load items reason=show-refresh" 12
                $showLine = Recent-LogLines $showStart |
                    Where-Object { $_ -match "palette shown" } |
                    Select-Object -Last 1
                return [pscustomobject]@{
                    ShowMs = $visibleMs
                    Window = $visibleProcess.Window
                    LauncherToMainMs = $null
                    LauncherToShowMs = if ($showLine) { Value-AfterKey $showLine "elapsedMs" } else { $null }
                    PaletteLoadMs = if ($paletteLoadLine) { Value-AfterKey $paletteLoadLine "elapsedMs" } else { $null }
                    ShellPrivateMB = [math]::Round($shownProcess.PrivateMemorySize64 / 1MB, 1)
                    ProcessName = $shownProcess.ProcessName
                    ShortcutProcess = $shortcut.Name
                }
            }

            if ($process) {
                $process.Refresh()
                if ($process.HasExited -and -not $visibleProcess -and $shortcut.Name -ne "Clip.Launcher") {
                    throw "Clip shortcut process exited before shortcut show launch created a visible shell window."
                }
            }
        } while ((Get-Date) -lt $deadline)

        $processes = @(Get-LocalClipProcesses | ForEach-Object { "$($_.ProcessName):$($_.Id):$($_.Path)" })
        throw "Shortcut show launch did not create a visible Clip window. Processes: $($processes -join '; ')"
    }
    finally {
        Stop-LocalClipProcesses
    }
}

function Measure-ShortcutSignalShow {
    $watcher = Find-WatcherCommand
    $shortcut = Find-ShortcutLaunchCommand
    if (-not $watcher -or -not $shortcut) {
        throw "Could not find Clip watcher and shortcut launcher to measure shortcut signal show."
    }

    Stop-LocalClipProcesses
    $watcherProcess = $null
    $shortcutProcess = $null
    try {
        $watcherLogOffset = WatcherLogLength
        $watcherProcess = Start-Process -FilePath $watcher -ArgumentList @("watch") -PassThru -WindowStyle Hidden
        $readyLine = Wait-ForNewWatcherLogLine $watcherLogOffset "Clip watcher started" 12 $watcherProcess
        if (-not $readyLine) {
            $recent = New-WatcherLogLines $watcherLogOffset
            throw "Clip watcher did not report readiness before shortcut signal measurement. Recent watcher log: $($recent -join '; ')"
        }

        $showStart = Get-Date
        $shortcutProcess = Start-ShortcutProcess $shortcut
        $deadline = $showStart.AddMilliseconds($ShortcutSignalShowLimitMs + 1500)
        do {
            $visibleProcess = Get-LocalClipProcesses |
                Where-Object { $_.ProcessName -eq "Clip" } |
                Sort-Object StartTime -Descending |
                ForEach-Object {
                    $windows = @(Get-VisibleWindowsForProcess $_.Id)
                    if ($windows.Count -gt 0) {
                        [pscustomobject]@{
                            Process = $_
                            Window = $windows[0]
                        }
                    }
                } |
                Select-Object -First 1

            if ($visibleProcess) {
                $showMs = [int]((Get-Date) - $showStart).TotalMilliseconds
                Start-Sleep -Milliseconds 250
                $shellLine = Recent-LogLines $showStart |
                    Where-Object { $_ -match "palette shown" } |
                    Select-Object -Last 1

                return [pscustomobject]@{
                    ShowMs = $showMs
                    Window = $visibleProcess.Window
                    ProcessName = $visibleProcess.Process.ProcessName
                    ShortcutProcess = $shortcut.Name
                    WatcherPrivateMB = [math]::Round((Get-Process -Id $watcherProcess.Id).PrivateMemorySize64 / 1MB, 1)
                    AppMs = if ($shellLine) { Value-AfterKey $shellLine "elapsedMs" } else { $null }
                    PrewarmMs = $null
                }
            }

            $watcherProcess.Refresh()
            if ($watcherProcess.HasExited) {
                throw "Clip watcher exited during shortcut signal measurement."
            }

            Start-Sleep -Milliseconds 2
        } while ((Get-Date) -lt $deadline)

        throw "Shortcut signal did not show the shell palette."
    }
    finally {
        if ($shortcutProcess -and -not $shortcutProcess.HasExited) {
            Stop-Process -Id $shortcutProcess.Id -Force -ErrorAction SilentlyContinue
            $shortcutProcess.WaitForExit(5000) | Out-Null
        }

        Stop-LocalClipProcesses
    }
}

function Measure-SettingsOpen {
    $process = $null
    try {
        $settingsStart = Get-Date
        $process = Start-ClipProcess -DebugVisible -DebugOpenSettings

        $readyLine = Wait-ForLogLine $settingsStart "window initialized" 12 $process
        if (-not $readyLine) {
            $recent = Recent-LogLines $settingsStart
            throw "No settings measurement readiness line found. Recent log: $($recent -join '; ')"
        }

        $settingsLine = Wait-ForLogLine $settingsStart "settings rendered" 12 $process
        if (-not $settingsLine) {
            $recent = Recent-LogLines $settingsStart
            throw "No settings render timing line found. Recent log: $($recent -join '; ')"
        }

        return Value-AfterKey $settingsLine "elapsedMs"
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

function Measure-DebugSurfaceOpen([string]$Surface, [string]$LogName) {
    $process = $null
    try {
        $surfaceStart = Get-Date
        $process = Start-ClipProcess -DebugVisible -DebugOpenSurface $Surface

        $readyLine = Wait-ForLogLine $surfaceStart "window initialized" 12 $process
        if (-not $readyLine) {
            $recent = Recent-LogLines $surfaceStart
            throw "No $Surface measurement readiness line found. Recent log: $($recent -join '; ')"
        }

        $escapedLogName = [regex]::Escape($LogName)
        $surfaceLine = Wait-ForLogLine $surfaceStart "$escapedLogName rendered" 12 $process
        if (-not $surfaceLine) {
            $recent = Recent-LogLines $surfaceStart
            throw "No $Surface render timing line found. Recent log: $($recent -join '; ')"
        }

        return Value-AfterKey $surfaceLine "elapsedMs"
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

$restartInstalledClip = $false
if (-not $NoSuspendInstalledClip) {
    $restartInstalledClip = @(Get-InstalledClipProcesses).Count -gt 0
    if ($restartInstalledClip) {
        Stop-InstalledClipProcesses | Out-Null
    }
}

Stop-LocalClipProcesses
Remove-TestHistoryMarkers

$visibleProcess = $null
$hiddenProcess = $null
try {
    $packageSizeMB = Measure-PackageSizeMB
    if ($packageSizeMB -ne $null -and $packageSizeMB -gt $PackageSizeLimitMB) {
        throw "Package size was ${packageSizeMB}MB, limit is ${PackageSizeLimitMB}MB."
    }

    $frameworkDependentPackageSizeMB = Measure-FrameworkDependentPackageSizeMB
    if ($frameworkDependentPackageSizeMB -ne $null -and $frameworkDependentPackageSizeMB -gt $FrameworkDependentPackageSizeLimitMB) {
        throw "Framework-dependent package size was ${frameworkDependentPackageSizeMB}MB, limit is ${FrameworkDependentPackageSizeLimitMB}MB."
    }

    $commandPalettePackageSizeMB = Measure-CommandPalettePackageSizeMB
    if ($commandPalettePackageSizeMB -ne $null -and $commandPalettePackageSizeMB -gt $CommandPalettePackageSizeLimitMB) {
        throw "Command Palette package size was ${commandPalettePackageSizeMB}MB, limit is ${CommandPalettePackageSizeLimitMB}MB."
    }

    $commandPaletteIdle = $null
    if (-not $SkipCommandPaletteIdle) {
        $commandPaletteExe = Find-CommandPaletteExtensionExe
        if ($commandPaletteExe) {
            $commandPaletteIdle = Measure-ExternalIdle $commandPaletteExe @("-RegisterProcessAsComServer") 2 5
            if ($commandPaletteIdle.PrivateMB -gt $CommandPalettePrivateMemoryLimitMB) {
                $diagnostics = $commandPaletteIdle.Diagnostics
                throw "Command Palette extension private memory was $($commandPaletteIdle.PrivateMB)MB, limit is ${CommandPalettePrivateMemoryLimitMB}MB. pid=$($diagnostics.ProcessId) path=$($diagnostics.Path) commandLine=$($diagnostics.CommandLine) modules=$($diagnostics.InterestingModules) children=$($diagnostics.ChildProcesses)"
            }

            if ($commandPaletteIdle.CpuDelta -gt $CommandPaletteCpuDeltaLimit) {
                $diagnostics = $commandPaletteIdle.Diagnostics
                throw "Command Palette extension CPU delta was $($commandPaletteIdle.CpuDelta) over 5s, limit is ${CommandPaletteCpuDeltaLimit}. pid=$($diagnostics.ProcessId) path=$($diagnostics.Path) commandLine=$($diagnostics.CommandLine)"
            }
        }
    }

    $commandJsonList = $null
    if (-not $SkipCommandJsonList) {
        $commandJsonList = Measure-CommandJsonList
        Assert-Under "Command JSON list process" $commandJsonList.ProcessMs $CommandJsonProcessLimitMs
        Assert-Under "Command JSON list query" $commandJsonList.QueryMs $CommandJsonQueryLimitMs
    }

    $visibleStart = (Get-Date).AddSeconds(-1)
    $debugSearch = if (-not $SkipSearch -and -not [string]::IsNullOrWhiteSpace($SearchText)) { $SearchText } else { "" }
    $visibleProcess = Start-ClipProcess -DebugVisible -DebugSearch $debugSearch

    $paletteLine = Wait-ForLogLine $visibleStart "palette shown" 12 $visibleProcess
    $loadLine = Wait-ForLogLine $visibleStart "load items reason=show-refresh" 12 $visibleProcess
    $searchLine = if (-not $SkipSearch -and -not [string]::IsNullOrWhiteSpace($SearchText)) {
        Wait-ForLogLine $visibleStart "load items reason=search" 12 $visibleProcess
    }
    else {
        Recent-LogLines $visibleStart | Where-Object { $_ -match "load items reason=search" } | Select-Object -Last 1
    }

    if (-not $paletteLine) {
        $recent = Recent-LogLines $visibleStart
        throw "No palette timing line found in $logPath. Recent log: $($recent -join '; ')"
    }

    if (-not $loadLine) {
        $recent = Recent-LogLines $visibleStart
        throw "No first-load timing line found in $logPath. Recent log: $($recent -join '; ')"
    }

    $visible = Get-Process -Id $visibleProcess.Id -ErrorAction SilentlyContinue
    if (-not $visible) {
        $recent = Recent-LogLines $visibleStart
        throw "Clip exited while visible timing was being measured. Recent log: $($recent -join '; ')"
    }

    $paletteMs = Value-AfterKey $paletteLine "elapsedMs"
    $firstLoadMs = Value-AfterKey $loadLine "elapsedMs"
    $searchMs = if ($searchLine) { Value-AfterKey $searchLine "elapsedMs" } else { $null }
    $visibleWebView2Loaded = Test-ProcessModuleLoaded $visible.Id "Microsoft\.Web\.WebView2"

    Assert-Under "Palette show" $paletteMs $PaletteLimitMs
    Assert-Under "First list load" $firstLoadMs $FirstLoadLimitMs
    if ($visibleWebView2Loaded) {
        throw "Visible startup loaded WebView2 before an HTML preview was opened."
    }

    if ($searchMs -ne $null) {
        Assert-Under "Search load" $searchMs $SearchLimitMs
    }
    elseif (-not $SkipSearch -and -not [string]::IsNullOrWhiteSpace($SearchText)) {
        throw "No search timing line found in $logPath."
    }

    Stop-Process -Id $visibleProcess.Id -Force
    $visibleProcess.WaitForExit(5000) | Out-Null
    $visibleProcess = $null
    Wait-LocalClipProcessesExited

    $settingsOpenMs = $null
    if (-not $SkipSettingsOpen) {
        $settingsOpenMs = Measure-SettingsOpen
        Assert-Under "Settings open" $settingsOpenMs $SettingsLimitMs
    }

    $renameOpenMs = $null
    $editTextOpenMs = $null
    $openWithOpenMs = $null
    $watcherSettingsOpenMs = $null
    $watcherRenameOpenMs = $null
    $watcherEditTextOpenMs = $null
    $watcherOpenWithOpenMs = $null

    $hiddenPrivateMB = $null
    $cpuDelta = $null
    $hiddenWebView2Loaded = $null
    $hiddenMeasured = $false
    for ($hiddenAttempt = 1; $hiddenAttempt -le 2; $hiddenAttempt++) {
        $hiddenStart = Get-Date
        $hiddenProcess = Start-ClipProcess
        Start-Sleep -Seconds 5
        $hiddenProcess.Refresh()
        if ($hiddenProcess.HasExited) {
            $recent = Recent-LogLines $hiddenStart
            throw "Clip exited before hidden idle could be measured. Recent log: $($recent -join '; ')"
        }

        $hiddenBefore = Get-Process -Id $hiddenProcess.Id
        $cpuBefore = $hiddenBefore.CPU
        Start-Sleep -Seconds 5
        $hidden = Get-Process -Id $hiddenProcess.Id
        $cpuDelta = [math]::Round($hidden.CPU - $cpuBefore, 4)
        $hiddenPrivateMB = [math]::Round($hidden.PrivateMemorySize64 / 1MB, 1)
        $hiddenWebView2Loaded = Test-ProcessModuleLoaded $hidden.Id "Microsoft\.Web\.WebView2"
        $hiddenLines = Recent-LogLines $hiddenStart

        if ($hiddenWebView2Loaded) {
            throw "Hidden idle loaded WebView2 before an HTML preview was opened."
        }

        $unexpectedStartupWork = $hiddenLines | Where-Object {
            $_ -match "palette shown|load items reason=startup|windows history import reason=startup|render items reason=startup"
        }
        if ($unexpectedStartupWork) {
            throw "Unexpected hidden startup work: $($unexpectedStartupWork -join '; ')"
        }

        $clipboardActivity = $hiddenLines | Where-Object {
            $_ -match "clipboard captured|clipboard capture failed|render items reason=clipboard-live"
        }
        if ($clipboardActivity) {
            if ($hiddenAttempt -lt 2) {
                Stop-Process -Id $hiddenProcess.Id -Force -ErrorAction SilentlyContinue
                $hiddenProcess.WaitForExit(5000) | Out-Null
                $hiddenProcess = $null
                Start-Sleep -Seconds 1
                continue
            }

            throw "Hidden idle sample had clipboard activity: $($clipboardActivity -join '; ')"
        }

        if ($hiddenPrivateMB -gt $HiddenPrivateMemoryLimitMB) {
            throw "Hidden private memory was ${hiddenPrivateMB}MB, limit is ${HiddenPrivateMemoryLimitMB}MB."
        }

        if ($cpuDelta -gt $HiddenCpuDeltaLimit) {
            throw "Hidden CPU delta was $cpuDelta over 5s, limit is $HiddenCpuDeltaLimit."
        }

        $hiddenMeasured = $true
        break
    }

    if (-not $hiddenMeasured) {
        throw "Hidden idle could not be measured."
    }

    if ($hiddenProcess -and -not $hiddenProcess.HasExited) {
        Stop-Process -Id $hiddenProcess.Id -Force -ErrorAction SilentlyContinue
        $hiddenProcess.WaitForExit(5000) | Out-Null
        $hiddenProcess = $null
    }

    $watcherPrivateMB = $null
    $watcherCpuDelta = $null
    $watcherModuleCount = $null
    $watcherWebView2Loaded = $null
    $idleMemoryGapMB = $null
    if (-not $SkipWatcherIdle) {
        $watcher = Find-WatcherCommand
        if ($watcher) {
            $watcherIdle = Measure-ExternalIdle $watcher @("watch") 18 10
            $watcherPrivateMB = $watcherIdle.PrivateMB
            $watcherCpuDelta = $watcherIdle.CpuDelta
            $watcherModuleCount = $watcherIdle.ModuleCount
            $watcherWebView2Loaded = $watcherIdle.WebView2Loaded
            $idleMemoryGapMB = [math]::Round($hiddenPrivateMB - $watcherPrivateMB, 1)

            if ($watcherWebView2Loaded) {
                $diagnostics = $watcherIdle.Diagnostics
                throw "Watcher idle loaded WebView2 before an HTML preview was opened. pid=$($diagnostics.ProcessId) path=$($diagnostics.Path) commandLine=$($diagnostics.CommandLine) modules=$($diagnostics.InterestingModules) children=$($diagnostics.ChildProcesses)"
            }

            if ($watcherPrivateMB -gt $WatcherPrivateMemoryLimitMB) {
                $diagnostics = $watcherIdle.Diagnostics
                throw "Watcher private memory was ${watcherPrivateMB}MB, limit is ${WatcherPrivateMemoryLimitMB}MB. pid=$($diagnostics.ProcessId) path=$($diagnostics.Path) commandLine=$($diagnostics.CommandLine) modules=$($diagnostics.InterestingModules) children=$($diagnostics.ChildProcesses)"
            }

            if ($watcherCpuDelta -gt $WatcherCpuDeltaLimit) {
                $diagnostics = $watcherIdle.Diagnostics
                throw "Watcher settled CPU delta was $watcherCpuDelta over 10s, limit is ${WatcherCpuDeltaLimit}. pid=$($diagnostics.ProcessId) path=$($diagnostics.Path) commandLine=$($diagnostics.CommandLine)"
            }
        }
    }

    $openMode = Get-ClipOpenMode
    $commandPaletteHotkey = $null
    $watcherHotkeyShowSkipped = $false
    $watcherHotkeyShow = $null
    if (-not $SkipWatcherHotkeyShow) {
        if ($openMode -eq "CommandPalette") {
            $commandPaletteHotkey = Measure-CommandPaletteHotkeyReadiness
            $watcherHotkeyShowSkipped = $true
        }
        else {
            $watcherHotkeyShow = Measure-WatcherHotkeyShow
            Assert-Under "Watcher hotkey show" $watcherHotkeyShow.ShowMs $WatcherHotkeyShowLimitMs
        }
    }

    $shortcutSignalShow = $null
    if (-not $SkipShortcutSignalShow) {
        $shortcutSignalShow = Measure-ShortcutSignalShow
        Assert-Under "Shortcut signal show" $shortcutSignalShow.ShowMs $ShortcutSignalShowLimitMs
        if ($shortcutSignalShow.AppMs -ne $null) {
            Assert-Under "Shortcut signal app show" $shortcutSignalShow.AppMs $ShortcutSignalAppLimitMs
        }
    }

    if (-not $SkipSecondaryUi) {
        $textItemId = $null
        $fileItemId = $null
        $tempFile = $null
        try {
            $textMarker = "clip-perf-ui-" + [guid]::NewGuid().ToString("N")
            $textItemId = Add-HistoryTextMarker $textMarker
            Wait-LocalClipProcessesExited

            $renameOpenMs = Measure-DebugSurfaceOpen "rename" "rename"
            Assert-Under "Rename open" $renameOpenMs $SecondaryUiLimitMs
            Wait-LocalClipProcessesExited

            $editTextOpenMs = Measure-DebugSurfaceOpen "edit-text" "edit-text"
            Assert-Under "Edit text open" $editTextOpenMs $SecondaryUiLimitMs
            Wait-LocalClipProcessesExited

            $tempFile = Join-Path ([IO.Path]::GetTempPath()) ("clip-perf-open-with-" + [guid]::NewGuid().ToString("N") + ".txt")
            Set-Content -LiteralPath $tempFile -Value "clip perf open with" -Encoding UTF8
            $fileItemId = Add-HistoryFileMarker $tempFile
            Wait-LocalClipProcessesExited

            $openWithOpenMs = Measure-DebugSurfaceOpen "open-with" "open-with"
            Assert-Under "Open With open" $openWithOpenMs $SecondaryUiLimitMs
            Wait-LocalClipProcessesExited

            $watcherSettingsOpenMs = $null
            $watcherRenameOpenMs = $null
            $watcherEditTextOpenMs = $null
            $watcherOpenWithOpenMs = $null
        }
        finally {
            if ($textItemId) {
                Remove-TestHistoryItem $textItemId
            }

            if ($fileItemId) {
                Remove-TestHistoryItem $fileItemId
            }

            if ($tempFile -and (Test-Path $tempFile)) {
                Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
            }
        }
    }

    $clipboardCaptureMs = $null
    $watcherClipboardCaptureMs = $null
    $watcherClipboardStoreMs = $null
    $watcherCapturePrivateMB = $null
    if (-not $SkipClipboardCapture) {
        $clipboardCaptureMs = Measure-ClipboardCapture
        Assert-Under "Clipboard capture" $clipboardCaptureMs $ClipboardCaptureLimitMs

        $watcherClipboardCapture = Measure-WatcherClipboardCapture
        $watcherClipboardCaptureMs = if ($watcherClipboardCapture.AppMs -ne $null) { $watcherClipboardCapture.AppMs } else { $watcherClipboardCapture.PollMs }
        $watcherClipboardStoreMs = $watcherClipboardCapture.StoreMs
        $watcherCapturePrivateMB = $watcherClipboardCapture.PrivateMB
        Assert-Under "Watcher clipboard capture" $watcherClipboardCaptureMs $ClipboardCaptureLimitMs
        if ($watcherClipboardStoreMs -ne $null) {
            Assert-Under "Watcher clipboard store" $watcherClipboardStoreMs $WatcherClipboardStoreLimitMs
        }

        if ($watcherCapturePrivateMB -gt $WatcherCapturePrivateMemoryLimitMB) {
            throw "Watcher private memory after clipboard capture was ${watcherCapturePrivateMB}MB, limit is ${WatcherCapturePrivateMemoryLimitMB}MB."
        }
    }

    $paletteSession = $null
    if (-not $SkipPaletteSession) {
        $paletteSession = Measure-PaletteSession
        Assert-Under "Palette-session show" $paletteSession.PaletteMs $PaletteLimitMs
        Assert-Under "Palette-session first list load" $paletteSession.FirstLoadMs $FirstLoadLimitMs
    }

    $shortcutShow = $null
    if (-not $SkipShortcutShow) {
        $shortcutShow = Measure-ShortcutShowLaunch
        Assert-Under "Shortcut show launch" $shortcutShow.ShowMs $ShortcutShowLimitMs
        if ($shortcutShow.LauncherToShowMs -ne $null) {
            Assert-Under "Shortcut launcher-to-show" $shortcutShow.LauncherToShowMs $ShortcutLauncherToShowLimitMs
        }

        if ($shortcutShow.PaletteLoadMs -ne $null) {
            Assert-Under "Shortcut first palette load" $shortcutShow.PaletteLoadMs $ShortcutPaletteLoadLimitMs
        }
    }

    [pscustomobject]@{
        PaletteMs = $paletteMs
        FirstLoadMs = $firstLoadMs
        SearchMs = $searchMs
        SettingsOpenMs = $settingsOpenMs
        RenameOpenMs = $renameOpenMs
        EditTextOpenMs = $editTextOpenMs
        OpenWithOpenMs = $openWithOpenMs
        WatcherSettingsOpenMs = $watcherSettingsOpenMs
        WatcherRenameOpenMs = $watcherRenameOpenMs
        WatcherEditTextOpenMs = $watcherEditTextOpenMs
        WatcherOpenWithOpenMs = $watcherOpenWithOpenMs
        CommandJsonProcessMs = if ($commandJsonList) { $commandJsonList.ProcessMs } else { $null }
        CommandJsonQueryMs = if ($commandJsonList) { $commandJsonList.QueryMs } else { $null }
        CommandJsonCount = if ($commandJsonList) { $commandJsonList.Count } else { $null }
        CommandJsonBytes = if ($commandJsonList) { $commandJsonList.Bytes } else { $null }
        CommandJsonExe = if ($commandJsonList) { $commandJsonList.Exe } else { $null }
        CommandJsonSamplesMs = if ($commandJsonList) { $commandJsonList.Samples } else { $null }
        CommandJsonQuerySamplesMs = if ($commandJsonList) { $commandJsonList.QuerySamples } else { $null }
        ClipboardCaptureMs = $clipboardCaptureMs
        WatcherClipboardCaptureMs = $watcherClipboardCaptureMs
        WatcherClipboardPollMs = if ($watcherClipboardCapture) { $watcherClipboardCapture.PollMs } else { $null }
        WatcherClipboardStoreMs = $watcherClipboardStoreMs
        WatcherCapturePrivateMB = $watcherCapturePrivateMB
        PaletteSessionMs = if ($paletteSession) { $paletteSession.PaletteMs } else { $null }
        PaletteSessionFirstLoadMs = if ($paletteSession) { $paletteSession.FirstLoadMs } else { $null }
        PaletteSessionExited = if ($paletteSession) { $paletteSession.Exited } else { $null }
        ShortcutShowMs = if ($shortcutShow) { $shortcutShow.ShowMs } else { $null }
        ShortcutShowWindow = if ($shortcutShow) { $shortcutShow.Window } else { $null }
        ShortcutLauncherToMainMs = if ($shortcutShow) { $shortcutShow.LauncherToMainMs } else { $null }
        ShortcutLauncherToShowMs = if ($shortcutShow) { $shortcutShow.LauncherToShowMs } else { $null }
        ShortcutPaletteLoadMs = if ($shortcutShow) { $shortcutShow.PaletteLoadMs } else { $null }
        ShortcutShowProcess = if ($shortcutShow) { $shortcutShow.ProcessName } else { $null }
        ShortcutShowShortcutProcess = if ($shortcutShow) { $shortcutShow.ShortcutProcess } else { $null }
        ShortcutShowShellPrivateMB = if ($shortcutShow) { $shortcutShow.ShellPrivateMB } else { $null }
        ShortcutSignalShowMs = if ($shortcutSignalShow) { $shortcutSignalShow.ShowMs } else { $null }
        ShortcutSignalWindow = if ($shortcutSignalShow) { $shortcutSignalShow.Window } else { $null }
        ShortcutSignalShortcutProcess = if ($shortcutSignalShow) { $shortcutSignalShow.ShortcutProcess } else { $null }
        ShortcutSignalWatcherPrivateMB = if ($shortcutSignalShow) { $shortcutSignalShow.WatcherPrivateMB } else { $null }
        ShortcutSignalAppMs = if ($shortcutSignalShow) { $shortcutSignalShow.AppMs } else { $null }
        ShortcutSignalPrewarmMs = if ($shortcutSignalShow) { $shortcutSignalShow.PrewarmMs } else { $null }
        OpenMode = $openMode
        WatcherHotkeyShowSkipped = $watcherHotkeyShowSkipped
        CommandPaletteHotkeyReady = if ($commandPaletteHotkey) { $commandPaletteHotkey.Ready } else { $null }
        CommandPaletteHotkeyConfigured = if ($commandPaletteHotkey) { $commandPaletteHotkey.HotkeyConfigured } else { $null }
        CommandPaletteProviderEnabled = if ($commandPaletteHotkey) { $commandPaletteHotkey.ProviderEnabled } else { $null }
        CommandPaletteExtensionRunning = if ($commandPaletteHotkey) { $commandPaletteHotkey.ExtensionProcessRunning } else { $null }
        CommandPaletteExtensionPrivateMB = if ($commandPaletteHotkey) { $commandPaletteHotkey.ExtensionPrivateMB } else { $null }
        CommandPaletteAltVOwned = if ($commandPaletteHotkey) { $commandPaletteHotkey.AltVOwned } else { $null }
        CommandPalettePackageFullName = if ($commandPaletteHotkey) { $commandPaletteHotkey.PackageFullName } else { $null }
        VisiblePrivateMB = [math]::Round($visible.PrivateMemorySize64 / 1MB, 1)
        HiddenPrivateMB = $hiddenPrivateMB
        HiddenCpuDelta5s = $cpuDelta
        WatcherPrivateMB = $watcherPrivateMB
        WatcherCpuDelta10s = $watcherCpuDelta
        WatcherModuleCount = $watcherModuleCount
        WatcherHotkeyShowMs = if ($watcherHotkeyShow) { $watcherHotkeyShow.ShowMs } else { $null }
        WatcherHotkeyAppMs = if ($watcherHotkeyShow) { $watcherHotkeyShow.AppMs } else { $null }
        WatcherHotkeyPrewarmMs = if ($watcherHotkeyShow) { $watcherHotkeyShow.PrewarmMs } else { $null }
        WatcherHotkeyPrivateMB = if ($watcherHotkeyShow) { $watcherHotkeyShow.PrivateMB } else { $null }
        WatcherHotkeyWindow = if ($watcherHotkeyShow) { $watcherHotkeyShow.Window } else { $null }
        WatcherHotkeySamplesMs = if ($watcherHotkeyShow) { $watcherHotkeyShow.Samples } else { $null }
        VisibleStartupWebView2Loaded = $visibleWebView2Loaded
        HiddenStartupWebView2Loaded = $hiddenWebView2Loaded
        WatcherStartupWebView2Loaded = $watcherWebView2Loaded
        IdleMemoryGapMB = $idleMemoryGapMB
        PackageSizeMB = $packageSizeMB
        FrameworkDependentPackageSizeMB = $frameworkDependentPackageSizeMB
        CommandPalettePackageSizeMB = $commandPalettePackageSizeMB
        CommandPalettePrivateMB = if ($commandPaletteIdle) { $commandPaletteIdle.PrivateMB } else { $null }
        CommandPaletteCpuDelta5s = if ($commandPaletteIdle) { $commandPaletteIdle.CpuDelta } else { $null }
        CommandPaletteModuleCount = if ($commandPaletteIdle) { $commandPaletteIdle.ModuleCount } else { $null }
        ExePath = $ExePath
    }
}
finally {
    if ($visibleProcess -and -not $visibleProcess.HasExited) {
        Stop-Process -Id $visibleProcess.Id -Force -ErrorAction SilentlyContinue
        $visibleProcess.WaitForExit(5000) | Out-Null
    }

    if ($hiddenProcess -and -not $hiddenProcess.HasExited) {
        Stop-Process -Id $hiddenProcess.Id -Force -ErrorAction SilentlyContinue
        $hiddenProcess.WaitForExit(5000) | Out-Null
    }

    Remove-TestHistoryMarkers

    if ($restartInstalledClip -and @(Get-InstalledClipProcesses).Count -eq 0) {
        Start-InstalledClipFromStartup | Out-Null
    }
}
