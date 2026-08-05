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
    REQUIRES AN UNLOCKED, INTERACTIVE SESSION. Synthetic keystrokes go to the input desktop, and
    while the workstation is locked that is the secure desktop, so nothing receives them. The script
    refuses to run rather than reporting silence as a slow application.

    It types a real Alt+V and Alt+Shift+V, so it takes over the keyboard for a few seconds. Both
    windows are dismissed with Escape after each sample.
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

if (Get-Process LogonUI -ErrorAction SilentlyContinue) {
    throw "The session is locked. Synthetic key presses go to the lock screen's desktop, not to Clip or Raycast, so this would measure nothing. Unlock and re-run."
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

    public static bool RaycastShown(IntPtr h) { return IsWindowVisible(h) && Alpha(h) > 0; }

    public static bool ClipShown(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        RECT r; GetWindowRect(h, out r);
        // On screen and a real size: Clip parks the window off the desktop while hidden.
        return (r.Right - r.Left) > 200 && (r.Bottom - r.Top) > 200 && r.Left > -10000 && r.Top > -10000;
    }

    /// <summary>Finds the biggest top-level window of a process whose class matches, by area.</summary>
    public static long FindWindow(uint pid, string classContains)
    {
        long best = 0; int bestArea = 0;
        EnumWindows((h, p) =>
        {
            uint w; GetWindowThreadProcessId(h, out w);
            if (w != pid) return true;
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            if (classContains.Length > 0 && c.ToString().IndexOf(classContains, StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r; GetWindowRect(h, out r);
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h.ToInt64(); }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Presses the chord and returns milliseconds until the window is on screen, or -1.</summary>
    public static double Race(long hwnd, ushort[] mods, ushort key, bool raycast, int timeoutMs)
    {
        IntPtr h = new IntPtr(hwnd);
        long start = Now();
        Press(mods, key);
        while (MsSince(start) < timeoutMs)
        {
            if (raycast ? RaycastShown(h) : ClipShown(h)) return MsSince(start);
        }
        return -1;
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

$clipHwnd = [OpenRace]::FindWindow([uint32]$clipProc.Id, "HwndWrapper")
$rayHwnd = [OpenRace]::FindWindow([uint32]$rayProc.Id, "HwndWrapper")
if ($clipHwnd -eq 0) { throw "Could not find Clip's palette window." }
if ($rayHwnd -eq 0) { throw "Could not find Raycast's window." }

Write-Host ("Clip hwnd 0x{0:X}  |  Raycast hwnd 0x{1:X}" -f $clipHwnd, $rayHwnd)
Write-Host "Sending real key presses - do not touch the keyboard for about $([math]::Round($Runs * $SettleMs * 2 / 1000))s."

$VK_MENU = 18; $VK_SHIFT = 16; $VK_V = 86

$clipTimes = @(); $rayTimes = @()

for ($i = 1; $i -le $Runs; $i++) {
    Start-Sleep -Milliseconds $SettleMs
    $c = [OpenRace]::Race($clipHwnd, @([uint16]$VK_MENU), [uint16]$VK_V, $false, 4000)
    if ($c -ge 0) { $clipTimes += $c }
    [OpenRace]::Dismiss($clipHwnd)

    Start-Sleep -Milliseconds $SettleMs
    $r = [OpenRace]::Race($rayHwnd, @([uint16]$VK_MENU, [uint16]$VK_SHIFT), [uint16]$VK_V, $true, 4000)
    if ($r -ge 0) { $rayTimes += $r }
    [OpenRace]::Dismiss($rayHwnd)

    Write-Host ("  run {0,2}: clip {1,7} ms   raycast {2,7} ms" -f $i,
        $(if ($c -ge 0) { "{0:F1}" -f $c } else { "miss" }),
        $(if ($r -ge 0) { "{0:F1}" -f $r } else { "miss" }))
}

$payload = [pscustomobject]@{
    Label = $Label
    TakenUtc = (Get-Date).ToUniversalTime().ToString("o")
    Commit = (& git -C $root rev-parse --short HEAD 2>$null)
    Runs = $Runs
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
