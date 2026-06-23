param(
    [string]$Version = "1.0.0.0",
    [switch]$Build,
    [switch]$TrustDevCertificate,
    [switch]$InteractiveCurrentUserRootTrust,
    [switch]$ElevateIfNeeded,
    [string]$ElevatedLogPath = "",
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"

$script:transcriptStarted = $false
if (-not [string]::IsNullOrWhiteSpace($ElevatedLogPath)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ElevatedLogPath) | Out-Null
    Start-Transcript -Path $ElevatedLogPath -Append | Out-Null
    $script:transcriptStarted = $true
}

trap {
    if ($script:transcriptStarted) {
        Stop-Transcript | Out-Null
        $script:transcriptStarted = $false
    }

    throw $_
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$buildScript = Join-Path $root "tools\Build-ClipCommandPalettePackage.ps1"
$msixPath = Join-Path $root "artifacts\command-palette\msix\Clip.CommandPalette_$Version.msix"

function Quote-ForPowerShellSingleQuotedString([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-SelfElevated {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw "Cannot relaunch elevated because the script path is unknown."
    }

    $logPath = Join-Path $env:TEMP ("Clip.CommandPalette.install-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    $command = "& " + (Quote-ForPowerShellSingleQuotedString $scriptPath) +
        " -Version " + (Quote-ForPowerShellSingleQuotedString $Version) +
        " -TrustDevCertificate" +
        " -ElevatedLogPath " + (Quote-ForPowerShellSingleQuotedString $logPath)
    if ($Build) {
        $command += " -Build"
    }

    if ($InteractiveCurrentUserRootTrust) {
        $command += " -InteractiveCurrentUserRootTrust"
    }

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $powershellExe = (Get-Process -Id $PID).Path
    $process = Start-Process `
        -FilePath $powershellExe `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) `
        -Verb RunAs `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        $logTail = if (Test-Path $logPath) {
            (Get-Content -Tail 80 -LiteralPath $logPath) -join [Environment]::NewLine
        }
        else {
            "No elevated log was created."
        }

        throw "Elevated Command Palette package install failed. Exit code: $($process.ExitCode). Log: $logPath$([Environment]::NewLine)$logTail"
    }

    Write-Output "Elevated Command Palette package install log: $logPath"
}

function Test-AltVHotkey($Hotkey) {
    return $null -ne $Hotkey -and
        $Hotkey.win -eq $false -and
        $Hotkey.ctrl -eq $false -and
        $Hotkey.alt -eq $true -and
        $Hotkey.shift -eq $false -and
        [int]$Hotkey.code -eq 86
}

function Set-ClipCommandPaletteHotkey {
    $installedCommand = Join-Path $env:APPDATA "Programs\Clip\Clip.Command.exe"
    if (Test-Path $installedCommand) {
        & $installedCommand configure-command-palette
        if ($LASTEXITCODE -eq 0) {
            return
        }
    }

    $settingsPath = Join-Path $env:LOCALAPPDATA "Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\settings.json"
    $settingsDir = Split-Path -Parent $settingsPath
    if (-not (Test-Path $settingsDir)) {
        Write-Warning "Command Palette settings folder was not found; Alt+V was not configured."
        return
    }

    $settings = [ordered]@{}
    if (Test-Path $settingsPath) {
        $parsed = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        foreach ($property in $parsed.PSObject.Properties) {
            $settings[$property.Name] = $property.Value
        }
    }

    $filtered = New-Object System.Collections.Generic.List[object]
    if ($settings.Contains("CommandHotkeys") -and $null -ne $settings["CommandHotkeys"]) {
        foreach ($item in @($settings["CommandHotkeys"])) {
            if ([string]$item.CommandId -eq "clip.history" -or (Test-AltVHotkey $item.Hotkey)) {
                continue
            }

            $filtered.Add($item)
        }
    }

    $filtered.Add([pscustomobject]@{
        CommandId = "clip.history"
        Hotkey = [pscustomobject]@{
            win = $false
            ctrl = $false
            alt = $true
            shift = $false
            code = 86
            key = "V"
        }
    })

    $settings["AllowExternalReload"] = $true
    $settings["CommandHotkeys"] = @($filtered)
    [pscustomobject]$settings |
        ConvertTo-Json -Depth 50 |
        Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Start-Process "x-cmdpal://reload"
    Start-Sleep -Seconds 3
    $settings["AllowExternalReload"] = $false
    [pscustomobject]$settings |
        ConvertTo-Json -Depth 50 |
        Set-Content -LiteralPath $settingsPath -Encoding UTF8
    Write-Output "Configured Alt+V for Clip Clipboard History in Command Palette."
}

if ($AllowUnsigned) {
    throw "Unsigned install is not supported for Clip.CommandPalette because the package declares executable activation. Use a signed MSIX, or rerun this script from an elevated shell with -Build -TrustDevCertificate for local development."
}

if ($Build -or -not (Test-Path $msixPath)) {
    & $buildScript -Version $Version
}

if ($TrustDevCertificate) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq "CN=Clip" -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if (-not $cert) {
        throw "The CN=Clip development signing certificate was not found. Run this script with -Build first."
    }

    $certPath = Join-Path $env:TEMP "Clip.CommandPalette.cer"
    Export-Certificate -Cert $cert -FilePath $certPath -Force | Out-Null
    Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
    Write-Output "Trusted Clip dev certificate as a publisher for the current user."

    if ($isAdministrator) {
        Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        Write-Output "Trusted Clip dev certificate for the local machine."
    } elseif ($InteractiveCurrentUserRootTrust) {
        Write-Warning "Windows will show a Security Warning before adding the Clip dev cert to the current user's Root store."
        $certUtil = Start-Process -FilePath "certutil.exe" -ArgumentList @("-user", "-f", "-addstore", "Root", $certPath) -Wait -PassThru
        if ($certUtil.ExitCode -ne 0) {
            throw "certutil failed to add the Clip dev certificate to the current user's Root store. Exit code: $($certUtil.ExitCode)"
        }
    }

    $machineRootTrusted = Get-ChildItem Cert:\LocalMachine\Root |
        Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
        Select-Object -First 1

    if (-not $machineRootTrusted) {
        if ($ElevateIfNeeded) {
            Write-Output "Machine-level certificate trust is required for this MSIX. Relaunching elevated."
            Invoke-SelfElevated
            return
        }

        throw "Windows package deployment on this PC requires the Clip dev certificate in the LocalMachine Root store. Rerun this script with -Build -TrustDevCertificate -ElevateIfNeeded, open PowerShell as administrator and rerun it with -Build -TrustDevCertificate, or install a publicly signed MSIX."
    }
}

if (-not (Test-Path $msixPath)) {
    throw "Command Palette package was not found: $msixPath"
}

try {
    Add-AppxPackage -Path $msixPath -ForceApplicationShutdown
}
catch {
    throw "Command Palette package install failed. $($_.Exception.Message)"
}
Write-Output "Installed $msixPath"
Set-ClipCommandPaletteHotkey

if ($script:transcriptStarted) {
    Stop-Transcript | Out-Null
    $script:transcriptStarted = $false
}
