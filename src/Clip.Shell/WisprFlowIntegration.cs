using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clip.Shell;

internal static class WisprFlowIntegration
{
    public const string SourceName = "Wispr Flow";
    private const string WisprProcessPrefix = "Wispr Flow";

    public static bool IsWisprClipboardOwner()
    {
        try
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(owner, out var pid);
            if (pid == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.StartsWith(WisprProcessPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

internal sealed class WisprPasteWatcher : IDisposable
{
    private const int WhKeyboardLL = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint LlkhfInjected = 0x00000010;
    private const int VkV = 0x56;
    private const int VkInsert = 0x2D;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;

    private IntPtr _hook = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;
    private bool _sawInjectedPaste;
    private DateTimeOffset _startedAt;

    public bool SawInjectedPaste => _sawInjectedPaste;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _sawInjectedPaste = false;
        _startedAt = DateTimeOffset.UtcNow;
        _proc = HookCallback;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLL, _proc, GetModuleHandle(module.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((info.flags & LlkhfInjected) != 0)
            {
                var ctrlDown = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
                var shiftDown = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
                if ((info.vkCode == VkV && ctrlDown) || (info.vkCode == VkInsert && shiftDown))
                {
                    _sawInjectedPaste = true;
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
