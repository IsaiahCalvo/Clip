using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Clip.Core;

[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    public const bool DefaultEnabled = true;
    internal const string RunValueName = "Clip";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WatcherExecutableName = "Clip.Watcher.exe";
    private const string WatcherArguments = "watch";

    /// <summary>
    /// Optional informational logging sink. Hosts (e.g. Clip.Shell) can wire this to their
    /// own logger. Left null so Clip.Core carries no WPF/Shell dependency.
    /// </summary>
    public static Action<string>? InfoLog { get; set; }

    /// <summary>
    /// Optional error logging sink. Hosts can wire this to their own logger.
    /// </summary>
    public static Action<Exception, string>? ErrorLog { get; set; }

    public static bool IsEnabled() => IsEnabled(RunValueName);

    public static bool IsEnabled(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static string? CurrentValue() => CurrentValue(RunValueName);

    public static string? CurrentValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public static void SetEnabled(bool enabled) => SetEnabled(enabled, RunValueName, CurrentExecutablePath(), RemoveLegacyStartupShortcut);

    public static void SetEnabled(bool enabled, string valueName, string executablePath, Action? afterDisable = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open Windows startup registry key.");
        }

        if (enabled)
        {
            key.SetValue(valueName, StartupCommandFor(executablePath), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
        afterDisable?.Invoke();
    }

    public static bool MigrateToLightweightHostIfNeeded() => MigrateToLightweightHostIfNeeded(RunValueName, CurrentExecutablePath());

    internal static bool MigrateToLightweightHostIfNeeded(string valueName, string executablePath)
    {
        var current = CurrentValue(valueName);
        if (string.IsNullOrWhiteSpace(current) ||
            current.Contains(WatcherExecutableName, StringComparison.OrdinalIgnoreCase) ||
            !IsLegacyClipStartupValue(current))
        {
            return false;
        }

        var migrationSource = StartupExecutablePath(current) ?? executablePath;
        var desired = StartupCommandFor(migrationSource);
        if (!desired.Contains(WatcherExecutableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return false;
        }

        key.SetValue(valueName, desired, RegistryValueKind.String);
        RemoveLegacyStartupShortcut();
        InfoLog?.Invoke($"startup migrated to headless watcher value={desired}");
        return true;
    }

    private static string CurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        using var process = Process.GetCurrentProcess();
        path = process.MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        throw new InvalidOperationException("Could not find Clip executable path.");
    }

    private static string StartupCommandFor(string executablePath)
    {
        var watcherPath = WatcherPathFor(executablePath);
        if (!string.IsNullOrWhiteSpace(watcherPath))
        {
            return $"{Quote(watcherPath)} {WatcherArguments}";
        }

        return Quote(executablePath);
    }

    private static string? WatcherPathFor(string executablePath)
    {
        if (Path.GetFileName(executablePath).Equals(WatcherExecutableName, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(executablePath))
        {
            return executablePath;
        }

        var folder = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var siblingWatcher = Path.Combine(folder, WatcherExecutableName);
        return File.Exists(siblingWatcher) ? siblingWatcher : null;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static string? StartupExecutablePath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : null;
        }

        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }

    private static bool IsLegacyClipStartupValue(string value)
    {
        return value.Contains("Clip.exe", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Clip.Shell.exe", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Start-Clip.ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveLegacyStartupShortcut()
    {
        try
        {
            var startupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Clip.lnk");
            if (File.Exists(startupShortcut))
            {
                File.Delete(startupShortcut);
                InfoLog?.Invoke($"legacy startup shortcut removed path={startupShortcut}");
            }
        }
        catch (Exception ex)
        {
            ErrorLog?.Invoke(ex, "legacy startup shortcut remove failed");
        }
    }
}
