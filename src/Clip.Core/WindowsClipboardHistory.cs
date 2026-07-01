using Microsoft.Win32;

namespace Clip.Core;

/// <summary>
/// Turns on the built-in Windows clipboard history (the <c>Win+V</c> feature) so Clip can import it
/// and Windows keeps capturing a history even when Clip isn't running. Windows exposes no API to
/// enable it, so we set the documented HKCU flag the Clipboard service reads. All calls are
/// best-effort and never throw.
/// </summary>
public static class WindowsClipboardHistory
{
    private const string KeyPath = @"Software\Microsoft\Clipboard";
    private const string ValueName = "EnableClipboardHistory";
    private const string PolicyKeyPath = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string PolicyValueName = "AllowClipboardHistory";

    /// <summary>
    /// Pure decision (no registry I/O, so it is unit-tested): should we write EnableClipboardHistory=1?
    /// </summary>
    /// <param name="current">HKCU EnableClipboardHistory (null = unset).</param>
    /// <param name="policy">HKLM/HKCU AllowClipboardHistory group policy (null = not set).</param>
    internal static bool ShouldEnable(int? current, int? policy)
    {
        if (policy == 0) return false;   // a group policy forces the feature off — we can't override it
        return current != 1;             // otherwise enable it unless the user already has it on
    }

    /// <summary>Enables Windows clipboard history if it isn't already on. Returns true if it is on afterwards.</summary>
    public static bool EnsureEnabled()
    {
        try
        {
            var policy = ReadDword(Registry.LocalMachine, PolicyKeyPath, PolicyValueName)
                         ?? ReadDword(Registry.CurrentUser, PolicyKeyPath, PolicyValueName);
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            var current = key?.GetValue(ValueName) as int?;
            if (!ShouldEnable(current, policy))
            {
                return policy != 0 && current == 1; // already on (true) or policy-blocked (false)
            }

            key!.SetValue(ValueName, 1, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as int?;
        }
        catch
        {
            return null;
        }
    }
}
