<#
.SYNOPSIS
    Times Clip's palette and Raycast's clipboard history the same way, and compares them.

.DESCRIPTION
    Both applications are resident and both open on a global hotkey registered with RegisterHotKey,
    so both are measured the same way: send the real keystroke, then watch the target window until
    it is on screen. Nothing is instrumented inside either one - Raycast cannot be, so Clip is not
    either, and neither gets an advantage the other cannot have.

    How "open" is detected differs because the two hide their windows differently, and each is
    detected by its own mechanism at the moment the window becomes visible to a user:

      Raycast keeps its window mapped and sets the layered alpha to 0. Opening flips it to 255 in
              one step (no fade), which is a precise and cheap thing to poll.
      Clip    hides its window outright. Opening calls Show(), so the transition is IsWindowVisible
              plus a non-zero size.

    This is deliberately generous to Raycast and harsh on Clip. Raycast's alpha flips when it
    decides to show the window; Clip is additionally required to have painted. Clip's own harness
    (Measure-OpenLatency.ps1) is stricter still - it waits for the search box to take focus - so if
    Clip wins here it is winning while being measured more strictly.

.NOTES
    It types a real Alt+V and Alt+Shift+V, so it takes over the keyboard for a few seconds. Both
    windows are dismissed with Escape after each sample.

    A locked workstation was assumed to make this impossible - synthetic input goes to the input
    desktop, which while locked is the secure one. Measured, it works anyway: SendInput reported
    4/4 and Clip's palette opened within 100ms with the lock screen up. So rather than trusting
    either assumption, the script presses the key once and checks that something happened, and only
    refuses if nothing does. Never report silence as a slow application.
#>
[CmdletBinding()]
param(
    [int]$Runs = 10,
    [int]$SettleMs = 1200,
    [string]$OutDir = "",
    [string]$Label = "vs-raycast"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root ".claudehelper\perf" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$locked = $null -ne (Get-Process LogonUI -ErrorAction SilentlyContinue)
if ($locked) {
    Write-Host "Note: the workstation is locked. Key injection still reaches both apps here, and the probe below proves it per run, but the numbers are taken with the lock screen up." -ForegroundColor Yellow
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class OpenRace
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int c);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] public static extern bool QueryPerformanceCounter(out long c);
    [DllImport("kernel32.dll")] public static extern bool QueryPerformanceFrequency(out long f);
    [DllImport("kernel32.dll")] public static extern void Sleep(uint ms);

    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 2;

    public static long Now() { long c; QueryPerformanceCounter(out c); return c; }
    public static double MsSince(long s) { long n, f; QueryPerformanceCounter(out n); QueryPerformanceFrequency(out f); return (n - s) * 1000.0 / f; }

    static INPUT Key(ushort vk, bool up)
    {
        var i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.ki.wVk = vk;
        i.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        return i;
    }

    /// <summary>Presses a chord for real, so the application's own hotkey registration fires it.</summary>
    public static void Press(ushort[] modifiers, ushort key)
    {
        var list = new List<INPUT>();
        foreach (var m in modifiers) list.Add(Key(m, false));
        list.Add(Key(key, false));
        list.Add(Key(key, true));
        for (int i = modifiers.Length - 1; i >= 0; i--) list.Add(Key(modifiers[i], true));
        var arr = list.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
    }

    public static byte Alpha(IntPtr h)
    {
        uint key; byte a; uint f;
        if (!GetLayeredWindowAttributes(h, out key, out a, out f)) return 255;
        return a;
    }

    /// <summary>
    /// Every top-level window of a process, captured once so the hot loop does no enumeration.
    /// Watching all of them rather than one guessed handle is what makes this robust: picking "the
    /// biggest window" picked the wrong one for Clip (a 960x545 hidden window outranks the 880x560
    /// palette by area), and handles change whenever either app restarts.
    /// </summary>
    public static IntPtr[] WindowsOf(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, p) =>
        {
            uint w; GetWindowThreadProcessId(h, out w);
            if (w == pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    /// <summary>
    /// Is a real window of this application on screen right now?
    ///
    /// The two applications hide differently, so each is asked in its own terms, at the moment a
    /// user would see it. Raycast keeps its window mapped and sets layered alpha to 0, flipping to
    /// 255 in one step. Clip hides its window and parks it off the desktop.
    /// </summary>
    public static bool Shown(IntPtr[] windows, bool raycast)
    {
        foreach (var h in windows)
        {
            if (!IsWindowVisible(h)) continue;
            RECT r; GetWindowRect(h, out r);
            if ((r.Right - r.Left) <= 200 || (r.Bottom - r.Top) <= 200) continue;
            if (raycast) { if (Alpha(h) > 0) return true; }
            else if (r.Left > -10000 && r.Top > -10000) return true;
        }
        return false;
    }

    /// <summary>Presses the chord and returns milliseconds until a window is on screen, or -1.</summary>
    public static double Race(IntPtr[] windows, ushort[] mods, ushort key, bool raycast, int timeoutMs)
    {
        long start = Now();
        Press(mods, key);
        while (MsSince(start) < timeoutMs)
        {
            if (Shown(windows, raycast)) return MsSince(start);
        }
        return -1;
    }

    public static void DismissAll(IntPtr[] windows)
    {
        foreach (var h in windows)
        {
            if (!IsWindowVisible(h)) continue;
            Dismiss(h.ToInt64());
        }
    }

    /// <summary>
    /// Waits until the application is definitely closed again, and says whether it got there.
    ///
    /// Both hotkeys toggle, so pressing while the window is still up closes it instead of opening
    /// it and the run records nothing. That is what produced two "miss" samples in ten on the first
    /// attempt - the harness, not the application. Confirming the closed state before each press
    /// removes them.
    /// </summary>
    public static bool WaitHidden(IntPtr[] windows, bool raycast, int timeoutMs)
    {
        long start = Now();
        int nudges = 0;
        while (MsSince(start) < timeoutMs)
        {
            if (!Shown(windows, raycast)) return true;

            // Ask once, then wait. Re-sending on every pass of a tight loop posts thousands of
            // Escapes a second into both applications' message queues, which does not close them
            // any sooner and wrecks the timings of every run after: medians went from 40ms to 92ms
            // and the worst case from 47ms to 624ms purely from the harness thrashing.
            if (nudges == 0 || MsSince(start) > nudges * 400) { DismissAll(windows); nudges++; }
            Sleep(10);
        }
        return false;
    }

    public static void Dismiss(long hwnd)
    {
        IntPtr h = new IntPtr(hwnd);
        PostMessage(h, 0x0100, new IntPtr(0x1B), new IntPtr(0x00010001));
        PostMessage(h, 0x0101, new IntPtr(0x1B), unchecked((IntPtr)0xC0010001));
        EnumChildWindows(h, (c, p) =>
        {
            PostMessage(c, 0x0100, new IntPtr(0x1B), new IntPtr(0x00010001));
            PostMessage(c, 0x0101, new IntPtr(0x1B), unchecked((IntPtr)0xC0010001));
            return true;
        }, IntPtr.Zero);
    }
}
'@

function Get-Percentile([double[]]$Values, [double]$P) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $s = [double[]]($Values | Sort-Object)
    if ($s.Count -eq 1) { return [math]::Round($s[0], 1) }
    $rank = ($P / 100.0) * ($s.Count - 1)
    $lo = [math]::Floor($rank); $hi = [math]::Ceiling($rank)
    $v = if ($lo -eq $hi) { $s[$lo] } else { $s[$lo] + (($s[$hi] - $s[$lo]) * ($rank - $lo)) }
    return [math]::Round($v, 1)
}

$clipProc = Get-Process Clip -ErrorAction SilentlyContinue | Select-Object -First 1
$rayProc = Get-Process Raycast -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $clipProc) { throw "Clip is not running. Start it so its Alt+V hotkey is registered." }
if (-not $rayProc) { throw "Raycast is not running." }

$clipWindows = [OpenRace]::WindowsOf([uint32]$clipProc.Id)
$rayWindows = [OpenRace]::WindowsOf([uint32]$rayProc.Id)

Write-Host ("Clip: {0} windows (pid {1}, {2})" -f $clipWindows.Count, $clipProc.Id, $clipProc.Path)
Write-Host ("Raycast: {0} windows (pid {1})" -f $rayWindows.Count, $rayProc.Id)
Write-Host "Sending real key presses - do not touch the keyboard for about $([math]::Round($Runs * $SettleMs * 2 / 1000))s."

$VK_MENU = 18; $VK_SHIFT = 16; $VK_V = 86

# Prove the key press reaches each application before trusting a single timing. A run that measures
# nothing must look like a broken harness, not like a slow application.
foreach ($probe in @(
    @{ Name = "Clip"; Windows = $clipWindows; Mods = @([uint16]$VK_MENU); Ray = $false },
    @{ Name = "Raycast"; Windows = $rayWindows; Mods = @([uint16]$VK_MENU, [uint16]$VK_SHIFT); Ray = $true })) {
    $t = [OpenRace]::Race($probe.Windows, $probe.Mods, [uint16]$VK_V, $probe.Ray, 4000)
    [OpenRace]::DismissAll($probe.Windows)
    Start-Sleep -Milliseconds 700
    if ($t -lt 0) {
        throw "$($probe.Name) did not open from a synthetic key press, so nothing can be measured. Its hotkey may be unregistered, remapped, or blocked."
    }

    Write-Host ("  probe: {0} responded in {1:F1} ms" -f $probe.Name, $t)
}

$clipTimes = @(); $rayTimes = @()

for ($i = 1; $i -le $Runs; $i++) {
    Start-Sleep -Milliseconds $SettleMs
    $null = [OpenRace]::WaitHidden($clipWindows, $false, 3000)
    $c = [OpenRace]::Race($clipWindows, @([uint16]$VK_MENU), [uint16]$VK_V, $false, 4000)
    if ($c -ge 0) { $clipTimes += $c }
    [OpenRace]::DismissAll($clipWindows)

    Start-Sleep -Milliseconds $SettleMs
    $null = [OpenRace]::WaitHidden($rayWindows, $true, 3000)
    $r = [OpenRace]::Race($rayWindows, @([uint16]$VK_MENU, [uint16]$VK_SHIFT), [uint16]$VK_V, $true, 4000)
    if ($r -ge 0) { $rayTimes += $r }
    [OpenRace]::DismissAll($rayWindows)

    Write-Host ("  run {0,2}: clip {1,7} ms   raycast {2,7} ms" -f $i,
        $(if ($c -ge 0) { "{0:F1}" -f $c } else { "miss" }),
        $(if ($r -ge 0) { "{0:F1}" -f $r } else { "miss" }))
}

$payload = [pscustomobject]@{
    Label = $Label
    TakenUtc = (Get-Date).ToUniversalTime().ToString("o")
    Commit = (& git -C $root rev-parse --short HEAD 2>$null)
    Runs = $Runs
    SessionLocked = $locked
    ClipExe = $clipProc.Path
    ClipMedianMs = Get-Percentile $clipTimes 50
    ClipP95Ms = Get-Percentile $clipTimes 95
    RaycastMedianMs = Get-Percentile $rayTimes 50
    RaycastP95Ms = Get-Percentile $rayTimes 95
    ClipSamples = $clipTimes
    RaycastSamples = $rayTimes
}

$path = Join-Path $OutDir "$Label.json"
$payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8

Write-Host ""
Write-Host ("Clip    median {0} ms   p95 {1} ms   ({2}/{3} samples)" -f $payload.ClipMedianMs, $payload.ClipP95Ms, $clipTimes.Count, $Runs)
Write-Host ("Raycast median {0} ms   p95 {1} ms   ({2}/{3} samples)" -f $payload.RaycastMedianMs, $payload.RaycastP95Ms, $rayTimes.Count, $Runs)
Write-Host "written to $path"
