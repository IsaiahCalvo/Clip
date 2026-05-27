using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clip.Core;
using Svg;
using DrawingImage = System.Drawing.Image;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;
using WpfImage = System.Windows.Controls.Image;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfListBoxItem = System.Windows.Controls.ListBoxItem;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfPath = System.Windows.Shapes.Path;
using WpfShape = System.Windows.Shapes.Shape;
using WinDataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using WinDataRequestedEventArgs = Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs;
using WinDataTransferManager = Windows.ApplicationModel.DataTransfer.DataTransferManager;
using WatcherAppChoice = Clip.Watcher.AppChoice;
using WatcherAppDiscovery = Clip.Watcher.AppDiscovery;
using WatcherAppLauncher = Clip.Watcher.AppLauncher;
using WatcherPackageLogoLookup = Clip.Watcher.PackageLogoLookup;
using WatcherPdfPreviewRenderer = Clip.Watcher.PdfPreviewRenderer;
using WatcherShellIconReader = Clip.Watcher.ShellIconReader;
using WatcherStartMenuIconLookup = Clip.Watcher.StartMenuIconLookup;
using WatcherStaticDocumentPreviewRenderer = Clip.Watcher.StaticDocumentPreviewRenderer;

namespace Clip.Shell;

internal enum ClipThemePreference
{
    System,
    Light,
    Dark,
}

internal enum AppIconPreference
{
    Light,
    Dark,
}

internal enum PasteFormatPreference
{
    PlainText,
    OriginalFormatting,
}

internal sealed class ClipShellSettings
{
    private const string ClipboardFolderName = "Clipboard History";
    private const string PreviousClipboardFolderName = "Clipboard";

    public ClipThemePreference Theme { get; set; } = ClipThemePreference.System;
    public AppIconPreference AppIcon { get; set; } = AppIconPreference.Light;
    public PasteFormatPreference DefaultPasteFormat { get; set; } = PasteFormatPreference.PlainText;
    public int? HistoryLimit { get; set; } = 500;
    public long? MaxItemSizeBytes { get; set; } = 50L * 1024 * 1024;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; } = true;
    public string? ClipboardFolderPath { get; set; }
    public ClipHotkeySettings Hotkeys { get; set; } = new();
    public ClipPrivacySettings Privacy { get; set; } = new();
    public List<string> AltVPasteApps { get; set; } = new();
    public List<ClipAppOverride> AppOverrides { get; set; } = new();

    public static string DefaultClipboardFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        ClipboardFolderName);

    public static string PreviousDefaultClipboardFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        PreviousClipboardFolderName);

    public string EffectiveClipboardFolderPath()
    {
        return string.IsNullOrWhiteSpace(ClipboardFolderPath) ? DefaultClipboardFolderPath : ClipboardFolderPath;
    }

    public void ResetToDefaults()
    {
        Theme = ClipThemePreference.System;
        AppIcon = AppIconPreference.Light;
        DefaultPasteFormat = PasteFormatPreference.PlainText;
        HistoryLimit = 500;
        MaxItemSizeBytes = 50L * 1024 * 1024;
        CheckForUpdatesOnStartup = true;
        InstallUpdatesAutomatically = true;
        ClipboardFolderPath = null;
        Hotkeys = new ClipHotkeySettings();
        Hotkeys.ResetToDefaults();
        Privacy = new ClipPrivacySettings();
        AltVPasteApps = new List<string>();
        AppOverrides = new List<ClipAppOverride>();
    }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "settings.json");

    public static ClipShellSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new ClipShellSettings();
            }

            var settings = JsonSerializer.Deserialize<ClipShellSettings>(File.ReadAllText(SettingsPath)) ?? new ClipShellSettings();
            settings.Hotkeys ??= new ClipHotkeySettings();
            settings.Hotkeys.Normalize();
            settings.Privacy ??= new ClipPrivacySettings();
            settings.Privacy.Normalize();
            settings.AltVPasteApps ??= new List<string>();
            settings.AppOverrides ??= new List<ClipAppOverride>();
            foreach (var entry in settings.AppOverrides)
            {
                entry.Action = ClipAppOverride.NormalizeAction(entry.Action);
            }
            if (settings.AltVPasteApps.Count > 0)
            {
                foreach (var legacy in settings.AltVPasteApps)
                {
                    if (string.IsNullOrWhiteSpace(legacy)) continue;
                    if (!settings.AppOverrides.Any(o => string.Equals(o.AppName, legacy, StringComparison.OrdinalIgnoreCase) && string.Equals(o.Action, ClipAppOverride.ActionPaste, StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.AppOverrides.Add(new ClipAppOverride { AppName = legacy, Action = ClipAppOverride.ActionPaste, Hotkey = "Alt+V" });
                    }
                }
                settings.AltVPasteApps.Clear();
            }
            if (string.Equals(settings.ClipboardFolderPath, PreviousDefaultClipboardFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                settings.ClipboardFolderPath = null;
            }

            return settings;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings load failed");
            return new ClipShellSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            ShellLog.Info($"settings saved path={SettingsPath} theme={Theme} appIcon={AppIcon} historyLimit={HistoryLimit?.ToString() ?? "Unlimited"} maxItemSize={ClipItemSizeLimit.MaxItemSizeLabel(MaxItemSizeBytes)} updateCheck={CheckForUpdatesOnStartup} autoInstall={InstallUpdatesAutomatically} clipboardFolder={EffectiveClipboardFolderPath()} openHotkey={Hotkeys.OpenClip} debugHotkey={Hotkeys.SaveDebugLog} excludedApps={Privacy.ExcludedApps.Count}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings save failed");
        }
    }
}

internal sealed class ClipAppOverride
{
    public const string ActionOpenClip = "Open Clip";
    public const string ActionPaste = "Paste";

    // Legacy action labels — migrated to ActionPaste on load.
    public const string LegacyActionPasteImage = "Paste image";
    public const string LegacyActionPasteText = "Paste text";
    public const string LegacyActionPasteFiles = "Paste files";
    public const string LegacyActionPasteLink = "Paste link";

    public static readonly string[] AvailableActions =
    {
        ActionOpenClip,
        ActionPaste,
    };

    public string AppName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string Action { get; set; } = ActionPaste;
    public string Hotkey { get; set; } = "Alt+V";

    public static string NormalizeAction(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Equals(ActionOpenClip, StringComparison.OrdinalIgnoreCase)) return ActionOpenClip;
        if (v.Equals(ActionPaste, StringComparison.OrdinalIgnoreCase)) return ActionPaste;
        if (v.Equals(LegacyActionPasteImage, StringComparison.OrdinalIgnoreCase)
            || v.Equals(LegacyActionPasteText, StringComparison.OrdinalIgnoreCase)
            || v.Equals(LegacyActionPasteFiles, StringComparison.OrdinalIgnoreCase)
            || v.Equals(LegacyActionPasteLink, StringComparison.OrdinalIgnoreCase))
        {
            return ActionPaste;
        }
        return ActionPaste;
    }
}

internal static class ClipItemSizeLimit
{
    public static bool Allows(ClipboardHistoryItem item, long? maxBytes)
    {
        if (maxBytes is null)
        {
            return true;
        }

        return EstimateBytes(item) <= Math.Max(0, maxBytes.Value);
    }

    public static long EstimateBytes(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color => TextBytes(item.Text) + TextBytes(item.HtmlText) + TextBytes(item.RtfText),
            ClipboardItemKind.Image => ExistingPathBytes(item.AssetPath),
            ClipboardItemKind.Files => item.FilePaths.Sum(TextBytes),
            _ => 0,
        };
    }

    private static long TextBytes(string? text)
    {
        return string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);
    }

    private static long ExistingPathBytes(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static string MaxItemSizeLabel(long? bytes)
    {
        if (bytes is null)
        {
            return "Unlimited";
        }

        return $"{Math.Max(0, bytes.Value) / 1024 / 1024} MB";
    }
}

internal sealed class ClipPrivacySettings
{
    public List<ClipExcludedApp> ExcludedApps { get; set; } = [];

    public void AddExcludedApp(string name, string? executablePath)
    {
        var app = ClipExcludedApp.Create(name, executablePath);
        if (app is null || ExcludedApps.Any(existing => existing.MatchesEntry(app)))
        {
            return;
        }

        ExcludedApps.Add(app);
    }

    public void RemoveExcludedApp(ClipExcludedApp app)
    {
        ExcludedApps.RemoveAll(existing => existing.MatchesEntry(app));
    }

    public void RemoveExcludedApp(string name, string? executablePath)
    {
        var app = ClipExcludedApp.Create(name, executablePath);
        if (app is null)
        {
            return;
        }

        RemoveExcludedApp(app);
    }

    public bool IsExcluded(string? sourceName, string? sourcePath)
    {
        return ExcludedApps.Any(app => app.MatchesSource(sourceName, sourcePath));
    }

    public void Normalize()
    {
        ExcludedApps = ExcludedApps
            .Concat(MigrateLegacyExcludedApps())
            .Select(app => ClipExcludedApp.Create(app.Name, app.ExecutablePath))
            .Where(app => app is not null)
            .Select(app => app!)
            .DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ClipExcludedApp> MigrateLegacyExcludedApps()
    {
        var migrated = new List<ClipExcludedApp>();
        if (ExcludedApps.Count > 0)
        {
            return migrated;
        }

        // Older builds stored this as a string array. Keep reading it so users do not lose exclusions.
        try
        {
            var json = File.Exists(ClipShellSettings.SettingsPath) ? File.ReadAllText(ClipShellSettings.SettingsPath) : "";
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Privacy", out var privacy) ||
                !privacy.TryGetProperty("ExcludedApps", out var apps) ||
                apps.ValueKind != JsonValueKind.Array)
            {
                return migrated;
            }

            foreach (var app in apps.EnumerateArray())
            {
                if (app.ValueKind == JsonValueKind.String)
                {
                    var entry = ClipExcludedApp.Create(app.GetString(), null);
                    if (entry is not null)
                    {
                        migrated.Add(entry);
                    }
                }
            }
        }
        catch
        {
        }

        return migrated;
    }
}

internal sealed class ClipExcludedApp
{
    public string Name { get; set; } = "";
    public string? ExecutablePath { get; set; }

    public string Key => NormalizePath(ExecutablePath) ?? NormalizeName(Name) ?? Name;

    public static ClipExcludedApp? Create(string? name, string? executablePath)
    {
        var path = NormalizeEntry(executablePath);
        var displayName = NormalizeEntry(name) ?? Path.GetFileNameWithoutExtension(path);
        if (displayName is null)
        {
            return null;
        }

        return new ClipExcludedApp
        {
            Name = displayName,
            ExecutablePath = path,
        };
    }

    public bool MatchesEntry(ClipExcludedApp other)
    {
        return string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesSource(string? sourceName, string? sourcePath)
    {
        var sourceNameKey = NormalizeName(sourceName);
        var sourcePathKey = NormalizePath(sourcePath);
        var sourcePathNameKey = NormalizeName(Path.GetFileNameWithoutExtension(sourcePath));
        var appPathKey = NormalizePath(ExecutablePath);
        var appNameKey = NormalizeName(Name);
        var appPathNameKey = NormalizeName(Path.GetFileNameWithoutExtension(ExecutablePath));

        return (!string.IsNullOrWhiteSpace(appPathKey) && string.Equals(appPathKey, sourcePathKey, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(appNameKey) &&
                (string.Equals(appNameKey, sourceNameKey, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(appNameKey, sourcePathNameKey, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrWhiteSpace(appPathNameKey) && string.Equals(appPathNameKey, sourceNameKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeEntry(string? value)
    {
        var trimmed = value?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? NormalizeName(string? value)
    {
        var normalized = NormalizeEntry(value);
        return normalized is null ? null : Path.GetFileNameWithoutExtension(normalized);
    }

    private static string? NormalizePath(string? value)
    {
        var normalized = NormalizeEntry(value);
        return normalized is null || !Path.IsPathRooted(normalized) ? null : Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal sealed record ClipboardPastePayload(string Text, string? Html, string? Rtf);

internal static class ClipboardPasteData
{
    public static bool HasOriginalFormatting(ClipboardHistoryItem item)
    {
        return !string.IsNullOrWhiteSpace(item.HtmlText) || !string.IsNullOrWhiteSpace(item.RtfText);
    }

    public static ClipboardPastePayload Create(ClipboardHistoryItem item, PasteFormatPreference preference)
    {
        var text = item.Text ?? item.Preview ?? string.Empty;
        if (preference != PasteFormatPreference.OriginalFormatting)
        {
            return new ClipboardPastePayload(text, null, null);
        }

        return new ClipboardPastePayload(
            text,
            string.IsNullOrWhiteSpace(item.HtmlText) ? null : item.HtmlText,
            string.IsNullOrWhiteSpace(item.RtfText) ? null : item.RtfText);
    }
}

internal sealed class ClipHotkeySettings
{
    public string OpenClip { get; set; } = ClipHotkeyDefaults.OpenClip;
    public string PasteSelected { get; set; } = ClipHotkeyDefaults.PasteSelected;
    public string CopySelected { get; set; } = ClipHotkeyDefaults.CopySelected;
    public string PinSelected { get; set; } = ClipHotkeyDefaults.PinSelected;
    public string OpenActions { get; set; } = ClipHotkeyDefaults.OpenActions;
    public string OpenSelected { get; set; } = ClipHotkeyDefaults.OpenSelected;
    public string EditSelected { get; set; } = ClipHotkeyDefaults.EditSelected;
    public string SaveDebugLog { get; set; } = ClipHotkeyDefaults.SaveDebugLog;
    public string DeleteSelected { get; set; } = ClipHotkeyDefaults.DeleteSelected;
    public string CloseClip { get; set; } = ClipHotkeyDefaults.CloseClip;

    public void ResetToDefaults()
    {
        OpenClip = ClipHotkeyDefaults.OpenClip;
        PasteSelected = ClipHotkeyDefaults.PasteSelected;
        CopySelected = ClipHotkeyDefaults.CopySelected;
        PinSelected = ClipHotkeyDefaults.PinSelected;
        OpenActions = ClipHotkeyDefaults.OpenActions;
        OpenSelected = ClipHotkeyDefaults.OpenSelected;
        EditSelected = ClipHotkeyDefaults.EditSelected;
        SaveDebugLog = ClipHotkeyDefaults.SaveDebugLog;
        DeleteSelected = ClipHotkeyDefaults.DeleteSelected;
        CloseClip = ClipHotkeyDefaults.CloseClip;
    }

    public void Normalize()
    {
        OpenClip = NormalizeGlobal(OpenClip, ClipHotkeyDefaults.OpenClip);
        PasteSelected = NormalizeLocal(PasteSelected, ClipHotkeyDefaults.PasteSelected);
        CopySelected = NormalizeLocal(CopySelected, ClipHotkeyDefaults.CopySelected);
        PinSelected = NormalizeLocal(PinSelected, ClipHotkeyDefaults.PinSelected);
        OpenActions = NormalizeLocal(OpenActions, ClipHotkeyDefaults.OpenActions);
        OpenSelected = NormalizeLocal(OpenSelected, ClipHotkeyDefaults.OpenSelected);
        EditSelected = NormalizeLocal(EditSelected, ClipHotkeyDefaults.EditSelected);
        SaveDebugLog = NormalizeGlobal(SaveDebugLog, ClipHotkeyDefaults.SaveDebugLog);
        DeleteSelected = NormalizeLocal(DeleteSelected, ClipHotkeyDefaults.DeleteSelected);
        CloseClip = NormalizeLocal(CloseClip, ClipHotkeyDefaults.CloseClip);
    }

    private static string NormalizeLocal(string value, string fallback)
        => ClipHotkeyGesture.TryParse(value, out var gesture) ? gesture.DisplayText : fallback;

    private static string NormalizeGlobal(string value, string fallback)
        => ClipHotkeyGesture.TryParseGlobal(value, out var gesture) ? gesture.DisplayText : fallback;
}

internal static class ClipHotkeyDefaults
{
    public const string OpenClip = "Alt+V";
    public const string PasteSelected = "Enter";
    public const string CopySelected = "Ctrl+C";
    public const string PinSelected = "Ctrl+P";
    public const string OpenActions = "Ctrl+K";
    public const string OpenSelected = "Ctrl+O";
    public const string EditSelected = "Ctrl+E";
    public const string SaveDebugLog = "Ctrl+Shift+L";
    public const string DeleteSelected = "Delete";
    public const string CloseClip = "Esc";
}

internal readonly record struct ClipHotkeyGesture(int WinModifiers, int VirtualKey, ModifierKeys WpfModifiers, Key WpfKey, string DisplayText)
{
    private const int WinModAlt = 0x0001;
    private const int WinModControl = 0x0002;
    private const int WinModShift = 0x0004;
    private const int WinModWindows = 0x0008;

    public static bool TryParse(string? text, out ClipHotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var winModifiers = 0;
        var wpfModifiers = ModifierKeys.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "ALT":
                    winModifiers |= WinModAlt;
                    wpfModifiers |= ModifierKeys.Alt;
                    break;
                case "CTRL":
                case "CONTROL":
                    winModifiers |= WinModControl;
                    wpfModifiers |= ModifierKeys.Control;
                    break;
                case "SHIFT":
                    winModifiers |= WinModShift;
                    wpfModifiers |= ModifierKeys.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    winModifiers |= WinModWindows;
                    wpfModifiers |= ModifierKeys.Windows;
                    break;
                default:
                    return false;
            }
        }

        if (!TryKey(parts[^1], out var key))
        {
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            return false;
        }

        gesture = new ClipHotkeyGesture(winModifiers, virtualKey, wpfModifiers, key, Format(wpfModifiers, key));
        return true;
    }

    public static bool TryParseGlobal(string? text, out ClipHotkeyGesture gesture)
    {
        return TryParse(text, out gesture) && gesture.WpfModifiers != ModifierKeys.None;
    }

    private static bool TryKey(string text, out Key key)
    {
        key = Key.None;
        if (text.Length == 1 && char.IsLetterOrDigit(text[0]) && Enum.TryParse("D" + char.ToUpperInvariant(text[0]), out key))
        {
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out key) && key is not Key.None;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyDisplay(key));
        return string.Join("+", parts);
    }

    private static string KeyDisplay(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        return key >= Key.A && key <= Key.Z ? key.ToString() : key.ToString();
    }
}

internal static class StartupRegistration
{
    public const bool DefaultEnabled = true;
    internal const string RunValueName = "Clip";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

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
            key.SetValue(valueName, Quote(executablePath), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
        afterDisable?.Invoke();
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

    private static string Quote(string path) => $"\"{path}\"";

    private static void RemoveLegacyStartupShortcut()
    {
        try
        {
            var startupShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Clip.lnk");
            if (File.Exists(startupShortcut))
            {
                File.Delete(startupShortcut);
                ShellLog.Info($"legacy startup shortcut removed path={startupShortcut}");
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "legacy startup shortcut remove failed");
        }
    }
}

public partial class MainWindow : Window
{
    private const int OpenHotkeyId = 0x4350;
    private const int DebugLogHotkeyId = 0x4351;
    private const int OpenOverrideHotkeyId = 0x4352;
    private const int WmHotkey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int DwmwaWindowCornerPreference = 33;
    private static readonly Dictionary<string, ImageSource> SvgImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SvgTextCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SvgCacheGate = new();

    private readonly ClipShellSettings _settings = ClipShellSettings.Load();
    private readonly ClipboardHistoryStore _store;
    private readonly ClipUpdateService _updates = new();
    private readonly Dictionary<string, Border> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly System.Windows.Threading.DispatcherTimer _hotkeyRetryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Threading.DispatcherTimer _outsideClickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Windows.Threading.DispatcherTimer _clipboardSettleTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly System.Windows.Threading.DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromHours(4) };
    private IReadOnlyList<ClipboardHistoryItem> _allItems = [];
    private ClipboardHistoryItem? _selected;
    private ClipboardHistoryItem? _pendingTextClipboardItem;
    private HwndSource? _source;
    private bool _openHotkeyRegistered;
    private bool _debugLogHotkeyRegistered;
    private bool _openOverrideRegistered;
    private string? _activeOpenOverrideApp;
    private string? _activeOpenOverrideHotkey;
    private string _kindFilter = "all";
    private string _dateFilter = "all";
    private string _fileFilter = "all";
    private int _previewToken;
    private bool _suppressDeactivate;
    private bool _updateCheckInProgress;
    private string? _promptedUpdateVersion;
    private bool _itemsDirtySinceRender = true;
    private bool _paletteRequested;
    private bool _paletteNoActivate;
    private IntPtr _returnFocusHwnd;
    private IntPtr _returnFocusChildHwnd;
    private AutomationElement? _returnFocusElement;
    private string _returnFocusElementSummary = "none";
    private string? _returnFocusValueBefore;
    private bool _returnFocusCommitsPasteWithEnter;
    private ClipboardHistoryItem? _menuItem;
    private bool _expandedImagePanning;
    private System.Windows.Point _expandedImageLastPoint;
    private System.Windows.Point _expandedImageDownPoint;
    private bool _expandedImageMoved;
    private bool _expandedImageDownOnImage;
    private double _expandedImageZoom = 1.0;
    private double _expandedImageNaturalWidth = 1.0;
    private double _expandedImageNaturalHeight = 1.0;
    private Rect _expandedRestoreBounds;
    private CornerRadius _expandedRestoreCornerRadius;
    private bool _expandedWindowResized;
    private string? _currentPreviewImagePath;
    private string? _currentPreviewPdfPath;
    private ClipUpdateStatus _lastUpdateStatus = ClipUpdateStatus.NotChecked(ClipUpdateService.CurrentVersion);
    public bool KeepOpenForDebug { get; set; }
    internal ClipUpdateStatus LastUpdateStatus => _lastUpdateStatus;
    internal AppIconPreference AppIconPreference => _settings.AppIcon;
    internal event Action<AppIconPreference>? AppIconChanged;
    internal event Action<string>? UserNotificationRequested;

    public MainWindow()
    {
        _store = new ClipboardHistoryStore(contentRootPath: _settings.EffectiveClipboardFolderPath());
        InitializeComponent();
        RenderOptions.SetClearTypeHint(Shell, ClearTypeHint.Enabled);
        ApplyTheme(_settings.Theme, save: false);
        ApplyAppIcon(_settings.AppIcon, save: false);
        Opacity = 0;
        RefreshChromeIcons();
        TitleText.Cursor = System.Windows.Input.Cursors.IBeam;
        TitleText.ToolTip = "Double-click to rename";
        TitleText.MouseLeftButtonDown += OnTitleTextMouseLeftButtonDown;
        TitleText.Foreground = (WpfBrush)FindResource("Text");
        SubTitleText.Foreground = (WpfBrush)FindResource("Muted");
        TitleText.MouseEnter += (_, _) => TitleText.Foreground = (WpfBrush)FindResource("Accent");
        TitleText.MouseLeave += (_, _) => TitleText.Foreground = (WpfBrush)FindResource("Text");
        AllFilterShell.MouseEnter += (_, _) =>
        {
            AllFilterShell.Background = (WpfBrush)FindResource("AccentSoft");
            AllFilterShell.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
        };
        AllFilterShell.MouseLeave += (_, _) => UpdateFilterVisuals();
        FilesFilterShell.MouseEnter += (_, _) =>
        {
            FilesFilterShell.Background = (WpfBrush)FindResource("AccentSoft");
            FilesFilterShell.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
        };
        FilesFilterShell.MouseLeave += (_, _) => UpdateFilterVisuals();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _hotkeyRetryTimer.Tick += (_, _) => EnsureHotkeyRegistered("retry");
        _outsideClickTimer.Tick += (_, _) => HideIfMousePressedOutsidePalette();
        _clipboardSettleTimer.Tick += (_, _) =>
        {
            _clipboardSettleTimer.Stop();
            SavePendingTextClipboardItem();
        };
        _updateCheckTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(showToastWhenCurrent: false);
    }

    public void InitializeShell()
    {
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            var hwnd = new WindowInteropHelper(this).Handle;
            ApplyRoundedWindowCorners(hwnd);
            var hotkey = EnsureHotkeyRegistered("startup");
            var listener = AddClipboardFormatListener(hwnd);
            InstallForegroundHook();
            ShellLog.Info($"window initialized hwnd={hwnd} hotkey={hotkey} listener={listener} win32={Marshal.GetLastWin32Error()}");
        };

        Loaded += async (_, _) =>
        {
            LoadItems(selectFirst: true, reason: "startup");
            MoveOffscreen();
            Opacity = 1;
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            if (_paletteRequested || KeepOpenForDebug)
            {
                ShowPalette();
            }
            else
            {
                ConcealPalette("startup");
            }

            ShellLog.Info("window pre-rendered while hidden");
            _ = WarmHtmlPreviewAsync();
            if (_settings.CheckForUpdatesOnStartup)
            {
                _ = CheckForUpdatesAsync(showToastWhenCurrent: false);
            }
            ApplyUpdateCheckSchedule();

            OpenWithWindow.WarmCacheAsync();
            ClipboardSharePayload.CleanupStaleTemporaryFiles();
        };

        Closing += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeyRetryTimer.Stop();
            _updateCheckTimer.Stop();
            if (_openHotkeyRegistered)
            {
                var released = UnregisterHotKey(hwnd, OpenHotkeyId);
                ShellLog.Info($"open hotkey unregistered={released} hwnd={hwnd} win32={Marshal.GetLastWin32Error()}");
                _openHotkeyRegistered = false;
            }

            if (_debugLogHotkeyRegistered)
            {
                var released = UnregisterHotKey(hwnd, DebugLogHotkeyId);
                ShellLog.Info($"debug log hotkey unregistered={released} hwnd={hwnd} win32={Marshal.GetLastWin32Error()}");
                _debugLogHotkeyRegistered = false;
            }

            if (_openOverrideRegistered)
            {
                UnregisterHotKey(hwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
            }

            UninstallForegroundHook();
            RemoveClipboardFormatListener(hwnd);
            ShellLog.Info("window closing");
        };

        Show();
    }

    public void ShowPalette()
    {
        _paletteRequested = true;
        var watch = Stopwatch.StartNew();
        var ownHwnd = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != ownHwnd)
        {
            CaptureReturnFocus(foreground);
        }

        _paletteNoActivate = ShouldShowPaletteWithoutActivation(_returnFocusHwnd, _returnFocusElement);
        ApplyNoActivatePaletteStyle(_paletteNoActivate);

        if (!IsVisible)
        {
            Show();
        }

        Opacity = 0;
        IsHitTestVisible = false;
        PositionOnMouseScreen();
        UpdateLayout();
        Opacity = 1;
        IsHitTestVisible = true;
        _outsideClickTimer.Start();
        ShellLog.Info($"palette shown elapsedMs={watch.ElapsedMilliseconds} selected={_selected?.Id ?? "none"} rows={_rows.Count} dirty={_itemsDirtySinceRender} noActivate={_paletteNoActivate}");

        if (_itemsDirtySinceRender || _rows.Count == 0)
        {
            Dispatcher.BeginInvoke(() => LoadItems(selectFirst: _selected is null, reason: "show-refresh"), System.Windows.Threading.DispatcherPriority.Background);
        }

        PromptForKnownUpdate();
    }

    public void CheckForUpdatesFromTray()
    {
        _ = CheckForUpdatesAsync(showToastWhenCurrent: true, promptIfAvailable: true);
    }

    public void InstallKnownUpdateFromTray()
    {
        if (IsUpdateAvailable(_lastUpdateStatus))
        {
            _ = InstallUpdateAsync(_lastUpdateStatus);
            return;
        }

        CheckForUpdatesFromTray();
    }

    private void ConcealPalette(string reason)
    {
        _outsideClickTimer.Stop();
        Opacity = 0;
        IsHitTestVisible = false;
        MoveOffscreen();
        ShellLog.Info($"palette concealed reason={reason}");
    }

    private void HideIfMousePressedOutsidePalette()
    {
        if (Opacity <= 0 || !IsHitTestVisible)
        {
            _outsideClickTimer.Stop();
            return;
        }

        if (KeepOpenForDebug || _suppressDeactivate || ActionMenuPopup.IsOpen || IsContextMenuOpen(this))
        {
            return;
        }

        if (Forms.Control.MouseButtons == Forms.MouseButtons.None)
        {
            return;
        }

        var mouse = Forms.Control.MousePosition;
        var point = PointFromScreen(new System.Windows.Point(mouse.X, mouse.Y));
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (!bounds.Contains(point))
        {
            ConcealPalette("outside-click");
            ShellLog.Info("palette hidden on outside click");
        }
    }

    private void MoveOffscreen()
    {
        Left = SystemParameters.VirtualScreenLeft - Math.Max(ActualWidth, Width) - 100;
        Top = SystemParameters.VirtualScreenTop - Math.Max(ActualHeight, Height) - 100;
    }

    public void WriteDebugSnapshot(string reason = "hotkey")
    {
        ShellLog.Info("=== Snapshot ===");
        ShellLog.Info($"reason={reason} visible={IsVisible} selected={_selected?.Id ?? "none"} kind={_selected?.Kind.ToString() ?? "none"} filter={_kindFilter} date={_dateFilter} file={_fileFilter}");
        ShellLog.Info($"items all={_allItems.Count} renderedRows={_rows.Count} search={SearchBox.Text}");
        if (_selected is not null)
        {
            ShellLog.Info($"selected preview={_selected.Preview} pinned={_selected.IsPinned} source={_selected.SourceApplication} path={_selected.SourceApplicationPath}");
        }

            ShellLog.Info($"scroll listV={ListScroll.VerticalOffset}/{ListScroll.ScrollableHeight} listH={ListScroll.HorizontalOffset}/{ListScroll.ScrollableWidth} info={InfoScroll.VerticalOffset}/{InfoScroll.ScrollableHeight}");
            ShellLog.Info($"ui popupOpen={ActionMenuPopup.IsOpen} rows={ItemsHost.Children.Count} infoRows={InfoHost.Children.Count}");
            ShellLog.Info("=== End Snapshot ===");
            ShowToast("Clip log saved");
        }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate && _paletteNoActivate)
        {
            handled = true;
            return new IntPtr(MouseActivateNoActivate);
        }

        if (ExpandedImageOverlay.Visibility == Visibility.Visible && (msg == WmMouseWheel || msg == WmMouseHWheel))
        {
            var delta = WheelDelta(wParam);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ZoomExpandedImage(Math.Pow(1.0018, delta), MousePointInExpandedViewport());
            }
            else if (msg == WmMouseHWheel)
            {
                PanExpandedImage(-delta, 0);
            }
            else
            {
                PanExpandedImage(0, delta);
            }

            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == OpenHotkeyId)
        {
            ShellLog.Info($"{_settings.Hotkeys.OpenClip} received");
            ShowPalette();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == OpenOverrideHotkeyId)
        {
            ShellLog.Info($"open override hotkey received key={_activeOpenOverrideHotkey ?? "?"} app={_activeOpenOverrideApp ?? "?"}");
            ShowPalette();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == DebugLogHotkeyId)
        {
            ShellLog.Info($"{_settings.Hotkeys.SaveDebugLog} received");
            WriteDebugSnapshot("global-hotkey");
            handled = true;
        }
        else if (msg == WmClipboardUpdate)
        {
            CaptureClipboard();
        }

        return IntPtr.Zero;
    }

    private bool EnsureHotkeyRegistered(string reason)
    {
        if (_openHotkeyRegistered && _debugLogHotkeyRegistered)
        {
            _hotkeyRetryTimer.Stop();
            return true;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            ShellLog.Info($"hotkey skipped reason={reason} hwnd=0");
            return false;
        }

        _openHotkeyRegistered = RegisterConfiguredHotkey(hwnd, OpenHotkeyId, _settings.Hotkeys.OpenClip, ClipHotkeyDefaults.OpenClip, "open", reason);
        _debugLogHotkeyRegistered = RegisterConfiguredHotkey(hwnd, DebugLogHotkeyId, _settings.Hotkeys.SaveDebugLog, ClipHotkeyDefaults.SaveDebugLog, "debug-log", reason);

        if (_openHotkeyRegistered && _debugLogHotkeyRegistered)
        {
            _hotkeyRetryTimer.Stop();
        }
        else if (!_hotkeyRetryTimer.IsEnabled)
        {
            _hotkeyRetryTimer.Start();
        }

        return _openHotkeyRegistered && _debugLogHotkeyRegistered;
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 3;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private WinEventDelegate? _foregroundHookDelegate;
    private IntPtr _foregroundHook = IntPtr.Zero;

    private void InstallForegroundHook()
    {
        if (_foregroundHook != IntPtr.Zero) return;
        _foregroundHookDelegate = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundHookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        ShellLog.Info($"foreground hook installed handle={_foregroundHook}");
        ApplyForegroundOverride(GetForegroundWindow());
    }

    private void UninstallForegroundHook()
    {
        if (_foregroundHook == IntPtr.Zero) return;
        UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundHookDelegate = null;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0) return;
        if (hwnd == IntPtr.Zero) return;
        var own = new WindowInteropHelper(this).Handle;
        if (hwnd == own) return;
        ApplyForegroundOverride(hwnd);
    }

    private void ApplyForegroundOverride(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var processName = TryGetProcessNameForWindow(hwnd);
        if (string.IsNullOrEmpty(processName)) return;
        using (var self = Process.GetCurrentProcess())
        {
            if (string.Equals(processName, self.ProcessName, StringComparison.OrdinalIgnoreCase)) return;
        }

        var match = _settings.AppOverrides.FirstOrDefault(o =>
            string.Equals(o.Action, ClipAppOverride.ActionOpenClip, StringComparison.OrdinalIgnoreCase)
            && string.Equals(StripExeSuffix(o.AppName), processName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(o.Hotkey));

        var mainHwnd = new WindowInteropHelper(this).Handle;
        if (mainHwnd == IntPtr.Zero) return;

        if (match is not null)
        {
            if (_openOverrideRegistered && string.Equals(_activeOpenOverrideHotkey, match.Hotkey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (_openOverrideRegistered)
            {
                UnregisterHotKey(mainHwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
            }
            if (_openHotkeyRegistered)
            {
                UnregisterHotKey(mainHwnd, OpenHotkeyId);
                _openHotkeyRegistered = false;
            }
            if (ClipHotkeyGesture.TryParseGlobal(match.Hotkey, out var gesture))
            {
                var ok = RegisterHotKey(mainHwnd, OpenOverrideHotkeyId, gesture.WinModifiers, gesture.VirtualKey);
                if (ok)
                {
                    _openOverrideRegistered = true;
                    _activeOpenOverrideApp = processName;
                    _activeOpenOverrideHotkey = match.Hotkey;
                    ShellLog.Info($"open override registered app={processName} key={match.Hotkey}");
                    return;
                }
                ShellLog.Info($"open override register failed app={processName} key={match.Hotkey} win32={Marshal.GetLastWin32Error()}");
            }
            EnsureHotkeyRegistered("override-fallback");
        }
        else
        {
            if (_openOverrideRegistered)
            {
                UnregisterHotKey(mainHwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
                _activeOpenOverrideApp = null;
                _activeOpenOverrideHotkey = null;
                ShellLog.Info("open override cleared");
            }
            if (!_openHotkeyRegistered)
            {
                EnsureHotkeyRegistered("foreground-default");
            }
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private static bool RegisterConfiguredHotkey(IntPtr hwnd, int id, string configured, string fallback, string name, string reason)
    {
        if (!ClipHotkeyGesture.TryParseGlobal(configured, out var gesture) && !ClipHotkeyGesture.TryParseGlobal(fallback, out gesture))
        {
            ShellLog.Info($"hotkey register skipped name={name} configured={configured} reason={reason}");
            return false;
        }

        var registered = RegisterHotKey(hwnd, id, gesture.WinModifiers, gesture.VirtualKey);
        var win32 = Marshal.GetLastWin32Error();
        ShellLog.Info($"hotkey register name={name} key={gesture.DisplayText} reason={reason} registered={registered} hwnd={hwnd} win32={win32}");
        return registered;
    }

    private void CaptureClipboard()
    {
        try
        {
            ClipboardHistoryItem? item = null;
            var source = ForegroundSource();
            if (_settings.Privacy.IsExcluded(source.Name, source.Path))
            {
                ShellLog.Info($"clipboard skipped excluded source={source.Name} path={source.Path}");
                return;
            }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var files = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
                item = new ClipboardHistoryItem
                {
                    Kind = ClipboardItemKind.Files,
                    FilePaths = files,
                    Preview = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files",
                    SourceApplication = source.Name,
                    SourceApplicationPath = source.Path,
                };
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image is not null)
                {
                    var path = _store.NewAssetFilePath(".png");
                    using var file = File.Create(path);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(file);
                    item = new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Image,
                        AssetPath = path,
                        Preview = $"Image {image.PixelWidth} x {image.PixelHeight}",
                        ImageWidth = image.PixelWidth,
                        ImageHeight = image.PixelHeight,
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                    };
                }
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                var htmlText = ClipboardTextOrNull(System.Windows.TextDataFormat.Html);
                var rtfText = ClipboardTextOrNull(System.Windows.TextDataFormat.Rtf);
                if (TryNormalizeColorText(text, source.Name, out var colorHex))
                {
                    item = new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Color,
                        Text = colorHex,
                        Preview = colorHex,
                        ContentHash = HashText(colorHex),
                        HtmlText = htmlText,
                        RtfText = rtfText,
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                    };
                }
                else
                {
                    item = new ClipboardHistoryItem
                    {
                        Kind = ClipboardLinkDetector.IsLinkOrEmail(text) ? ClipboardItemKind.Link : ClipboardItemKind.Text,
                        Text = text,
                        Preview = ClipboardHistoryStore.PreviewText(text),
                        ContentHash = HashText(text),
                        HtmlText = htmlText,
                        RtfText = rtfText,
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                    };
                }
            }

            if (item is null)
            {
                return;
            }

            CaptureClipboardItem(item);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "clipboard capture failed");
        }
    }

    private void CaptureClipboardItem(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            _pendingTextClipboardItem = item;
            _clipboardSettleTimer.Stop();
            _clipboardSettleTimer.Start();
            ShellLog.Info($"clipboard text pending kind={item.Kind} source={item.SourceApplication} preview={item.Preview}");
            return;
        }

        DropPendingTextClipboardItem("replaced-before-settle");
        SaveClipboardItem(item, "clipboard-live");
    }

    private void SavePendingTextClipboardItem()
    {
        var pending = _pendingTextClipboardItem;
        _pendingTextClipboardItem = null;
        if (pending is null)
        {
            return;
        }

        if (!ClipboardStillContains(pending))
        {
            ShellLog.Info($"clipboard text skipped transient source={pending.SourceApplication} preview={pending.Preview}");
            return;
        }

        SaveClipboardItem(pending, "clipboard-live");
    }

    private void DropPendingTextClipboardItem(string reason)
    {
        if (_pendingTextClipboardItem is null)
        {
            return;
        }

        ShellLog.Info($"clipboard text skipped reason={reason} source={_pendingTextClipboardItem.SourceApplication} preview={_pendingTextClipboardItem.Preview}");
        _pendingTextClipboardItem = null;
        _clipboardSettleTimer.Stop();
    }

    private void SaveClipboardItem(ClipboardHistoryItem item, string renderReason)
    {
        if (!ClipItemSizeLimit.Allows(item, _settings.MaxItemSizeBytes))
        {
            var itemBytes = ClipItemSizeLimit.EstimateBytes(item);
            ShellLog.Info($"clipboard skipped oversized kind={item.Kind} bytes={itemBytes} limit={ClipItemSizeLimit.MaxItemSizeLabel(_settings.MaxItemSizeBytes)} source={item.SourceApplication} preview={item.Preview}");
            DeleteUnsavedCaptureAsset(item);
            ShowToast("Clipboard item skipped: too large");
            return;
        }

        var saved = _store.AddOrUpdate(item, EffectiveHistoryLimit());
        ShellLog.Info($"clipboard captured id={saved.Id} kind={saved.Kind} source={saved.SourceApplication} preview={saved.Preview}");
        _allItems = _store.QueryItems();
        if (IsVisible)
        {
            RenderItems(reason: renderReason);
        }
        else
        {
            _itemsDirtySinceRender = true;
        }
    }

    private void DeleteUnsavedCaptureAsset(ClipboardHistoryItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.AssetPath) ||
                !Path.GetFullPath(item.AssetPath).StartsWith(Path.GetFullPath(_store.ContentRootPath), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(item.AssetPath))
            {
                return;
            }

            File.Delete(item.AssetPath);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"oversized capture cleanup failed path={item.AssetPath}");
        }
    }

    private static bool ClipboardStillContains(ClipboardHistoryItem item)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return false;
            }

            return string.Equals(System.Windows.Clipboard.GetText(), item.Text, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void LoadItems(bool selectFirst, string reason)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            _allItems = _store.QueryItems(SearchBox.Text);
            RenderItems(reason);
            _itemsDirtySinceRender = false;
            if (selectFirst && _selected is null)
            {
                SelectItem(FilteredItems().FirstOrDefault(), reason: "initial");
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"load items failed reason={reason}");
        }
        finally
        {
            ShellLog.Info($"load items reason={reason} count={_allItems.Count} elapsedMs={watch.ElapsedMilliseconds}");
        }
    }

    private void RenderItems(string reason)
    {
        var selectedId = _selected?.Id;
        ItemsHost.Children.Clear();
        _rows.Clear();
        UpdateFilterVisuals();

        foreach (var group in GroupItems(FilteredItems()))
        {
            if (group.Items.Count == 0)
            {
                continue;
            }

            var header = new TextBlock
            {
                Text = $"{group.Header.ToUpperInvariant()}  {group.Items.Count}",
                Foreground = (WpfBrush)FindResource("Muted"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(16, 12, 8, 4),
            };
            ItemsHost.Children.Add(header);

            foreach (var item in group.Items)
            {
                var row = BuildRow(item);
                ItemsHost.Children.Add(row);
                _rows[item.Id] = row;
            }
        }

        if (selectedId is not null && _rows.TryGetValue(selectedId, out var selectedRow))
        {
            selectedRow.Background = (WpfBrush)FindResource("Selected");
            selectedRow.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
            selectedRow.BorderThickness = new Thickness(1);
        }

        ShellLog.Info($"render items reason={reason} rows={_rows.Count} selected={selectedId ?? "none"}");
    }

    private void RefreshClipboardManagerTextTheme()
    {
        RefreshClipboardManagerVisualTheme(ItemsHost);
        RefreshInfoPanelTheme(refreshIcon: false);
        UpdateFilterVisuals();
        TitleText.Foreground = (WpfBrush)FindResource("Text");
        SubTitleText.Foreground = (WpfBrush)FindResource("Muted");
        RefreshClipboardManagerIconTheme();
    }

    private void RefreshClipboardManagerVisualTheme(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case TextBlock text:
                    text.Foreground = IsPrimaryClipboardText(text) ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
                    break;
                case Border { Tag: ClipboardHistoryItem rowItem } row when rowItem.Id == _selected?.Id:
                    row.Background = (WpfBrush)FindResource("Selected");
                    row.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
                    break;
            }

            RefreshClipboardManagerVisualTheme(child);
        }
    }

    private void RefreshClipboardManagerIconTheme()
    {
        RefreshClipboardManagerIcons(ItemsHost);
        if (_selected is not null)
        {
            HeaderIcon.Source = IconFor(_selected, 96);
        }
    }

    private void RefreshClipboardManagerIcons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is WpfImage image && FindRowItem(image) is { } imageItem)
            {
                image.Source = IconFor(imageItem, 96);
            }

            RefreshClipboardManagerIcons(child);
        }
    }

    private void RefreshInfoPanelTheme(bool refreshIcon = true)
    {
        RefreshInfoPanelTheme(InfoHost);
        if (refreshIcon && _selected is not null)
        {
            HeaderIcon.Source = IconFor(_selected, 96);
        }
    }

    private void RefreshInfoPanelTheme(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case TextBlock text:
                    text.Foreground = (WpfBrush)FindResource("Muted2");
                    break;
                case WpfTextBox box:
                    box.Foreground = (WpfBrush)FindResource("Text");
                    box.CaretBrush = (WpfBrush)FindResource("TextCursor");
                    break;
                case Border border when border.Height == 1:
                    border.Background = (WpfBrush)FindResource("Line");
                    break;
            }

            RefreshInfoPanelTheme(child);
        }
    }

    private static ClipboardHistoryItem? FindRowItem(DependencyObject child)
    {
        var current = child;
        while (VisualTreeHelper.GetParent(current) is { } parent)
        {
            if (parent is Border { Tag: ClipboardHistoryItem item })
            {
                return item;
            }

            current = parent;
        }

        return null;
    }

    private static bool IsPrimaryClipboardText(TextBlock text)
    {
        return text.FontWeight == FontWeights.SemiBold || text.FontSize >= 13;
    }

    private Border BuildRow(ClipboardHistoryItem item)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(6, 0, 6, 0),
            Background = item.Id == _selected?.Id ? (WpfBrush)FindResource("Selected") : WpfBrushes.Transparent,
            BorderBrush = item.Id == _selected?.Id ? (WpfBrush)FindResource("SelectedBorder") : WpfBrushes.Transparent,
            BorderThickness = item.Id == _selected?.Id ? new Thickness(1) : new Thickness(0),
            ClipToBounds = true,
            Tag = item,
        };

        var grid = new Grid { ClipToBounds = true };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new WpfImage
        {
            Source = IconFor(item, 96),
            Width = item.Kind == ClipboardItemKind.Text ? 32 : 28,
            Height = item.Kind == ClipboardItemKind.Text ? 32 : 28,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        var title = new TextBlock
        {
            Text = TitleFor(item),
            Foreground = (WpfBrush)FindResource("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var subtitle = new TextBlock
        {
            Text = SubtitleFor(item),
            Foreground = (WpfBrush)FindResource("Muted"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        textStack.Children.Add(title);
        textStack.Children.Add(subtitle);
        Grid.SetColumn(textStack, 2);
        grid.Children.Add(textStack);

        if (item.IsPinned)
        {
            var pin = new TextBlock
            {
                Text = "●",
                Foreground = (WpfBrush)FindResource("Muted"),
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(pin, 3);
            grid.Children.Add(pin);
        }

        var meta = new TextBlock
        {
            Text = MetaFor(item),
            Foreground = (WpfBrush)FindResource("Muted"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(meta, 4);
        grid.Children.Add(meta);

        row.Child = grid;
        row.MouseEnter += (_, _) =>
        {
            if (_selected?.Id == item.Id)
            {
                return;
            }

            row.Background = (WpfBrush)FindResource("AccentSoft");
        };
        row.MouseLeave += (_, _) =>
        {
            if (_selected?.Id == item.Id)
            {
                return;
            }

            row.Background = WpfBrushes.Transparent;
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                SelectItem(item, "double-click-paste");
                PasteSelected();
                e.Handled = true;
                return;
            }

            SelectItem(item, "click");
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            SelectItem(item, "right-click-up");
            ShowActionMenu(item);
            e.Handled = true;
        };
        return row;
    }

    private void OnTitleTextMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && _selected is not null)
        {
            RenameItem(_selected);
            e.Handled = true;
        }
    }

    private void ShowActionMenu(ClipboardHistoryItem item)
    {
        _menuItem = item;
        var actions = new List<MenuAction>
        {
            new("Paste", PasteSelected, true, shortcut: _settings.Hotkeys.PasteSelected),
            new("Copy", CopySelected, true, shortcut: _settings.Hotkeys.CopySelected),
            new("Rename", () => RenameItem(item)),
            new(item.IsPinned ? "Unpin" : "Pin", () => TogglePin(item), true, shortcut: _settings.Hotkeys.PinSelected),
            new("Move Pin Up", () => MovePin(item, -1), CanMovePin(item, -1)),
            new("Move Pin Down", () => MovePin(item, 1), CanMovePin(item, 1)),
            MenuAction.Separator,
        };

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            actions.Add(new MenuAction("Edit Text", () => EditText(item), true, shortcut: _settings.Hotkeys.EditSelected));
            actions.Add(new MenuAction("Append to Clipboard", () => AppendText(item)));
        }

        if (item.Kind == ClipboardItemKind.Link)
        {
            actions.Add(new MenuAction("Open", () => OpenItem(item), true, shortcut: _settings.Hotkeys.OpenSelected));
        }

        if (item.Kind is ClipboardItemKind.Image or ClipboardItemKind.Files)
        {
            actions.Add(MenuAction.Separator);
            actions.Add(new MenuAction("Open", () => OpenItem(item), true, shortcut: "Ctrl+O"));
            actions.Add(new MenuAction("Open With...", () => OpenWith(item)));
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            actions.Add(new MenuAction("Copy path", () => CopyPath(item)));
        }

        var shareActions = new List<MenuAction>();
        if (BlipShareLaunchPlan.IsInstalled())
        {
            shareActions.Add(new MenuAction("Blip", () => ShareWithBlip(item)));
        }

        shareActions.Add(new MenuAction("Windows Share...", () => ShareItem(item)));
        actions.Add(MenuAction.Separator);
        actions.Add(MenuAction.Submenu("Share", shareActions));
        actions.Add(new MenuAction("Save as File...", () => SaveItem(item)));
        actions.Add(new MenuAction("Delete", () => DeleteItem(item), true, danger: true, shortcut: "Del"));
        ShowStyledMenu(actions, null);
    }

    private void ShowStyledMenu(IEnumerable<MenuAction> actions, UIElement? target)
    {
        ActionMenuHost.Children.Clear();
        ShareSubmenuPopup.IsOpen = false;
        foreach (var action in actions)
        {
            if (action.IsSeparator)
            {
                ActionMenuHost.Children.Add(new Border { Height = 1, Background = (WpfBrush)FindResource("Line"), Margin = new Thickness(4, 4, 4, 4) });
                continue;
            }

            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = action.Enabled ? 1.0 : 0.45,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = action.Label,
                Foreground = action.Danger ? (WpfBrush)FindResource("Danger") : (WpfBrush)FindResource("Text"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(label);
            if (!string.IsNullOrWhiteSpace(action.Shortcut))
            {
                var shortcut = new TextBlock
                {
                    Text = action.Shortcut,
                    Foreground = (WpfBrush)FindResource("Muted"),
                    FontSize = 11,
                    Margin = new Thickness(20, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(shortcut, 1);
                grid.Children.Add(shortcut);
            }
            else if (action.Children.Count > 0)
            {
                var arrow = new TextBlock
                {
                    Text = ">",
                    Foreground = (WpfBrush)FindResource("Muted"),
                    FontSize = 12,
                    Margin = new Thickness(20, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(arrow, 1);
                grid.Children.Add(arrow);
            }

            row.Child = grid;
            if (action.Enabled)
            {
                row.MouseEnter += (_, _) =>
                {
                    row.Background = (WpfBrush)FindResource("AccentSoft");
                    row.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
                    if (action.Children.Count > 0)
                    {
                        ShowShareSubmenu(action.Children, row);
                    }
                    else
                    {
                        ShareSubmenuPopup.IsOpen = false;
                        ActionMenuPopup.StaysOpen = false;
                    }
                };
                row.MouseLeave += (_, _) =>
                {
                    row.Background = WpfBrushes.Transparent;
                    row.BorderBrush = WpfBrushes.Transparent;
                };
                row.MouseLeftButtonDown += (_, e) =>
                {
                    if (action.Children.Count > 0)
                    {
                        ShowShareSubmenu(action.Children, row);
                    }
                    else
                    {
                        CloseActionMenus();
                        action.Invoke();
                        ShellLog.Info($"menu action label={action.Label} item={_menuItem?.Id ?? "none"}");
                    }

                    e.Handled = true;
                };
            }

            ActionMenuHost.Children.Add(row);
        }

        _suppressDeactivate = true;
        ActionMenuBorder.MinWidth = target is null ? 220 : 178;
        ActionMenuPopup.Placement = target is null ? PlacementMode.MousePoint : PlacementMode.Bottom;
        ActionMenuPopup.PlacementTarget = target;
        ActionMenuPopup.HorizontalOffset = target is null ? 0 : -8;
        ActionMenuPopup.VerticalOffset = target is null ? 0 : 6;
        ActionMenuPopup.IsOpen = true;
        ActionMenuPopup.Closed -= OnActionMenuClosed;
        ActionMenuPopup.Closed += OnActionMenuClosed;
        ShellLog.Info($"menu opened target={(target is null ? "mouse" : target.GetType().Name)} count={ActionMenuHost.Children.Count}");
    }

    private void ShowShareSubmenu(IReadOnlyList<MenuAction> actions, UIElement owner)
    {
        ShareSubmenuHost.Children.Clear();
        foreach (var action in actions)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
                Opacity = action.Enabled ? 1.0 : 0.45,
                MinWidth = 170,
            };
            row.Child = new TextBlock
            {
                Text = action.Label,
                Foreground = action.Danger ? (WpfBrush)FindResource("Danger") : (WpfBrush)FindResource("Text"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (action.Enabled)
            {
                row.MouseEnter += (_, _) =>
                {
                    row.Background = (WpfBrush)FindResource("AccentSoft");
                    row.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
                };
                row.MouseLeave += (_, _) =>
                {
                    row.Background = WpfBrushes.Transparent;
                    row.BorderBrush = WpfBrushes.Transparent;
                };
                row.MouseLeftButtonDown += (_, e) =>
                {
                    CloseActionMenus();
                    action.Invoke();
                    ShellLog.Info($"submenu action label={action.Label} item={_menuItem?.Id ?? "none"}");
                    e.Handled = true;
                };
            }

            ShareSubmenuHost.Children.Add(row);
        }

        ShareSubmenuPopup.PlacementTarget = owner;
        ShareSubmenuPopup.HorizontalOffset = 4;
        ShareSubmenuPopup.VerticalOffset = -4;
        ActionMenuPopup.StaysOpen = true;
        ShareSubmenuPopup.StaysOpen = true;
        ShareSubmenuPopup.IsOpen = true;
    }

    private void CloseActionMenus()
    {
        ShareSubmenuPopup.IsOpen = false;
        ActionMenuPopup.IsOpen = false;
        ShareSubmenuPopup.StaysOpen = false;
        ActionMenuPopup.StaysOpen = false;
    }

    private void OnActionMenuClosed(object? sender, EventArgs e)
    {
        ShareSubmenuPopup.IsOpen = false;
        ShareSubmenuPopup.StaysOpen = false;
        ActionMenuPopup.StaysOpen = false;
        _suppressDeactivate = false;
        _menuItem = null;
        ShellLog.Info("menu closed");
    }

    private void SelectItem(ClipboardHistoryItem? item, string reason)
    {
        if (item is null || item.Id == _selected?.Id)
        {
            ShellLog.Info($"selection skipped reason={reason} id={item?.Id ?? "none"}");
            return;
        }

        if (_selected is not null && _rows.TryGetValue(_selected.Id, out var oldRow))
        {
            oldRow.Background = WpfBrushes.Transparent;
            oldRow.BorderBrush = WpfBrushes.Transparent;
            oldRow.BorderThickness = new Thickness(0);
        }

        _selected = item;
        if (_rows.TryGetValue(item.Id, out var newRow))
        {
            newRow.Background = (WpfBrush)FindResource("Selected");
            newRow.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
            newRow.BorderThickness = new Thickness(1);
        }

        HeaderIcon.Source = IconFor(item, 96);
        TitleText.Text = TitleFor(item);
        SubTitleText.Text = HeaderSubtitleFor(item);
        if (item.Kind == ClipboardItemKind.Text)
        {
            OpenButton.Content = "Edit";
            OpenButton.Visibility = Visibility.Visible;
        }
        else if (item.Kind is ClipboardItemKind.Link or ClipboardItemKind.Files or ClipboardItemKind.Image)
        {
            OpenButton.Content = "Open";
            OpenButton.Visibility = Visibility.Visible;
        }
        else
        {
            OpenButton.Visibility = Visibility.Collapsed;
        }
        RenderInfo(item);
        RenderPreview(item);
        ShellLog.Info($"selection changed reason={reason} id={item.Id} kind={item.Kind}");
    }

    private void RenderPreview(ClipboardHistoryItem item)
    {
        var token = ++_previewToken;
        HidePreviews();

        try
        {
            if (item.Kind == ClipboardItemKind.Color)
            {
                ColorPreviewSwatch.Fill = BrushFromHex(TextPayload(item));
                ColorPreviewText.Text = TextPayload(item);
                ColorPreview.Visibility = Visibility.Visible;
                ShellLog.Info($"preview color id={item.Id} hex={TextPayload(item)}");
                return;
            }

            if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
            {
                TextPreview.Text = TextPayload(item);
                TextPreview.Foreground = (WpfBrush)FindResource("Text");
                TextPreview.Visibility = Visibility.Visible;
                ShellLog.Info($"preview text id={item.Id} chars={TextPreview.Text.Length}");
                return;
            }

            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
            {
                ImagePreview.Source = LoadBitmap(item.AssetPath);
                _currentPreviewImagePath = item.AssetPath;
                ImagePreview.Visibility = Visibility.Visible;
                ExpandImageButton.Visibility = Visibility.Visible;
                ShellLog.Info($"preview image id={item.Id} path={item.AssetPath}");
                return;
            }

            if (item.Kind == ClipboardItemKind.Files)
            {
                var path = item.FilePaths.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(path))
                {
                    ShowPlaceholder(item, "No file selected");
                    return;
                }

                ShowPlaceholder(item, "Loading preview...");
                _ = LoadFilePreviewAsync(item, path, token);
                return;
            }

            ShowPlaceholder(item, item.Preview);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"preview failed id={item.Id}");
            ShowPlaceholder(item, "Preview unavailable");
        }
    }

    private async Task LoadFilePreviewAsync(ClipboardHistoryItem item, string path, int token)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (Directory.Exists(path))
            {
                await Dispatcher.InvokeAsync(() => ShowPlaceholder(item, path));
                return;
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (IsImageFile(ext))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token != _previewToken) return;
                    HidePreviews();
                    ImagePreview.Source = LoadBitmap(path);
                    _currentPreviewImagePath = path;
                    ImagePreview.Visibility = Visibility.Visible;
                    ExpandImageButton.Visibility = Visibility.Visible;
                });
                ShellLog.Info($"preview file image path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            if (IsHtmlFile(ext))
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (token != _previewToken) return;
                    HidePreviews();
                    HtmlPreview.Visibility = Visibility.Visible;
                    await HtmlPreview.EnsureCoreWebView2Async();
                    HtmlPreview.Source = new Uri(path);
                });
                ShellLog.Info($"preview html path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            if (IsTextFile(ext))
            {
                var text = await File.ReadAllTextAsync(path);
                if (text.Length > 80_000)
                {
                    text = text[..80_000] + Environment.NewLine + "...";
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token != _previewToken) return;
                    HidePreviews();
                    TextPreview.Text = text;
                    TextPreview.Visibility = Visibility.Visible;
                });
                ShellLog.Info($"preview text-file path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            DrawingImage? rendered = null;
            if (ext == ".pdf")
            {
                rendered = await Task.Run(() => WatcherPdfPreviewRenderer.TryRenderFirstPage(path, out var image) ? image : null);
            }
            else if (IsOfficeOrVisio(ext))
            {
                rendered = await Task.Run(() => WatcherStaticDocumentPreviewRenderer.TryRenderFirstPageOnStaThread(path));
            }

            if (rendered is not null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (token != _previewToken)
                    {
                        rendered.Dispose();
                        return;
                    }

                    HidePreviews();
                    ImagePreview.Source = BitmapFromDrawingImage(rendered);
                    if (ext == ".pdf")
                    {
                        _currentPreviewPdfPath = path;
                    }

                    ImagePreview.Visibility = Visibility.Visible;
                    ExpandImageButton.Visibility = Visibility.Visible;
                    rendered.Dispose();
                });
                ShellLog.Info($"preview rendered file path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            await Dispatcher.InvokeAsync(() => ShowPlaceholder(item, path));
            ShellLog.Info($"preview fallback file path={path} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"file preview failed path={path}");
            await Dispatcher.InvokeAsync(() => ShowPlaceholder(item, "Preview unavailable"));
        }
    }

    private void RenderInfo(ClipboardHistoryItem item)
    {
        InfoHost.Children.Clear();
        AddInfo("Source", SourceDisplayName(item), SourceIcon(item));
        AddInfo("Content type", ContentType(item));
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            AddInfo("Saved format", ClipboardPasteData.HasOriginalFormatting(item) ? "Plain text + formatting" : "Plain text");
        }

        AddInfo("Copied", item.LastCopiedAt.LocalDateTime.ToString("M/d/yyyy h:mm tt"));
        AddInfo("Times copied", item.CopyCount.ToString());

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            AddInfo("Characters", (item.CharacterCount ?? TextPayload(item).Length).ToString());
            AddInfo("Words", (item.WordCount ?? CountWords(TextPayload(item))).ToString());
        }

        if (item.Kind == ClipboardItemKind.Color)
        {
            AddInfo("Hex", TextPayload(item));
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            if (item.ImageWidth is not null && item.ImageHeight is not null)
            {
                AddInfo("Dimensions", $"{item.ImageWidth} x {item.ImageHeight}");
            }

            AddInfo("Image size", FormatBytes(item.AssetSizeBytes ?? SizeOf(item.AssetPath)));
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            var paths = item.FilePaths;
            AddInfo("Files", paths.Count.ToString());
            if (paths.Count == 1)
            {
                var path = paths[0];
                AddInfo("File name", Path.GetFileName(path), scrollable: true);
                AddInfo("File type", Directory.Exists(path) ? "Folder" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant());
                AddInfo("File size", Directory.Exists(path) ? "Folder" : FormatBytes(SizeOf(path)));
                AddInfo("File path", path, scrollable: true);
            }
        }

        ShellLog.Info($"info rendered id={item.Id} kind={item.Kind} rows={InfoHost.Children.Count}");
    }

    private void AddInfo(string label, string value, ImageSource? icon = null, bool scrollable = false)
    {
        var row = new Grid { MinHeight = 31 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new TextBlock
        {
            Text = label,
            Foreground = (WpfBrush)FindResource("Muted2"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        row.Children.Add(left);

        var valueHost = new DockPanel { LastChildFill = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        if (icon is not null)
        {
            var image = new WpfImage { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(image, Dock.Left);
            valueHost.Children.Add(image);
        }

        var text = new WpfTextBox
        {
            Text = value,
            Style = (Style)FindResource("CleanTextBox"),
            Foreground = (WpfBrush)FindResource("Text"),
            IsReadOnly = true,
            TextAlignment = TextAlignment.Right,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = scrollable ? TextWrapping.NoWrap : TextWrapping.Wrap,
            HorizontalScrollBarVisibility = scrollable ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
        };
        valueHost.Children.Add(text);
        Grid.SetColumn(valueHost, 1);
        row.Children.Add(valueHost);

        InfoHost.Children.Add(row);
        InfoHost.Children.Add(new Border { Height = 1, Background = (WpfBrush)FindResource("Line") });
    }

    private IEnumerable<ClipboardHistoryItem> FilteredItems()
    {
        IEnumerable<ClipboardHistoryItem> items = _allItems;
        items = _kindFilter switch
        {
            "text" => items.Where(i => i.Kind == ClipboardItemKind.Text),
            "images" => items.Where(i => i.Kind == ClipboardItemKind.Image || (i.Kind == ClipboardItemKind.Files && i.FilePaths.Any(p => IsImageFile(Path.GetExtension(p).ToLowerInvariant())))),
            "links" => items.Where(i => i.Kind == ClipboardItemKind.Link),
            "files" => items.Where(i => i.Kind == ClipboardItemKind.Files),
            "colors" => items.Where(i => i.Kind == ClipboardItemKind.Color),
            _ => items,
        };

        if (_dateFilter != "all")
        {
            items = items.Where(i => DateKey(i) == _dateFilter);
        }

        if (_kindFilter == "files" && _fileFilter != "all")
        {
            items = items.Where(i => i.FilePaths.Any(path => FileKindKey(path) == _fileFilter));
        }

        return items.ToList();
    }

    private static IEnumerable<(string Header, List<ClipboardHistoryItem> Items)> GroupItems(IEnumerable<ClipboardHistoryItem> items)
    {
        var list = items.ToList();
        yield return ("Pinned items", list.Where(i => i.IsPinned).OrderBy(i => i.PinOrder).ToList());
        yield return ("Today", list.Where(i => !i.IsPinned && DateKey(i) == "today").OrderByDescending(i => i.LastCopiedAt).ToList());
        yield return ("Yesterday", list.Where(i => !i.IsPinned && DateKey(i) == "yesterday").OrderByDescending(i => i.LastCopiedAt).ToList());
        yield return ("This week", list.Where(i => !i.IsPinned && DateKey(i) == "week").OrderByDescending(i => i.LastCopiedAt).ToList());
        yield return ("This month", list.Where(i => !i.IsPinned && DateKey(i) == "month").OrderByDescending(i => i.LastCopiedAt).ToList());
        yield return ("This year", list.Where(i => !i.IsPinned && DateKey(i) == "year").OrderByDescending(i => i.LastCopiedAt).ToList());
        yield return ("Older", list.Where(i => !i.IsPinned && DateKey(i) == "older").OrderByDescending(i => i.LastCopiedAt).ToList());
    }

    private void SetFilter(string kind)
    {
        _kindFilter = kind;
        if (kind != "files")
        {
            _fileFilter = "all";
        }

        RenderItems($"filter-{kind}");
        SelectItem(FilteredItems().FirstOrDefault(), $"filter-{kind}");
        ShellLog.Info($"filter changed kind={kind} date={_dateFilter} file={_fileFilter}");
    }

    private void TogglePin(ClipboardHistoryItem item)
    {
        var next = !item.IsPinned;
        if (_store.SetPinned(item.Id, next))
        {
            item.IsPinned = next;
            _allItems = _store.QueryItems(SearchBox.Text);
            RenderItems("pin-toggle");
            ShellLog.Info($"pin toggled id={item.Id} pinned={next}");
        }
    }

    private void MovePin(ClipboardHistoryItem item, int direction)
    {
        if (!_store.MovePinned(item.Id, direction))
        {
            ShellLog.Info($"pin move ignored id={item.Id} direction={direction}");
            return;
        }

        _allItems = _store.QueryItems(SearchBox.Text);
        RenderItems("pin-move");
        ShellLog.Info($"pin moved id={item.Id} direction={direction}");
    }

    private bool CanMovePin(ClipboardHistoryItem item, int direction)
    {
        var pins = _allItems.Where(i => i.IsPinned).OrderBy(i => i.PinOrder).ToList();
        var index = pins.FindIndex(i => i.Id == item.Id);
        var target = index + Math.Sign(direction);
        return index >= 0 && target >= 0 && target < pins.Count;
    }

    private void CopySelected()
    {
        if (_selected is null) return;
        SetClipboard(_selected, _settings.DefaultPasteFormat);
        ShellLog.Info($"copy selected id={_selected.Id}");
    }

    private void PasteSelected()
    {
        if (_selected is null) return;
        var selected = _selected;
        var payload = selected.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color
            ? ClipboardPasteData.Create(selected, _settings.DefaultPasteFormat)
            : null;
        SetClipboard(selected, _settings.DefaultPasteFormat);
        ConcealPalette("paste");
        RestoreReturnFocus();

        var actionKey = ClipAppOverride.ActionPaste;
        var overrideHotkey = ResolveOverrideHotkey(_returnFocusHwnd, actionKey);
        string pasteKeys;
        bool suspendHotkeys;
        if (!string.IsNullOrWhiteSpace(overrideHotkey))
        {
            pasteKeys = SendKeysFromGesture(overrideHotkey!);
            suspendHotkeys = true;
        }
        else if (selected.Kind == ClipboardItemKind.Image && AutoAltVForClaudeCli(_returnFocusHwnd))
        {
            pasteKeys = "%v";
            suspendHotkeys = true;
        }
        else
        {
            pasteKeys = "^v";
            suspendHotkeys = false;
        }

        if (TryPasteDirectlyIntoExplorerSearch(selected, payload?.Text))
        {
            ShellLog.Info($"paste selected id={selected.Id} keys=uia-explorer-search action={actionKey} override={overrideHotkey ?? "none"}");
            return;
        }

        SendPasteKeys(pasteKeys, suspendHotkeys);
        var verified = VerifyPasteOrRetry(selected, pasteKeys, suspendHotkeys, payload?.Text);
        if (verified)
        {
            CommitPasteIfNeeded(payload?.Text, suspendHotkeys);
        }

        ShellLog.Info($"paste selected id={selected.Id} keys={pasteKeys} action={actionKey} override={overrideHotkey ?? "none"} verified={verified}");
    }

    private void CaptureReturnFocus(IntPtr foreground)
    {
        _returnFocusHwnd = foreground;
        _returnFocusChildHwnd = FocusedChildWindow(foreground);
        _returnFocusElement = FocusedAutomationElement();
        _returnFocusElementSummary = AutomationSummary(_returnFocusElement);
        _returnFocusValueBefore = AutomationValue(_returnFocusElement);
        _returnFocusCommitsPasteWithEnter = ShouldCommitPasteWithEnter(_returnFocusHwnd, _returnFocusElement);
        ShellLog.Info($"return focus captured hwnd={_returnFocusHwnd} child={_returnFocusChildHwnd} element={_returnFocusElementSummary} value={SafeLogValue(_returnFocusValueBefore)}");
    }

    private void RestoreReturnFocus()
    {
        if (_returnFocusHwnd == IntPtr.Zero || !IsWindow(_returnFocusHwnd))
        {
            ShellLog.Info("return focus skipped hwnd=0");
            return;
        }

        var foregroundSet = SetForegroundWindow(_returnFocusHwnd);
        var focusSet = false;
        if (_returnFocusChildHwnd != IntPtr.Zero && IsWindow(_returnFocusChildHwnd))
        {
            var targetThread = GetWindowThreadProcessId(_returnFocusHwnd, out _);
            var currentThread = GetCurrentThreadId();
            var attached = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                focusSet = SetFocus(_returnFocusChildHwnd) != IntPtr.Zero;
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, targetThread, false);
                }
            }
        }

        var automationFocusSet = SetAutomationFocus(_returnFocusElement);
        ShellLog.Info($"return focus restored hwnd={_returnFocusHwnd} child={_returnFocusChildHwnd} foreground={foregroundSet} focus={focusSet} elementFocus={automationFocusSet} element={_returnFocusElementSummary}");
    }

    private bool TryPasteDirectlyIntoExplorerSearch(ClipboardHistoryItem item, string? text)
    {
        if (!IsFileExplorerSearchTarget(_returnFocusHwnd, _returnFocusElement))
        {
            return false;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            if (_returnFocusElement!.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                ShellLog.Info($"explorer search set through UIA chars={text.Length} element={_returnFocusElementSummary}");
                return true;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"explorer search UIA paste failed element={_returnFocusElementSummary}");
        }

        return false;
    }

    private void CommitPasteIfNeeded(string? expectedText, bool suspendHotkeys)
    {
        if (!_returnFocusCommitsPasteWithEnter || string.IsNullOrEmpty(expectedText))
        {
            return;
        }

        Thread.Sleep(80);
        RestoreReturnFocus();
        if (suspendHotkeys)
        {
            SuspendOwnHotkeysForSyntheticPaste(SendEnter);
        }
        else
        {
            SendEnter();
        }

        ShellLog.Info($"paste committed with Enter element={_returnFocusElementSummary}");
    }

    private bool VerifyPasteOrRetry(ClipboardHistoryItem item, string pasteKeys, bool suspendHotkeys, string? expectedText)
    {
        if (!CanVerifyPasteTarget(_returnFocusElement, expectedText))
        {
            ShellLog.Info($"paste verify skipped id={item.Id} element={_returnFocusElementSummary}");
            return true;
        }

        Thread.Sleep(180);
        var afterFirst = AutomationValue(_returnFocusElement);
        if (_returnFocusCommitsPasteWithEnter && PasteLooksApplied(_returnFocusValueBefore, afterFirst, expectedText))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=1-commit before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterFirst)}");
            return true;
        }

        if (PasteLooksApplied(_returnFocusValueBefore, afterFirst, expectedText) && PasteStillAppliedAfterSettle(afterFirst, expectedText))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=1 before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterFirst)}");
            return true;
        }

        ShellLog.Info($"paste verify retrying id={item.Id} before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterFirst)} element={_returnFocusElementSummary}");
        RestoreReturnFocus();
        SendPasteKeys(pasteKeys, suspendHotkeys);
        Thread.Sleep(240);

        var afterRetry = AutomationValue(_returnFocusElement);
        if (_returnFocusCommitsPasteWithEnter &&
            (PasteLooksApplied(afterFirst, afterRetry, expectedText) || PasteLooksApplied(_returnFocusValueBefore, afterRetry, expectedText)))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=2-commit after={SafeLogValue(afterRetry)}");
            return true;
        }

        if ((PasteLooksApplied(afterFirst, afterRetry, expectedText) || PasteLooksApplied(_returnFocusValueBefore, afterRetry, expectedText)) &&
            PasteStillAppliedAfterSettle(afterRetry, expectedText))
        {
            ShellLog.Info($"paste verify succeeded id={item.Id} attempt=2 after={SafeLogValue(afterRetry)}");
            return true;
        }

        if (TrySetAutomationValue(expectedText))
        {
            Thread.Sleep(240);
            var afterDirectSet = AutomationValue(_returnFocusElement);
            if (PasteLooksApplied(_returnFocusValueBefore, afterDirectSet, expectedText) &&
                PasteStillAppliedAfterSettle(afterDirectSet, expectedText))
            {
                ShellLog.Info($"paste verify succeeded id={item.Id} attempt=uia-set after={SafeLogValue(afterDirectSet)}");
                return true;
            }
        }

        NotifyPasteFailed();
        ShellLog.Info($"paste verify failed id={item.Id} expected={SafeLogValue(expectedText)} before={SafeLogValue(_returnFocusValueBefore)} after={SafeLogValue(afterRetry)} element={_returnFocusElementSummary}");
        return false;
    }

    private bool PasteStillAppliedAfterSettle(string? firstAppliedValue, string? expectedText)
    {
        Thread.Sleep(520);
        var afterSettle = AutomationValue(_returnFocusElement);
        var stable = PasteLooksApplied(firstAppliedValue, afterSettle, expectedText);
        if (!stable)
        {
            ShellLog.Info($"paste verify unstable first={SafeLogValue(firstAppliedValue)} afterSettle={SafeLogValue(afterSettle)} element={_returnFocusElementSummary}");
        }

        return stable;
    }

    private bool TrySetAutomationValue(string? text)
    {
        if (_returnFocusElement is null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            if (_returnFocusElement.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                ShellLog.Info($"paste fallback set through UIA chars={text.Length} element={_returnFocusElementSummary}");
                return true;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"paste fallback UIA set failed element={_returnFocusElementSummary}");
        }

        return false;
    }

    private void SendPasteKeys(string pasteKeys, bool suspendHotkeys)
    {
        if (pasteKeys == "^v")
        {
            if (suspendHotkeys)
            {
                SuspendOwnHotkeysForSyntheticPaste(SendCtrlV);
            }
            else
            {
                SendCtrlV();
            }

            return;
        }

        if (suspendHotkeys)
        {
            SuspendOwnHotkeysForSyntheticPaste(() => Forms.SendKeys.SendWait(pasteKeys));
        }
        else
        {
            Forms.SendKeys.SendWait(pasteKeys);
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(VirtualKeyControl, false),
            KeyboardInput(VirtualKeyV, false),
            KeyboardInput(VirtualKeyV, true),
            KeyboardInput(VirtualKeyControl, true),
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            Forms.SendKeys.SendWait("^v");
        }
    }

    private static void SendEnter()
    {
        var inputs = new[]
        {
            KeyboardInput(VirtualKeyEnter, false),
            KeyboardInput(VirtualKeyEnter, true),
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            Forms.SendKeys.SendWait("{ENTER}");
        }
    }

    private static Input KeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };
    }

    private void NotifyPasteFailed()
    {
        const string message = "Clip could not paste here. Press Ctrl+V manually.";
        ShowToast(message);
        UserNotificationRequested?.Invoke(message);
    }

    private string? ResolveOverrideHotkey(IntPtr hwnd, string actionKey)
    {
        if (hwnd == IntPtr.Zero) return null;
        var processName = TryGetProcessNameForWindow(hwnd);
        if (string.IsNullOrEmpty(processName)) return null;
        var match = _settings.AppOverrides.FirstOrDefault(o =>
            string.Equals(StripExeSuffix(o.AppName), processName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.Action, actionKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match?.Hotkey) ? null : match!.Hotkey;
    }

    private static string StripExeSuffix(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    private bool AutoAltVForClaudeCli(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var processName = TryGetProcessNameForWindow(hwnd);
        if (string.IsNullOrEmpty(processName)) return false;
        if (!_terminalHostProcesses.Any(t => string.Equals(t, processName, StringComparison.OrdinalIgnoreCase))) return false;
        return IsClaudeCliRunning();
    }

    private static string SendKeysFromGesture(string display)
    {
        var parts = display.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "^v";
        var prefix = new System.Text.StringBuilder();
        string keyToken = "v";
        for (var i = 0; i < parts.Length; i++)
        {
            var token = parts[i];
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase)) prefix.Append('^');
            else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase)) prefix.Append('%');
            else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase)) prefix.Append('+');
            else if (string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase)) { /* SendKeys can't emit Win cleanly; skip */ }
            else keyToken = MapKeyToken(token);
        }
        return prefix.Append(keyToken).ToString();
    }

    private static string MapKeyToken(string token)
    {
        if (token.Length == 1) return token.ToLowerInvariant();
        return token.ToUpperInvariant() switch
        {
            "ENTER" => "{ENTER}",
            "ESC" or "ESCAPE" => "{ESC}",
            "SPACE" => " ",
            "TAB" => "{TAB}",
            "BACKSPACE" => "{BACKSPACE}",
            "DELETE" or "DEL" => "{DEL}",
            "INSERT" or "INS" => "{INS}",
            "HOME" => "{HOME}",
            "END" => "{END}",
            "PAGEUP" or "PGUP" => "{PGUP}",
            "PAGEDOWN" or "PGDN" => "{PGDN}",
            "UP" => "{UP}",
            "DOWN" => "{DOWN}",
            "LEFT" => "{LEFT}",
            "RIGHT" => "{RIGHT}",
            var s when s.StartsWith("F") && int.TryParse(s.AsSpan(1), out _) => "{" + s + "}",
            _ => token.ToLowerInvariant(),
        };
    }

    private void SuspendOwnHotkeysForSyntheticPaste(Action send)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var hadOpen = _openHotkeyRegistered;
        var hadDebug = _debugLogHotkeyRegistered;
        var hadOverride = _openOverrideRegistered;
        if (hwnd != IntPtr.Zero)
        {
            if (hadOpen)
            {
                UnregisterHotKey(hwnd, OpenHotkeyId);
                _openHotkeyRegistered = false;
            }
            if (hadDebug)
            {
                UnregisterHotKey(hwnd, DebugLogHotkeyId);
                _debugLogHotkeyRegistered = false;
            }
            if (hadOverride)
            {
                UnregisterHotKey(hwnd, OpenOverrideHotkeyId);
                _openOverrideRegistered = false;
            }
        }

        try
        {
            send();
        }
        finally
        {
            if (hwnd != IntPtr.Zero && (hadOpen || hadDebug || hadOverride))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    EnsureHotkeyRegistered("post-paste");
                    ApplyForegroundOverride(GetForegroundWindow());
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private static readonly string[] _terminalHostProcesses =
    {
        "Code",
        "Code - Insiders",
        "WindowsTerminal",
        "OpenConsole",
        "conhost",
        "powershell",
        "pwsh",
        "cmd",
        "wezterm-gui",
        "alacritty",
        "mintty",
        "cursor",
    };

    private static string? TryGetProcessNameForWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsClaudeCliRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.ProcessName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static void SetClipboard(ClipboardHistoryItem item, PasteFormatPreference pasteFormat)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            var payload = ClipboardPasteData.Create(item, pasteFormat);
            var data = new System.Windows.DataObject();
            data.SetText(payload.Text, System.Windows.TextDataFormat.UnicodeText);
            if (payload.Html is not null)
            {
                data.SetText(payload.Html, System.Windows.TextDataFormat.Html);
            }

            if (payload.Rtf is not null)
            {
                data.SetText(payload.Rtf, System.Windows.TextDataFormat.Rtf);
            }

            System.Windows.Clipboard.SetDataObject(data, copy: true);
        }
        else if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            System.Windows.Clipboard.SetImage(LoadBitmap(item.AssetPath));
        }
        else if (item.Kind == ClipboardItemKind.Files)
        {
            var files = new StringCollection();
            files.AddRange(item.FilePaths.ToArray());
            System.Windows.Clipboard.SetFileDropList(files);
        }
    }

    private void EditText(ClipboardHistoryItem item)
    {
        var editor = new TextEditWindow(TextPayload(item), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Line"), (WpfBrush)FindResource("Surface"), (WpfBrush)FindResource("TextCursor"), (WpfBrush)FindResource("AccentSoft"), (WpfBrush)FindResource("Selected"), (WpfBrush)FindResource("SelectedBorder"))
        {
            Owner = this,
        };
        _suppressDeactivate = true;
        try
        {
            if (editor.ShowDialog() == true)
            {
                _store.EditText(item.Id, editor.Value);
                item.Text = editor.Value;
                item.Preview = ClipboardHistoryStore.PreviewText(editor.Value);
                LoadItems(selectFirst: false, reason: "edit-text");
                SelectItem(_store.GetItem(item.Id), "edit-text");
            }
        }
        finally
        {
            _suppressDeactivate = false;
            ShowPalette();
        }
    }

    private void RenameItem(ClipboardHistoryItem item)
    {
        var editor = new RenameWindow(TitleFor(item), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Muted"), (WpfBrush)FindResource("Line"), (WpfBrush)FindResource("Surface"), (WpfBrush)FindResource("AccentSoft"), (WpfBrush)FindResource("Selected"), (WpfBrush)FindResource("SelectedBorder"))
        {
            Owner = this,
        };
        _suppressDeactivate = true;
        try
        {
            if (editor.ShowDialog() == true)
            {
                _store.Rename(item.Id, editor.Value);
                var updated = _store.GetItem(item.Id);
                _selected = null;
                LoadItems(selectFirst: false, reason: "rename");
                SelectItem(updated, "rename");
                ShellLog.Info($"rename item id={item.Id}");
            }
        }
        finally
        {
            _suppressDeactivate = false;
            ShowPalette();
        }
    }

    private static void AppendText(ClipboardHistoryItem item)
    {
        var existing = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
        var payload = TextPayload(item);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            System.Windows.Clipboard.SetText(existing + payload);
        }
    }

    private void OpenItem(ClipboardHistoryItem item)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Link)
            {
                var target = ClipboardLinkDetector.TryNormalize(TextPayload(item), out var normalized) ? normalized : TextPayload(item);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else
            {
                var path = item.Kind == ClipboardItemKind.Image ? item.AssetPath : item.FilePaths.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }

            ShellLog.Info($"open item id={item.Id}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open item failed id={item.Id}");
        }
    }

    private void OpenWith(ClipboardHistoryItem item)
    {
        var targetPath = item.Kind == ClipboardItemKind.Image ? item.AssetPath : item.FilePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(targetPath) || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
        {
            ShellLog.Info($"open-with skipped missing target id={item.Id}");
            return;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            ShellLog.Info($"open-with opening path={targetPath}");
            var picker = new OpenWithWindow(
                targetPath,
                (WpfBrush)FindResource("Bg"),
                (WpfBrush)FindResource("Surface"),
                (WpfBrush)FindResource("Surface2"),
                (WpfBrush)FindResource("Surface3"),
                (WpfBrush)FindResource("Text"),
                (WpfBrush)FindResource("Muted"),
                (WpfBrush)FindResource("Line"),
                (WpfBrush)FindResource("Selected"),
                (WpfBrush)FindResource("AccentSoft"),
                (WpfBrush)FindResource("SelectedBorder"))
            {
                Owner = this,
            };

            _suppressDeactivate = true;
            picker.Closed += (_, _) =>
            {
                try
                {
                    if (picker.SelectedApp is not null)
                    {
                        ShellLog.Info($"open-with launching app={picker.SelectedApp.Name} source={picker.SelectedApp.Source} elapsedMs={watch.ElapsedMilliseconds}");
                        WatcherAppLauncher.OpenWith(targetPath, picker.SelectedApp);
                    }

                    ShellLog.Info($"open-with completed path={targetPath} elapsedMs={watch.ElapsedMilliseconds} selected={picker.SelectedApp?.Name ?? "none"}");
                }
                catch (Exception ex)
                {
                    ShellLog.Error(ex, $"open-with launch failed path={targetPath}");
                    ShowToast("Open With failed. Log saved.");
                }
                finally
                {
                    _suppressDeactivate = false;
                    ShowPalette();
                }
            };
            picker.Show();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with failed path={targetPath}");
            _suppressDeactivate = false;
            ShowToast("Open With failed. Log saved.");
        }
    }

    private void ShareItem(ClipboardHistoryItem item)
    {
        ClipboardSharePayload? payload = null;
        try
        {
            if (!WinDataTransferManager.IsSupported())
            {
                ShowToast("Sharing is not available on this PC.");
                return;
            }

            payload = ClipboardSharePayload.Create(item);
            var hwnd = new WindowInteropHelper(this).Handle;
            var interop = WinDataTransferManager.As<IDataTransferManagerInterop>();
            var result = interop.GetForWindow(hwnd, DataTransferManagerId);
            var manager = WinRT.MarshalInterface<WinDataTransferManager>.FromAbi(result);

            Windows.Foundation.TypedEventHandler<WinDataTransferManager, WinDataRequestedEventArgs>? handler = null;
            handler = async (_, args) =>
            {
                if (handler is not null)
                {
                    manager.DataRequested -= handler;
                }

                var deferral = args.Request.GetDeferral();
                try
                {
                    var data = args.Request.Data;
                    data.Properties.Title = ShareTitle(item);
                    data.Properties.Description = ShareDescription(item);
                    data.RequestedOperation = WinDataPackageOperation.Copy;
                    if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
                    {
                        data.SetText(TextPayload(item));
                    }

                    var files = new List<StorageFile>();
                    foreach (var path in payload.FilePaths)
                    {
                        files.Add(await StorageFile.GetFileFromPathAsync(path));
                    }

                    data.SetStorageItems(files);
                    data.ShareCompleted += (_, _) => payload.Cleanup();
                    data.ShareCanceled += (_, _) => payload.Cleanup();
                }
                catch (Exception ex)
                {
                    payload.Cleanup();
                    ShellLog.Error(ex, $"share data failed id={item.Id}");
                    args.Request.FailWithDisplayText("Clip could not prepare this item for sharing.");
                }
                finally
                {
                    deferral.Complete();
                }
            };

            manager.DataRequested += handler;
            interop.ShowShareUIForWindow(hwnd);
            ShellLog.Info($"share opened id={item.Id} files={payload.FilePaths.Count} temp={payload.HasTemporaryFiles}");
        }
        catch (Exception ex)
        {
            payload?.Cleanup();
            ShellLog.Error(ex, $"share failed id={item.Id}");
            ShowToast("Share failed. Log saved.");
        }
    }

    private void ShareWithBlip(ClipboardHistoryItem item)
    {
        try
        {
            var payload = ClipboardSharePayload.Create(item);
            var plan = BlipShareLaunchPlan.Create(payload);
            var startInfo = new ProcessStartInfo
            {
                FileName = BlipShareLaunchPlan.ExecutableName,
                UseShellExecute = true,
                Arguments = string.Join(" ", plan.LaunchArguments.Select(QuoteProcessArgument)),
            };

            Process.Start(startInfo);
            ShellLog.Info($"blip opened id={item.Id} files={plan.FilePaths.Count} temp={payload.HasTemporaryFiles}");
            if (payload.HasTemporaryFiles)
            {
                ShowToast($"Blip opened. Temp file: {Path.GetDirectoryName(plan.FilePaths[0])}");
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"blip failed id={item.Id}");
            ShowToast("Blip failed. Log saved.");
        }
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string ShareTitle(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Image => "Clip image",
            ClipboardItemKind.Files => item.FilePaths.Count == 1 ? Path.GetFileName(item.FilePaths[0]) : "Clip files",
            ClipboardItemKind.Link => "Clip link",
            ClipboardItemKind.Color => "Clip color",
            _ => "Clip text",
        };
    }

    private static string ShareDescription(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Image => "Image from Clip",
            ClipboardItemKind.Files => item.FilePaths.Count == 1 ? item.FilePaths[0] : $"{item.FilePaths.Count} files from Clip",
            _ => "Text saved as a temporary file by Clip",
        };
    }

    private static void CopyPath(ClipboardHistoryItem item)
    {
        if (item.FilePaths.Count > 0)
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, item.FilePaths));
        }
    }

    private void SaveItem(ClipboardHistoryItem item)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = item.Kind == ClipboardItemKind.Image ? "clipboard.png" : "clipboard.txt",
            Filter = item.Kind == ClipboardItemKind.Image ? "PNG Image|*.png|All files|*.*" : "Text File|*.txt|All files|*.*",
        };
        _suppressDeactivate = true;
        try
        {
            if (dialog.ShowDialog(this) == true)
            {
                _store.SaveAsFile(item.Id, dialog.FileName);
                ShellLog.Info($"save item id={item.Id} path={dialog.FileName}");
            }
        }
        finally
        {
            _suppressDeactivate = false;
            ShowPalette();
        }
    }

    private void DeleteItem(ClipboardHistoryItem item)
    {
        if (_store.Delete(item.Id))
        {
            if (_selected?.Id == item.Id)
            {
                _selected = null;
            }

            LoadItems(selectFirst: true, reason: "delete");
            ShellLog.Info($"delete item id={item.Id}");
        }
    }

    private void ShowPlaceholder(ClipboardHistoryItem item, string text)
    {
        HidePreviews();
        PlaceholderIcon.Source = IconFor(item, 240);
        PlaceholderText.Text = text;
        PlaceholderPreview.Visibility = Visibility.Visible;
    }

    private void HidePreviews()
    {
        CloseExpandedImage();
        TextPreview.Visibility = Visibility.Collapsed;
        ImagePreview.Visibility = Visibility.Collapsed;
        ExpandImageButton.Visibility = Visibility.Collapsed;
        HtmlPreview.Visibility = Visibility.Collapsed;
        PlaceholderPreview.Visibility = Visibility.Collapsed;
        ColorPreview.Visibility = Visibility.Collapsed;
        TextPreview.Text = string.Empty;
        ImagePreview.Source = null;
        _currentPreviewImagePath = null;
        _currentPreviewPdfPath = null;
    }

    private void OnExpandImageClick(object sender, RoutedEventArgs e)
    {
        var source = BestExpandedImageSource();
        if (source is null)
        {
            return;
        }

        CloseActionMenus();
        ExpandedImage.Source = source;
        SetExpandedImageNaturalSize(source);
        ExpandWindowForImage();
        ExpandedBackdrop.Source = CaptureShellBackdrop();
        ExpandedImageOverlay.Visibility = Visibility.Visible;
        ExpandedImageOverlay.UpdateLayout();
        ExpandedImageOverlay.Focus();
        ResetExpandedImageView();
        ShellLog.Info($"image expanded size={ExpandedImage.Width:0}x{ExpandedImage.Height:0}");
        e.Handled = true;
    }

    private ImageSource? BestExpandedImageSource()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_currentPreviewImagePath) && File.Exists(_currentPreviewImagePath))
            {
                return LoadBitmap(_currentPreviewImagePath);
            }

            if (!string.IsNullOrWhiteSpace(_currentPreviewPdfPath) && File.Exists(_currentPreviewPdfPath) &&
                WatcherPdfPreviewRenderer.TryRenderFirstPage(_currentPreviewPdfPath, out var pdfImage, 300))
            {
                using (pdfImage)
                {
                    return BitmapFromDrawingImage(pdfImage);
                }
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "best expanded image load failed");
        }

        return ImagePreview.Source;
    }

    private void SetExpandedImageNaturalSize(ImageSource source)
    {
        if (source is BitmapSource bitmap)
        {
            var dpiX = bitmap.DpiX > 0 ? bitmap.DpiX : 96;
            var dpiY = bitmap.DpiY > 0 ? bitmap.DpiY : 96;
            _expandedImageNaturalWidth = Math.Max(1, bitmap.PixelWidth * 96.0 / dpiX);
            _expandedImageNaturalHeight = Math.Max(1, bitmap.PixelHeight * 96.0 / dpiY);
            ExpandedImage.Width = _expandedImageNaturalWidth;
            ExpandedImage.Height = _expandedImageNaturalHeight;
            return;
        }

        _expandedImageNaturalWidth = Math.Max(1, double.IsNaN(source.Width) || source.Width <= 0 ? ActualWidth : source.Width);
        _expandedImageNaturalHeight = Math.Max(1, double.IsNaN(source.Height) || source.Height <= 0 ? ActualHeight : source.Height);
        ExpandedImage.Width = _expandedImageNaturalWidth;
        ExpandedImage.Height = _expandedImageNaturalHeight;
    }

    private void ExpandWindowForImage()
    {
        if (!_expandedWindowResized)
        {
            _expandedRestoreBounds = new Rect(Left, Top, Width, Height);
            _expandedRestoreCornerRadius = Shell.CornerRadius;
            _expandedWindowResized = true;
        }

        var screen = Forms.Screen.FromPoint(Forms.Control.MousePosition).Bounds;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;
        Shell.CornerRadius = new CornerRadius(0);
        UpdateLayout();
    }

    private void RestoreWindowAfterImage()
    {
        if (!_expandedWindowResized)
        {
            return;
        }

        Left = _expandedRestoreBounds.Left;
        Top = _expandedRestoreBounds.Top;
        Width = _expandedRestoreBounds.Width;
        Height = _expandedRestoreBounds.Height;
        Shell.CornerRadius = _expandedRestoreCornerRadius;
        _expandedWindowResized = false;
        UpdateLayout();
    }

    private ImageSource? CaptureShellBackdrop()
    {
        try
        {
            var width = Math.Max(1, (int)Math.Round(ActualWidth));
            var height = Math.Max(1, (int)Math.Round(ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Shell);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "expanded backdrop capture failed");
            return null;
        }
    }

    private static string? ClipboardTextOrNull(System.Windows.TextDataFormat format)
    {
        try
        {
            return System.Windows.Clipboard.ContainsText(format) ? System.Windows.Clipboard.GetText(format) : null;
        }
        catch
        {
            return null;
        }
    }

    private void ResetExpandedImageView()
    {
        ExpandedImageViewport.UpdateLayout();
        var viewportWidth = Math.Max(1, ExpandedImageViewport.ActualWidth);
        var viewportHeight = Math.Max(1, ExpandedImageViewport.ActualHeight);
        var fitWidth = Math.Max(1, viewportWidth - 48);
        var fitHeight = Math.Max(1, viewportHeight - 48);
        var imageWidth = Math.Max(1, _expandedImageNaturalWidth);
        var imageHeight = Math.Max(1, _expandedImageNaturalHeight);
        var fitScale = Math.Min(fitWidth / imageWidth, fitHeight / imageHeight);
        _expandedImageZoom = Math.Clamp(fitScale < 1 ? fitScale : 1.0, 0.05, 32.0);
        var scaledWidth = imageWidth * _expandedImageZoom;
        var scaledHeight = imageHeight * _expandedImageZoom;
        SetExpandedImageBounds(
            (viewportWidth - scaledWidth) / 2,
            (viewportHeight - scaledHeight) / 2,
            scaledWidth,
            scaledHeight);
    }

    private void CloseExpandedImage()
    {
        if (ExpandedImageOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        _expandedImagePanning = false;
        _expandedImageDownOnImage = false;
        ExpandedImageOverlay.ReleaseMouseCapture();
        ExpandedImageOverlay.Visibility = Visibility.Collapsed;
        ExpandedImage.Source = null;
        ExpandedBackdrop.Source = null;
        RestoreWindowAfterImage();
        ShellLog.Info("image expanded closed");
    }

    private void OnExpandedOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _expandedImageDownOnImage = IsPointOverExpandedImage(e.GetPosition(ExpandedImageViewport));
        if (!_expandedImageDownOnImage)
        {
            CloseExpandedImage();
            e.Handled = true;
            return;
        }

        _expandedImagePanning = true;
        _expandedImageLastPoint = e.GetPosition(ExpandedImageOverlay);
        _expandedImageDownPoint = _expandedImageLastPoint;
        _expandedImageMoved = false;
        ExpandedImageOverlay.CaptureMouse();
        ExpandedImageOverlay.Cursor = System.Windows.Input.Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnExpandedOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var releasedOffImage = !IsPointOverExpandedImage(e.GetPosition(ExpandedImageViewport));
        StopExpandedImagePan();
        if (!_expandedImageMoved && !_expandedImageDownOnImage && releasedOffImage)
        {
            CloseExpandedImage();
        }

        _expandedImageDownOnImage = false;
        e.Handled = true;
    }

    private void OnExpandedOverlayMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopExpandedImagePan();
        }
    }

    private void OnExpandedOverlayMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_expandedImagePanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(ExpandedImageOverlay);
        if (!_expandedImageMoved && Distance(point, _expandedImageDownPoint) > 2)
        {
            _expandedImageMoved = true;
        }

        PanExpandedImage(point.X - _expandedImageLastPoint.X, point.Y - _expandedImageLastPoint.Y);
        _expandedImageLastPoint = point;
        e.Handled = true;
    }

    private void OnExpandedOverlayMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomExpandedImage(Math.Pow(1.0018, e.Delta), e.GetPosition(ExpandedImageViewport));
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            PanExpandedImage(-e.Delta, 0);
        }
        else
        {
            PanExpandedImage(0, e.Delta);
        }

        e.Handled = true;
    }

    private void OnExpandedOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ExpandedImageOverlay.Visibility == Visibility.Visible && ExpandedImage.Source is not null)
        {
            ClampExpandedImage();
        }
    }

    private void StopExpandedImagePan()
    {
        _expandedImagePanning = false;
        ExpandedImageOverlay.ReleaseMouseCapture();
        ExpandedImageOverlay.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void PanExpandedImage(double deltaX, double deltaY)
    {
        Canvas.SetLeft(ExpandedImage, ExpandedImageLeft() + deltaX);
        Canvas.SetTop(ExpandedImage, ExpandedImageTop() + deltaY);
        ClampExpandedImage();
    }

    private void ZoomExpandedImage(double factor, System.Windows.Point center)
    {
        var oldZoom = _expandedImageZoom;
        var newZoom = Math.Clamp(oldZoom * factor, 0.02, 128.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            return;
        }

        var imageX = (center.X - ExpandedImageLeft()) / oldZoom;
        var imageY = (center.Y - ExpandedImageTop()) / oldZoom;
        _expandedImageZoom = newZoom;
        ExpandedImage.Width = _expandedImageNaturalWidth * newZoom;
        ExpandedImage.Height = _expandedImageNaturalHeight * newZoom;
        Canvas.SetLeft(ExpandedImage, center.X - imageX * newZoom);
        Canvas.SetTop(ExpandedImage, center.Y - imageY * newZoom);
        ClampExpandedImage();
    }

    private void ClampExpandedImage()
    {
        var viewportWidth = Math.Max(1, ExpandedImageViewport.ActualWidth);
        var viewportHeight = Math.Max(1, ExpandedImageViewport.ActualHeight);
        var scaledWidth = Math.Max(1, ExpandedImage.Width);
        var scaledHeight = Math.Max(1, ExpandedImage.Height);

        if (scaledWidth <= viewportWidth)
        {
            Canvas.SetLeft(ExpandedImage, (viewportWidth - scaledWidth) / 2);
        }
        else
        {
            Canvas.SetLeft(ExpandedImage, Math.Clamp(ExpandedImageLeft(), viewportWidth - scaledWidth, 0));
        }

        if (scaledHeight <= viewportHeight)
        {
            Canvas.SetTop(ExpandedImage, (viewportHeight - scaledHeight) / 2);
        }
        else
        {
            Canvas.SetTop(ExpandedImage, Math.Clamp(ExpandedImageTop(), viewportHeight - scaledHeight, 0));
        }
    }

    private bool IsPointOverExpandedImage(System.Windows.Point point)
    {
        var left = ExpandedImageLeft();
        var top = ExpandedImageTop();
        var right = left + Math.Max(1, ExpandedImage.Width);
        var bottom = top + Math.Max(1, ExpandedImage.Height);
        return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
    }

    private void SetExpandedImageBounds(double left, double top, double width, double height)
    {
        ExpandedImage.Width = Math.Max(1, width);
        ExpandedImage.Height = Math.Max(1, height);
        Canvas.SetLeft(ExpandedImage, left);
        Canvas.SetTop(ExpandedImage, top);
    }

    private double ExpandedImageLeft()
    {
        var left = Canvas.GetLeft(ExpandedImage);
        return double.IsNaN(left) ? 0 : left;
    }

    private double ExpandedImageTop()
    {
        var top = Canvas.GetTop(ExpandedImage);
        return double.IsNaN(top) ? 0 : top;
    }

    private System.Windows.Point MousePointInExpandedViewport()
    {
        return ExpandedImageViewport.PointFromScreen(new System.Windows.Point(Forms.Control.MousePosition.X, Forms.Control.MousePosition.Y));
    }

    private static short WheelDelta(IntPtr wParam)
    {
        return unchecked((short)(((long)wParam >> 16) & 0xffff));
    }

    private static double Distance(System.Windows.Point a, System.Windows.Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void PositionOnMouseScreen()
    {
        var screen = Forms.Screen.FromPoint(Forms.Control.MousePosition).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var screenTopLeft = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var screenBottomRight = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        var screenWidth = screenBottomRight.X - screenTopLeft.X;
        var screenHeight = screenBottomRight.Y - screenTopLeft.Y;
        var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;

        Left = screenTopLeft.X + Math.Max(0, (screenWidth - windowWidth) / 2);
        Top = screenTopLeft.Y + Math.Max(0, (screenHeight - windowHeight) / 2);
        ShellLog.Info($"position screenPx={screen.Left},{screen.Top},{screen.Width}x{screen.Height} screenDip={screenTopLeft.X:0},{screenTopLeft.Y:0},{screenWidth:0}x{screenHeight:0} mouse={Forms.Control.MousePosition.X},{Forms.Control.MousePosition.Y} size={windowWidth:0}x{windowHeight:0} left={Left:0} top={Top:0}");
    }

    private async Task WarmHtmlPreviewAsync()
    {
        try
        {
            await HtmlPreview.EnsureCoreWebView2Async();
            ShellLog.Info("html preview warmed");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "html warm failed");
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        LoadItems(selectFirst: false, reason: "search");
    }
    private void OnAllFilterClick(object sender, RoutedEventArgs e) => SetFilter("all");
    private void OnTextFilterClick(object sender, RoutedEventArgs e) => SetFilter("text");
    private void OnImageFilterClick(object sender, RoutedEventArgs e) => SetFilter("images");
    private void OnLinksFilterClick(object sender, RoutedEventArgs e) => SetFilter("links");
    private void OnColorFilterClick(object sender, RoutedEventArgs e) => SetFilter("colors");
    private void OnFilesFilterClick(object sender, RoutedEventArgs e) => SetFilter("files");
    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (_selected.Kind == ClipboardItemKind.Text) EditText(_selected);
        else OpenItem(_selected);
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => ConcealPalette("close");
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => ConcealPalette("minimize");
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsInternal(showPaletteOnClose: true);

    public void OpenSettingsFromTray() => OpenSettingsInternal(showPaletteOnClose: false);

    public void PasteLatestFromTray()
    {
        var item = _allItems.OrderByDescending(i => i.LastCopiedAt).FirstOrDefault();
        if (item is null)
        {
            ShellLog.Info("tray paste latest skipped — no items");
            return;
        }

        var foreground = GetForegroundWindow();
        var own = new WindowInteropHelper(this).Handle;
        if (foreground != IntPtr.Zero && foreground != own)
        {
            CaptureReturnFocus(foreground);
        }

        var previous = _selected;
        _selected = item;
        try
        {
            PasteSelected();
            ShellLog.Info($"tray paste latest id={item.Id}");
        }
        finally
        {
            _selected = previous;
        }
    }

    private void OpenSettingsInternal(bool showPaletteOnClose)
    {
        try
        {
            ShellLog.Info($"settings opening showPaletteOnClose={showPaletteOnClose}");
            var settings = new SettingsWindow(_settings, _lastUpdateStatus, ApplyTheme, RefreshClipboardManagerTextTheme, ApplyAppIcon, ApplyRunAtStartup, ApplyHistoryLimit, ApplyMaxItemSize, ApplyUpdateSettings, CheckForUpdatesFromSettings, InstallUpdateAsync, OpenDataFolder, OpenDebugLog, ClearHistory, ChangeClipboardFolder, ResetClipboardFolder, ApplyHotkeys, ApplyPrivacy, ApplyDefaultPasteFormat, ResetAllSettings, RenderSvg("dropdown-arrow-svgrepo-com.svg", 24), CurrentSettingsPalette)
            {
                Owner = this,
            };
            _suppressDeactivate = true;
            settings.Closed += (_, _) =>
            {
                _suppressDeactivate = false;
                ShellLog.Info("settings closed");
                if (showPaletteOnClose)
                {
                    ShowPalette();
                }
            };
            settings.Show();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings failed");
            _suppressDeactivate = false;
            ShowToast("Settings failed. Log saved.");
        }
    }

    private void ApplyAppIcon(AppIconPreference preference) => ApplyAppIcon(preference, save: true);

    private SettingsPalette CurrentSettingsPalette() => new(
        (WpfBrush)FindResource("Bg"),
        (WpfBrush)FindResource("Surface"),
        (WpfBrush)FindResource("Surface2"),
        (WpfBrush)FindResource("Surface3"),
        (WpfBrush)FindResource("Text"),
        (WpfBrush)FindResource("Muted"),
        (WpfBrush)FindResource("Line"),
        (WpfBrush)FindResource("Line2"),
        (WpfBrush)FindResource("Accent"),
        (WpfBrush)FindResource("AccentSoft"),
        (WpfBrush)FindResource("Selected"),
        (WpfBrush)FindResource("SelectedBorder"));

    private void ApplyAppIcon(AppIconPreference preference, bool save)
    {
        _settings.AppIcon = preference;
        ApplyWindowTitleIcon(preference);
        var iconPath = AppIconPath(preference);

        if (save)
        {
            _settings.Save();
            AppIconChanged?.Invoke(preference);
            UpdateInstalledShortcutIcons(iconPath);
            ShowToast($"Icon set to {preference}");
        }

        ShellLog.Info($"app icon applied preference={preference} path={iconPath}");
    }

    private void ApplyRunAtStartup(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
            var value = StartupRegistration.CurrentValue() ?? "none";
            ShellLog.Info($"startup preference changed enabled={enabled} value={value}");
            ShowToast(enabled ? "Startup enabled" : "Startup disabled");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"startup preference failed enabled={enabled}");
            ShowToast("Startup setting failed. Log saved.");
        }
    }

    private void ApplyHistoryLimit(int? limit)
    {
        _settings.HistoryLimit = limit;
        _settings.Save();
        var removed = _store.ApplyHistoryLimit(EffectiveHistoryLimit());
        _allItems = _store.QueryItems();
        if (_selected is not null && _allItems.All(item => item.Id != _selected.Id))
        {
            _selected = null;
        }

        RenderItems("history-limit");
        SelectItem(FilteredItems().FirstOrDefault(), "history-limit");
        ShellLog.Info($"history limit changed limit={HistoryLimitLabel(limit)} removed={removed}");
        ShowToast($"History limit set to {HistoryLimitLabel(limit)}");
    }

    private void ApplyMaxItemSize(long? maxBytes)
    {
        _settings.MaxItemSizeBytes = maxBytes;
        _settings.Save();
        ShellLog.Info($"max item size changed limit={ClipItemSizeLimit.MaxItemSizeLabel(maxBytes)}");
        ShowToast($"Max item size set to {ClipItemSizeLimit.MaxItemSizeLabel(maxBytes)}");
    }

    private void ApplyUpdateSettings(bool checkOnStartup, bool autoInstall)
    {
        _settings.CheckForUpdatesOnStartup = checkOnStartup;
        _settings.InstallUpdatesAutomatically = autoInstall;
        _settings.Save();
        ApplyUpdateCheckSchedule();
        ShellLog.Info($"update settings changed checkOnStartup={checkOnStartup} autoInstall={autoInstall}");
        ShowToast("Update settings saved");
    }

    private void ApplyUpdateCheckSchedule()
    {
        if (_settings.CheckForUpdatesOnStartup)
        {
            _updateCheckTimer.Start();
            ShellLog.Info($"update check schedule active interval={_updateCheckTimer.Interval}");
        }
        else
        {
            _updateCheckTimer.Stop();
            ShellLog.Info("update check schedule stopped");
        }
    }

    private void CheckForUpdatesFromSettings(Action<ClipUpdateStatus> updateStatus)
    {
        _ = CheckForUpdatesAsync(showToastWhenCurrent: true, updateStatus);
    }

    private async Task CheckForUpdatesAsync(bool showToastWhenCurrent, Action<ClipUpdateStatus>? updateStatus = null, bool promptIfAvailable = false)
    {
        if (_updateCheckInProgress)
        {
            ShellLog.Info("update check skipped already running");
            return;
        }

        _updateCheckInProgress = true;
        try
        {
            ShellLog.Info("update check started");
            var status = await _updates.CheckAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                _lastUpdateStatus = status;
                updateStatus?.Invoke(status);
                ShellLog.Info($"update check completed state={status.State} current={status.CurrentVersion} latest={status.LatestVersion ?? "none"} download={status.DownloadUrl ?? "none"}");

                if (status.State == "Update available")
                {
                    ShowToast(status.Message);
                    if (promptIfAvailable)
                    {
                        PromptForKnownUpdate();
                    }
                }
                else if (showToastWhenCurrent)
                {
                    ShowToast(status.Message);
                }
            });
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private void PromptForKnownUpdate()
    {
        if (!IsUpdateAvailable(_lastUpdateStatus))
        {
            return;
        }

        var version = _lastUpdateStatus.LatestVersion ?? "latest";
        if (string.Equals(_promptedUpdateVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Opacity == 0 || !IsHitTestVisible)
        {
            ShowPalette();
            return;
        }

        _promptedUpdateVersion = version;
        var result = System.Windows.MessageBox.Show(
            this,
            $"Clip {version} is available. Install it now?",
            "Update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            _ = InstallUpdateAsync(_lastUpdateStatus);
        }
    }

    private static bool IsUpdateAvailable(ClipUpdateStatus status) =>
        status.State == "Update available" && !string.IsNullOrWhiteSpace(status.DownloadUrl);

    private async Task InstallUpdateAsync(ClipUpdateStatus status)
    {
        try
        {
            var path = await _updates.DownloadUpdateAsync(status);
            if (path is null)
            {
                ShellLog.Info("update install skipped missing download asset");
                ShowToast("Update found, but no installer is attached");
                return;
            }

            ShellLog.Info($"update installer downloaded path={path}");
            var shouldExit = ClipUpdateService.LaunchInstaller(path, AppContext.BaseDirectory, Environment.ProcessId);
            ShowToast("Update installer opened");
            if (shouldExit)
            {
                System.Windows.Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "update install failed");
            ShowToast("Update install failed. Log saved.");
        }
    }

    private void ClearHistory(bool includePinned)
    {
        var removed = _store.ClearHistory(includePinned);
        if (_selected is not null && _store.GetItem(_selected.Id) is null)
        {
            _selected = null;
        }

        _allItems = _store.QueryItems();
        RenderItems(includePinned ? "clear-all-history" : "clear-unpinned-history");
        SelectItem(FilteredItems().FirstOrDefault(), includePinned ? "clear-all-history" : "clear-unpinned-history");
        ShellLog.Info($"history cleared includePinned={includePinned} removed={removed}");
        ShowToast(removed == 1 ? "1 item cleared" : $"{removed} items cleared");
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.ContentRootPath);
            Process.Start(new ProcessStartInfo(_store.ContentRootPath) { UseShellExecute = true });
            ShellLog.Info($"clipboard folder opened path={_store.ContentRootPath}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"clipboard folder open failed path={_store.ContentRootPath}");
            ShowToast("Clipboard folder failed. Log saved.");
        }
    }

    private void OpenDebugLog()
    {
        try
        {
            WriteDebugSnapshot("settings-about");
            Process.Start(new ProcessStartInfo(ShellLog.Path) { UseShellExecute = true });
            ShellLog.Info($"debug log opened path={ShellLog.Path}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "debug log open failed");
            ShowToast("Debug log failed. Log saved.");
        }
    }

    private void ChangeClipboardFolder(string folderPath)
    {
        _settings.ClipboardFolderPath = folderPath;
        _settings.Save();
        _store.SetContentRootPath(_settings.EffectiveClipboardFolderPath());
        ShellLog.Info($"clipboard folder changed path={_store.ContentRootPath}");
        ShowToast("Clipboard folder updated");
    }

    private void ResetClipboardFolder()
    {
        _settings.ClipboardFolderPath = null;
        _settings.Save();
        _store.SetContentRootPath(_settings.EffectiveClipboardFolderPath());
        ShellLog.Info($"clipboard folder reset path={_store.ContentRootPath}");
        ShowToast("Clipboard folder reset");
    }

    private int EffectiveHistoryLimit()
    {
        return _settings.HistoryLimit is null ? int.MaxValue : Math.Max(0, _settings.HistoryLimit.Value);
    }

    private static string HistoryLimitLabel(int? limit) => limit is null ? "Unlimited" : limit.Value.ToString();

    private void ApplyHotkeys(ClipHotkeySettings hotkeys)
    {
        hotkeys.Normalize();
        _settings.Hotkeys = hotkeys;
        _settings.Save();
        ReRegisterHotkeys("settings");
        ShellLog.Info($"hotkeys changed open={_settings.Hotkeys.OpenClip} debug={_settings.Hotkeys.SaveDebugLog}");
        ShowToast("Hotkeys updated");
    }

    private void ApplyPrivacy(ClipPrivacySettings privacy)
    {
        privacy.Normalize();
        _settings.Privacy = privacy;
        _settings.Save();
        ShellLog.Info($"privacy changed excludedApps={privacy.ExcludedApps.Count}");
        ShowToast("Privacy settings updated");
    }

    private void ApplyDefaultPasteFormat(PasteFormatPreference preference)
    {
        _settings.DefaultPasteFormat = preference;
        _settings.Save();
        ShellLog.Info($"default paste format changed format={preference}");
        ShowToast($"Paste format set to {PasteFormatLabel(preference)}");
    }

    private void ResetAllSettings()
    {
        _settings.ResetToDefaults();
        _settings.Save();
        ApplyRunAtStartup(StartupRegistration.DefaultEnabled);
        ApplyTheme(_settings.Theme, save: false);
        ApplyAppIcon(_settings.AppIcon, save: false);
        _store.SetContentRootPath(_settings.EffectiveClipboardFolderPath());
        ReRegisterHotkeys("settings-reset");
        var removed = _store.ApplyHistoryLimit(EffectiveHistoryLimit());
        _allItems = _store.QueryItems();
        RenderItems("settings-reset");
        SelectItem(FilteredItems().FirstOrDefault(), "settings-reset");
        ShellLog.Info($"settings reset all removed={removed}");
        ShowToast("Settings reset to defaults");
    }

    private static string PasteFormatLabel(PasteFormatPreference preference)
    {
        return preference switch
        {
            PasteFormatPreference.OriginalFormatting => "Original formatting",
            _ => "Plain text",
        };
    }

    private void ReRegisterHotkeys(string reason)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_openHotkeyRegistered)
        {
            UnregisterHotKey(hwnd, OpenHotkeyId);
            _openHotkeyRegistered = false;
        }

        if (_debugLogHotkeyRegistered)
        {
            UnregisterHotKey(hwnd, DebugLogHotkeyId);
            _debugLogHotkeyRegistered = false;
        }

        EnsureHotkeyRegistered(reason);
    }

    private void ApplyTheme(ClipThemePreference preference) => ApplyTheme(preference, save: true);

    private void ApplyTheme(ClipThemePreference preference, bool save)
    {
        _settings.Theme = preference;
        var useDark = preference switch
        {
            ClipThemePreference.Light => false,
            ClipThemePreference.Dark => true,
            _ => IsWindowsDarkMode(),
        };

        SetBrush("Bg", useDark ? "#1A1A1A" : "#F7F7F7");
        SetBrush("Surface", useDark ? "#212121" : "#FFFFFF");
        SetBrush("Surface2", useDark ? "#272727" : "#EDEDED");
        SetBrush("Surface3", useDark ? "#323232" : "#DCDCDC");
        SetBrush("Line", useDark ? "#494949" : "#B8B8B8");
        SetBrush("Line2", useDark ? "#5A5A5A" : "#989898");
        SetBrush("Text", useDark ? "#F1F1F1" : "#1A1A1A");
        SetBrush("Muted", useDark ? "#989898" : "#646464");
        SetBrush("Muted2", useDark ? "#BBBBBB" : "#474747");
        SetBrush("Muted3", useDark ? "#777777" : "#6A6A6A");
        SetBrush("Accent", useDark ? "#8A9CCC" : "#3B5BDB");
        SetBrush("TextCursor", useDark ? "#63D8FF" : "#005BFF");
        SetBrush("AccentSoft", useDark ? "#232A45" : "#E1E7FB");
        SetBrush("Selected", useDark ? "#324068" : "#C9D3F5");
        SetBrush("SelectedBorder", useDark ? "#6878A8" : "#5C7CFA");
        SetBrush("Danger", useDark ? "#D56B5D" : "#B94A3D");
        Background = (WpfBrush)FindResource("Bg");
        HtmlPreview.DefaultBackgroundColor = ToDrawingColor((SolidColorBrush)FindResource("Bg"));
        TextPreview.Foreground = (WpfBrush)FindResource("Text");
        TextPreview.CaretBrush = (WpfBrush)FindResource("TextCursor");
        if (TitleText is not null) { TitleText.Foreground = (WpfBrush)FindResource("Text"); }
        if (SubTitleText is not null) { SubTitleText.Foreground = (WpfBrush)FindResource("Muted"); }
        if (save)
        {
            Dispatcher.BeginInvoke(RefreshChromeIcons, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        else
        {
            RefreshChromeIcons();
        }

        ShellLog.Info($"theme applied preference={preference} dark={useDark}");

        if (save)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _settings.Save();
                if (_selected is not null)
                {
                    RenderInfo(_selected);
                    RenderPreview(_selected);
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }

    private void ApplyWindowTitleIcon(AppIconPreference preference)
    {
        var iconPath = AppIconPath(preference);
        if (File.Exists(iconPath))
        {
            var icon = LoadBitmap(iconPath);
            Icon = icon;
            AppHeaderIcon.Source = icon;
            ShellLog.Info($"window title icon applied icon={preference} path={iconPath}");
        }
    }

    private void RefreshChromeIcons()
    {
        SettingsIcon.Source = RenderSvg("settings-svgrepo-com.svg", 24, color: BrushHex("Muted2"));
        DateDropIcon.Source = RenderSvg("dropdown-arrow-svgrepo-com.svg", 24, color: BrushHex(_kindFilter == "all" ? "Text" : "Muted2"));
        FileDropIcon.Source = RenderSvg("dropdown-arrow-svgrepo-com.svg", 24, color: BrushHex(_kindFilter == "files" ? "Text" : "Muted2"));
        ExpandImageIcon.Source = RenderSvg("expand-alt-svgrepo-com.svg", 24, color: BrushHex("Muted2"));
    }

    private void SetBrush(string key, string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        if (Resources[key] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                Resources[key] = new SolidColorBrush(color);
                return;
            }

            brush.Color = color;
        }
    }

    private string BrushHex(string key)
    {
        if (Resources[key] is SolidColorBrush brush)
        {
            return $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
        }

        return "#F2EFE9";
    }

    internal static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }

    internal static bool IsLightBackground(WpfBrush brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return false;
        }

        var color = solid.Color;
        var brightness = (color.R * 0.299) + (color.G * 0.587) + (color.B * 0.114);
        return brightness > 150;
    }

    private static System.Drawing.Color ToDrawingColor(SolidColorBrush brush) =>
        System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);

    private static string ThemeLabel(ClipThemePreference preference) => preference switch
    {
        ClipThemePreference.Light => "Light",
        ClipThemePreference.Dark => "Dark",
        _ => "System",
    };

    internal static ClipThemePreference NextThemeTogglePreference(ClipThemePreference current, bool systemIsDark)
    {
        var currentlyDark = current switch
        {
            ClipThemePreference.Dark => true,
            ClipThemePreference.Light => false,
            _ => systemIsDark,
        };

        return currentlyDark ? ClipThemePreference.Light : ClipThemePreference.Dark;
    }

    private static void UpdateInstalledShortcutIcons(string iconPath)
    {
        try
        {
            var shortcuts = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Clip.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Clip.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Clip", "Clip.lnk"),
            };

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (var shortcutPath in shortcuts.Where(File.Exists))
            {
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.IconLocation = iconPath;
                shortcut.Save();
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "shortcut icon update failed");
        }
    }

    private void OnActionsClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not null)
        {
            ShowActionMenu(_selected);
        }
    }

    private void OnListWheel(object sender, MouseWheelEventArgs e)
    {
        ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset - e.Delta);
        e.Handled = true;
        ShellLog.Info($"list wheel delta={e.Delta} offset={ListScroll.VerticalOffset}/{ListScroll.ScrollableHeight}");
    }

    private void OnDateDropClick(object sender, RoutedEventArgs e)
    {
        var actions = new[] { ("All", "all"), ("Today", "today"), ("Yesterday", "yesterday"), ("This week", "week"), ("This month", "month"), ("This year", "year"), ("Older", "older") }
            .Select(pair => new MenuAction(pair.Item1, () =>
            {
                _dateFilter = pair.Item2;
                RenderItems("date-filter");
                SelectItem(FilteredItems().FirstOrDefault(), "date-filter");
            }));
        ShowStyledMenu(actions, AllFilterShell);
    }

    private void OnFileDropClick(object sender, RoutedEventArgs e)
    {
        var keys = _allItems.SelectMany(i => i.FilePaths).Select(FileKindKey).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new[] { "all", "folder", "pdf", "excel", "visio", "html", "image", "text", "word", "powerpoint" }
            .Concat(keys.OrderBy(k => k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => k == "all" || keys.Contains(k));
        var actions = ordered.Select(key => new MenuAction(LabelForFileKey(key), () =>
        {
            _kindFilter = "files";
            _fileFilter = key;
            RenderItems("file-filter");
            SelectItem(FilteredItems().FirstOrDefault(), "file-filter");
        }));
        ShowStyledMenu(actions, FilesFilterShell);
    }

    private void OnChromeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (MatchesHotkey(e, _settings.Hotkeys.SaveDebugLog))
        {
            WriteDebugSnapshot("keyboard");
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.PasteSelected))
        {
            PasteSelected();
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.CopySelected))
        {
            CopySelected();
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.PinSelected))
        {
            if (_selected is not null)
            {
                TogglePin(_selected);
                ShellLog.Info($"hotkey pin id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.OpenActions))
        {
            if (_selected is not null)
            {
                ShowActionMenu(_selected);
                ShellLog.Info($"hotkey actions id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowHotkeyHelp();
            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.OpenSelected))
        {
            if (_selected is not null && _selected.Kind is (ClipboardItemKind.Link or ClipboardItemKind.Files or ClipboardItemKind.Image))
            {
                OpenItem(_selected);
                ShellLog.Info($"hotkey open id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.EditSelected))
        {
            if (_selected is not null && _selected.Kind is (ClipboardItemKind.Text or ClipboardItemKind.Link))
            {
                EditText(_selected);
                ShellLog.Info($"hotkey edit id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.DeleteSelected))
        {
            if (_selected is not null)
            {
                DeleteItem(_selected);
            }

            e.Handled = true;
        }
        else if (MatchesHotkey(e, _settings.Hotkeys.CloseClip))
        {
            if (ExpandedImageOverlay.Visibility == Visibility.Visible)
            {
                CloseExpandedImage();
                e.Handled = true;
                return;
            }

            if (ActionMenuPopup.IsOpen)
            {
                ActionMenuPopup.IsOpen = false;
                e.Handled = true;
                return;
            }

            ConcealPalette("escape");
            e.Handled = true;
        }
    }

    private static bool MatchesHotkey(System.Windows.Input.KeyEventArgs e, string configured)
    {
        return ClipHotkeyGesture.TryParse(configured, out var gesture)
            && e.Key == gesture.WpfKey
            && Keyboard.Modifiers == gesture.WpfModifiers;
    }

    private void ShowHotkeyHelp()
    {
        try
        {
            ShellLog.Info("hotkey help opening");
            var help = new HotkeyHelpWindow(
                _settings.Hotkeys,
                (WpfBrush)FindResource("Bg"),
                (WpfBrush)FindResource("Surface"),
                (WpfBrush)FindResource("Surface2"),
                (WpfBrush)FindResource("Text"),
                (WpfBrush)FindResource("Muted"),
                (WpfBrush)FindResource("Line"),
                (WpfBrush)FindResource("AccentSoft"),
                (WpfBrush)FindResource("SelectedBorder"))
            {
                Owner = this,
            };
            _suppressDeactivate = true;
            help.Closed += (_, _) =>
            {
                _suppressDeactivate = false;
                ShellLog.Info("hotkey help closed");
                ShowPalette();
            };
            help.Show();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "hotkey help failed");
            _suppressDeactivate = false;
            ShowToast("Hotkey help failed. Log saved.");
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (ExpandedImageOverlay.Visibility == Visibility.Visible)
        {
            CloseExpandedImage();
            Activate();
            ShellLog.Info("image expanded closed on deactivate");
            return;
        }

        if (KeepOpenForDebug || _suppressDeactivate || ActionMenuPopup.IsOpen || IsContextMenuOpen(this))
        {
            ShellLog.Info($"deactivate suppressed debug={KeepOpenForDebug}");
            return;
        }

        ConcealPalette("deactivate");
        ShellLog.Info("palette hidden on deactivate");
    }

    private static bool IsContextMenuOpen(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ContextMenu { IsOpen: true })
            {
                return true;
            }

            if (IsContextMenuOpen(child))
            {
                return true;
            }
        }

        return false;
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void UpdateFilterVisuals()
    {
        SetFilterVisual(AllButton, AllFilterShell, _kindFilter == "all");
        SetFilterVisual(TextButton, null, _kindFilter == "text");
        SetFilterVisual(ImageButton, null, _kindFilter == "images");
        SetFilterVisual(LinksButton, null, _kindFilter == "links");
        SetFilterVisual(ColorButton, null, _kindFilter == "colors");
        SetFilterVisual(FilesButton, FilesFilterShell, _kindFilter == "files");
        DateDropButton.Foreground = _kindFilter == "all" ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        DateDropButton.Background = _kindFilter == "all" ? (WpfBrush)FindResource("AccentSoft") : WpfBrushes.Transparent;
        FileDropButton.Foreground = _kindFilter == "files" ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        FileDropButton.Background = _kindFilter == "files" ? (WpfBrush)FindResource("AccentSoft") : WpfBrushes.Transparent;
        RefreshChromeIcons();
    }

    private void SetFilterVisual(WpfButton button, Border? shell, bool selected)
    {
        var selectedBorder = (WpfBrush)FindResource("SelectedBorder");
        button.Foreground = selected ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        button.Background = selected ? (WpfBrush)FindResource("AccentSoft") : WpfBrushes.Transparent;
        if (shell is not null)
        {
            shell.Background = selected ? (WpfBrush)FindResource("AccentSoft") : WpfBrushes.Transparent;
            shell.BorderBrush = selected ? selectedBorder : WpfBrushes.Transparent;
            button.BorderBrush = WpfBrushes.Transparent;
        }
        else
        {
            button.BorderBrush = selected ? selectedBorder : WpfBrushes.Transparent;
        }
    }

    private static string TitleFor(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CustomTitle))
        {
            return item.CustomTitle;
        }

        if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
        {
            return Path.GetFileName(item.FilePaths[0]);
        }

        return item.Preview;
    }

    private static string SubtitleFor(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Files)
        {
            if (item.FilePaths.Count == 1)
            {
                var path = item.FilePaths[0];
                if (Directory.Exists(path))
                {
                    return "Folder";
                }

                var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                return string.IsNullOrWhiteSpace(ext) ? "File" : $"{ext} file";
            }

            return $"{item.FilePaths.Count} files";
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            return item.ImageWidth is not null && item.ImageHeight is not null ? "Screenshot" : "Image";
        }

        return DisplaySourceName(item.SourceApplication) is { Length: > 0 } source && source != "Unknown"
            ? source
            : item.Kind.ToString();
    }

    private static string HeaderSubtitleFor(ClipboardHistoryItem item)
    {
        var source = SourceDisplayName(item);
        return $"Copied from {source} · {MetaFor(item)}";
    }

    private static string SourceDisplayName(ClipboardHistoryItem item)
    {
        return DisplaySourceName(item.SourceApplication);
    }

    private static string DisplaySourceName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Unknown";
        }

        return source.ToLowerInvariant() switch
        {
            "olk" or "outlook" => "Outlook",
            "code" => "VS Code",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "explorer" => "File Explorer",
            "windowsterminal" => "Windows Terminal",
            "wt" => "Windows Terminal",
            "powershell" => "PowerShell",
            "pwsh" => "PowerShell",
            "cmd" => "Command Prompt",
            "winword" => "Word",
            "excel" => "Excel",
            "powerpnt" => "PowerPoint",
            "onenote" => "OneNote",
            "teams" => "Teams",
            "slack" => "Slack",
            "discord" => "Discord",
            "spotify" => "Spotify",
            "notion" => "Notion",
            "obsidian" => "Obsidian",
            _ => source,
        };
    }

    private static string MetaFor(ClipboardHistoryItem item)
    {
        var copied = item.LastCopiedAt.LocalDateTime;
        var today = DateTime.Today;
        if (copied.Date == today)
        {
            return copied.ToString("h:mm tt");
        }

        if (copied.Date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        if (copied >= today.AddDays(-7))
        {
            return copied.ToString("ddd");
        }

        return copied.ToString("M/d/yy");
    }

    private static string TextPayload(ClipboardHistoryItem item) => item.Text ?? item.Preview ?? string.Empty;
    private static bool TryNormalizeColorText(string? text, string? source, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var match = Regex.Match(trimmed, @"^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
        if (!match.Success)
        {
            return false;
        }

        var sourceLooksLikeColorPicker = source?.Contains("ColorPicker", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Contains("PowerToys", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Equals("Clip", StringComparison.OrdinalIgnoreCase) == true ||
            source?.Equals("Clip.Shell", StringComparison.OrdinalIgnoreCase) == true;

        if (!trimmed.StartsWith('#') && !sourceLooksLikeColorPicker)
        {
            return false;
        }

        var value = match.Groups[1].Value;
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(ch => $"{ch}{ch}"));
        }

        hex = "#" + value.ToUpperInvariant();
        return true;
    }

    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static int CountWords(string text) => Regex.Matches(text, @"\b[\w']+\b").Count;
    private static long? SizeOf(string? path) => File.Exists(path) ? new FileInfo(path).Length : null;
    private static string FormatBytes(long? bytes) => bytes is null ? "" : bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / 1024d / 1024d:0.#} MB";
    private static string ContentType(ClipboardHistoryItem item) => item.Kind == ClipboardItemKind.Link ? "Link" : item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1 && Directory.Exists(item.FilePaths[0]) ? "Folder" : item.Kind.ToString();

    private static string DateKey(ClipboardHistoryItem item)
    {
        var copied = item.LastCopiedAt.LocalDateTime.Date;
        var today = DateTime.Today;
        if (copied == today) return "today";
        if (copied == today.AddDays(-1)) return "yesterday";
        if (copied >= today.AddDays(-7)) return "week";
        if (copied.Year == today.Year && copied.Month == today.Month) return "month";
        return copied.Year == today.Year ? "year" : "older";
    }

    private static string FileKindKey(string path)
    {
        if (Directory.Exists(path)) return "folder";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".xls" or ".xlsx" or ".xlsm" => "excel",
            ".vsd" or ".vsdx" => "visio",
            ".html" or ".htm" => "html",
            ".doc" or ".docx" => "word",
            ".ppt" or ".pptx" => "powerpoint",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "image",
            ".txt" or ".log" or ".md" or ".json" or ".xml" or ".css" or ".js" or ".ts" or ".cs" or ".bat" or ".ps1" => "text",
            _ => ext.TrimStart('.'),
        };
    }

    private static string LabelForFileKey(string key) => key switch
    {
        "all" => "All",
        "folder" => "Folders",
        "pdf" => "PDF",
        "excel" => "Excel",
        "visio" => "Visio",
        "html" => "HTML",
        "word" => "Word",
        "powerpoint" => "PowerPoint",
        _ => key.ToUpperInvariant(),
    };

    private static bool IsImageFile(string ext) => ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp";
    private static bool IsHtmlFile(string ext) => ext is ".html" or ".htm";
    private static bool IsOfficeOrVisio(string ext) => ext is ".doc" or ".docx" or ".xls" or ".xlsx" or ".xlsm" or ".ppt" or ".pptx" or ".vsd" or ".vsdx";
    private static bool IsTextFile(string ext) => ext is ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".css" or ".js" or ".ts" or ".cs" or ".bat" or ".cmd" or ".ps1" or ".py" or ".html" or ".htm";

    private ImageSource IconFor(ClipboardHistoryItem item, int size)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Color)
            {
                return RenderColorSwatch(TextPayload(item), size);
            }

            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
            {
                return LoadBitmap(item.AssetPath);
            }

            if (item.Kind == ClipboardItemKind.Link) return RenderSvg("hyperlink-icon.svg", size, 0.78);
            if (item.Kind == ClipboardItemKind.Text) return RenderSvg("text_underline_icon_high_fidelity.svg", size);
            if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
            {
                var path = item.FilePaths[0];
                if (Directory.Exists(path)) return RenderSvg("folder-svgrepo-com.svg", size);
                if (File.Exists(path) && IsImageFile(Path.GetExtension(path).ToLowerInvariant()))
                {
                    return LoadBitmap(path);
                }

                return RenderFileSvg(path, size);
            }

            return RenderSvg("file-60.svg", size);
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"icon failed id={item.Id}");
            return BitmapFromDrawingImage(System.Drawing.SystemIcons.Application.ToBitmap());
        }
    }

    private ImageSource? SourceIcon(ClipboardHistoryItem item)
    {
        try
        {
            if (item.SourceApplicationPath is not null && File.Exists(item.SourceApplicationPath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(item.SourceApplicationPath);
                if (icon is not null)
                {
                    using var bitmap = icon.ToBitmap();
                    return BitmapFromDrawingImage(bitmap);
                }
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "source icon failed");
        }

        return null;
    }

    private ImageSource RenderFileSvg(string path, int size)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ShouldUseWindowsFileIcon(ext))
        {
            var windowsIcon = WatcherShellIconReader.TryGetIcon(path, large: size >= 48);
            if (windowsIcon is not null)
            {
                using (windowsIcon)
                {
                    return BitmapFromDrawingImage(windowsIcon);
                }
            }
        }

        var name = string.IsNullOrWhiteSpace(ext) ? "file-60.svg" : $"file-icon-{ext}.svg";
        return File.Exists(AssetIconPath(name)) ? RenderSvg(name, size) : RenderGeneratedFileIcon(ext, size);
    }

    private static bool ShouldUseWindowsFileIcon(string ext)
    {
        return ext is "doc" or "docx" or "xls" or "xlsx" or "xlsm" or "ppt" or "pptx" or "vsd" or "vsdx" or "pdf";
    }

    private ImageSource RenderSvg(string fileName, int size, double scaleX = 1.0, string? color = null)
    {
        var actualColor = color ?? BrushHex("Muted2");
        var cacheKey = $"{fileName}|{size}|{scaleX:0.###}|{actualColor}";
        lock (SvgCacheGate)
        {
            if (SvgImageCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var renderWidth = Math.Max(1, (int)Math.Round(size * scaleX));
        using var bitmap = new System.Drawing.Bitmap(Math.Max(size, renderWidth), size);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        var svg = ThemeSvg(ReadSvgText(fileName), actualColor);
        var document = SvgDocument.FromSvg<SvgDocument>(svg);
        document.Width = renderWidth;
        document.Height = size;
        using var rendered = document.Draw(renderWidth, size);
        graphics.DrawImage(rendered, (bitmap.Width - renderWidth) / 2, 0, renderWidth, size);
        var source = BitmapFromDrawingImage(bitmap);
        lock (SvgCacheGate)
        {
            SvgImageCache[cacheKey] = source;
        }

        return source;
    }

    private ImageSource RenderGeneratedFileIcon(string ext, int size)
    {
        var label = Regex.Replace((ext ?? "file").ToUpperInvariant(), @"[^A-Z0-9+#-]", "");
        if (label.Length == 0) label = "FILE";
        if (label.Length > 5) label = label[..5];
        var fontSize = label.Length <= 3 ? 92 : label.Length == 4 ? 72 : 58;
        var color = "#F4EEE7";
        var svg = $"""
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <path fill="{color}" d="M378.413,0H208.297h-13.182L185.8,9.314L57.02,138.102l-9.314,9.314v13.176v265.514c0,47.36,38.528,85.895,85.896,85.895h244.811c47.353,0,85.881-38.535,85.881-85.895V85.896C464.294,38.528,425.766,0,378.413,0z M432.497,426.105c0,29.877-24.214,54.091-54.084,54.091H133.602c-29.884,0-54.098-24.214-54.098-54.091V160.591h83.716c24.885,0,45.077-20.178,45.077-45.07V31.804h170.116c29.87,0,54.084,24.214,54.084,54.092V426.105z"/>
  <text x="256" y="330" text-anchor="middle" dominant-baseline="middle" font-family="Segoe UI,Arial,sans-serif" font-weight="900" font-size="{fontSize}" fill="{color}">{System.Security.SecurityElement.Escape(label)}</text>
</svg>
""";
        using var bitmap = new System.Drawing.Bitmap(size, size);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        var document = SvgDocument.FromSvg<SvgDocument>(svg);
        using var rendered = document.Draw(size, size);
        graphics.DrawImage(rendered, 0, 0, size, size);
        return BitmapFromDrawingImage(bitmap);
    }

    private static WpfBrush BrushFromHex(string hex)
    {
        try
        {
            return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return WpfBrushes.Transparent;
        }
    }

    private static ImageSource RenderColorSwatch(string hex, int size)
    {
        var swatchSize = Math.Max(18, size);
        using var bitmap = new System.Drawing.Bitmap(swatchSize, swatchSize);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);

        var color = System.Drawing.ColorTranslator.FromHtml(hex);
        using var fill = new System.Drawing.SolidBrush(color);
        using var border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(190, 244, 238, 231), Math.Max(1, swatchSize / 18));
        var inset = Math.Max(2, swatchSize / 12);
        graphics.FillEllipse(fill, inset, inset, swatchSize - inset * 2, swatchSize - inset * 2);
        graphics.DrawEllipse(border, inset, inset, swatchSize - inset * 2, swatchSize - inset * 2);
        return BitmapFromDrawingImage(bitmap);
    }

    private static string ThemeSvg(string svg, string color)
    {
        return Regex.Replace(svg, @"#[0-9a-fA-F]{3,8}|rgb\([^)]+\)|black|#000", color, RegexOptions.IgnoreCase);
    }

    private static string ReadSvgText(string fileName)
    {
        lock (SvgCacheGate)
        {
            if (SvgTextCache.TryGetValue(fileName, out var cached))
            {
                return cached;
            }

            var svg = File.ReadAllText(AssetIconPath(fileName));
            SvgTextCache[fileName] = svg;
            return svg;
        }
    }

    private static string AssetIconPath(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "icons", fileName);
        if (File.Exists(path)) return path;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "icons", fileName));
    }

    internal static string AppIconPath(AppIconPreference preference)
    {
        var fileName = preference == AppIconPreference.Dark ? "clip-tile-dark.ico" : "clip-tile-light.ico";
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "app-icons", fileName);
        if (File.Exists(path)) return path;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "app-icons", fileName));
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapSource BitmapFromDrawingImage(DrawingImage image)
    {
        using var bitmap = new System.Drawing.Bitmap(image);
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    internal static Style ThinScrollBarStyle(WpfBrush thumb)
    {
        var hex = "#6B656B";
        if (thumb is SolidColorBrush solid)
        {
            hex = $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}";
        }

        return (Style)XamlReader.Parse($$"""
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="{x:Type ScrollBar}" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Setter Property="Width" Value="6"/>
  <Setter Property="Height" Value="6"/>
  <Setter Property="Background" Value="Transparent"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="{x:Type ScrollBar}">
        <Grid Background="Transparent" SnapsToDevicePixels="True">
          <Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command="ScrollBar.LineUpCommand" Background="Transparent" BorderThickness="0" Opacity="0" Focusable="False"/>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType="{x:Type Thumb}">
                    <Border Background="{{hex}}" CornerRadius="3" Margin="1"/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command="ScrollBar.LineDownCommand" Background="Transparent" BorderThickness="0" Opacity="0" Focusable="False"/>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
""");
    }

    private static (string? Name, string? Path) ForegroundSource()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            var name = !string.IsNullOrWhiteSpace(path) ? System.IO.Path.GetFileNameWithoutExtension(path) : process.ProcessName;
            return (DisplaySourceName(name), path);
        }
        catch
        {
            return ("Unknown", null);
        }
    }

    private static IntPtr FocusedChildWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var thread = GetWindowThreadProcessId(hwnd, out _);
        if (thread == 0)
        {
            return IntPtr.Zero;
        }

        var info = new GuiThreadInfo { CbSize = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.HwndFocus : IntPtr.Zero;
    }

    private static AutomationElement? FocusedAutomationElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch
        {
            return null;
        }
    }

    private static bool SetAutomationFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            element.SetFocus();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? AutomationValue(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool CanVerifyPasteTarget(AutomationElement? element, string? expectedText)
    {
        if (element is null || string.IsNullOrEmpty(expectedText))
        {
            return false;
        }

        try
        {
            return element.Current.ControlType == ControlType.Edit &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly;
        }
        catch
        {
            return false;
        }
    }

    internal static bool PasteLooksApplied(string? before, string? after, string? expectedText)
    {
        if (after is null || string.IsNullOrEmpty(expectedText))
        {
            return false;
        }

        if (string.Equals(after, expectedText, StringComparison.Ordinal))
        {
            return true;
        }

        return after.Contains(expectedText, StringComparison.Ordinal);
    }

    private static string AutomationSummary(AutomationElement? element)
    {
        if (element is null)
        {
            return "none";
        }

        try
        {
            var current = element.Current;
            return $"type={current.ControlType?.ProgrammaticName ?? "unknown"} name={SafeLogValue(current.Name)} automationId={SafeLogValue(current.AutomationId)} hwnd={current.NativeWindowHandle}";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var length = Math.Max(GetWindowTextLength(hwnd), 0);
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            return title.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsFileExplorerSearchTarget(IntPtr hwnd, AutomationElement? element)
    {
        if (element is null || hwnd == IntPtr.Zero)
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        if (!string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var current = element.Current;
            if (current.ControlType != ControlType.Edit)
            {
                return false;
            }

            var name = current.Name ?? string.Empty;
            var automationId = current.AutomationId ?? string.Empty;
            return name.Contains("Search", StringComparison.OrdinalIgnoreCase) ||
                automationId.Contains("Search", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldShowPaletteWithoutActivation(IntPtr hwnd, AutomationElement? element)
    {
        if (hwnd == IntPtr.Zero || element is null)
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        var windowTitle = WindowTitle(hwnd);
        try
        {
            var current = element.Current;
            return IsFocusSensitiveWebEdit(processName, current.ControlType, current.NativeWindowHandle, current.Name, windowTitle);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldCommitPasteWithEnter(IntPtr hwnd, AutomationElement? element)
    {
        if (hwnd == IntPtr.Zero || element is null)
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        var windowTitle = WindowTitle(hwnd);
        try
        {
            var current = element.Current;
            return IsGoogleEarthSearchElement(processName, current.ControlType, current.NativeWindowHandle, current.Name, windowTitle);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsFocusSensitiveWebEdit(string? processName, ControlType controlType, int nativeWindowHandle, string? name)
        => IsGoogleEarthSearchElement(processName, controlType, nativeWindowHandle, name, "Google Earth");

    internal static bool IsFocusSensitiveWebEdit(string? processName, ControlType controlType, int nativeWindowHandle, string? name, string? windowTitle)
        => IsGoogleEarthSearchElement(processName, controlType, nativeWindowHandle, name, windowTitle);

    internal static bool IsGoogleEarthSearchElement(string? processName, ControlType controlType, int nativeWindowHandle, string? name, string? windowTitle = null)
    {
        if (!string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (nativeWindowHandle != 0)
        {
            return false;
        }

        if (controlType != ControlType.Edit && controlType != ControlType.Group)
        {
            return false;
        }

        var elementName = name ?? string.Empty;
        return elementName.Contains("Search Google Earth", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(elementName, "Search", StringComparison.OrdinalIgnoreCase) &&
                (windowTitle ?? string.Empty).Contains("Google Earth", StringComparison.OrdinalIgnoreCase)) ||
            elementName.Contains("flt-text-editing", StringComparison.OrdinalIgnoreCase) ||
            elementName.Contains("transparentTextEditing", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyNoActivatePaletteStyle(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, WindowLongExStyle).ToInt64();
        var nextStyle = enabled
            ? style | WindowExNoActivate
            : style & ~WindowExNoActivate;
        if (nextStyle == style)
        {
            return;
        }

        SetWindowLongPtr(hwnd, WindowLongExStyle, new IntPtr(nextStyle));
        ShellLog.Info($"palette no-activate style enabled={enabled}");
    }

    private static string SafeLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public NativeRectangle CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int InputKeyboard = 1;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private const int WindowLongExStyle = -20;
    private const long WindowExNoActivate = 0x08000000L;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyEnter = 0x0D;
    private const ushort VirtualKeyV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private static readonly Guid DataTransferManagerId = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    internal static void ApplyRoundedWindowCorners(IntPtr hwnd)
    {
        try
        {
            var preference = 2;
            var result = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            ShellLog.Info($"rounded corners applied result={result}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "rounded corners failed");
        }
    }
}

internal sealed class WpfWindowHandle(IntPtr handle) : Forms.IWin32Window
{
    public IntPtr Handle { get; } = handle;
}

internal sealed class HotkeyHelpWindow : Window
{
    public HotkeyHelpWindow(ClipHotkeySettings hotkeys, WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush accentSoft, WpfBrush selectedBorder)
    {
        Title = "Clip Hotkeys";
        Width = 430;
        Height = 482;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = bg;
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control))
            {
                Close();
                e.Handled = true;
            }
        };

        var root = new Border
        {
            Background = bg,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Child = shell;

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = "Hotkeys",
            Foreground = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        });
        var close = new WpfButton
        {
            Content = "Close",
            Foreground = muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            FocusVisualStyle = null,
            Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Button}">
  <Border x:Name="Root" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" Margin="{TemplateBinding Padding}"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Root" Property="Opacity" Value="0.85"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""),
        };
        close.MouseEnter += (_, _) => { close.Background = accentSoft; close.BorderBrush = selectedBorder; close.Foreground = text; };
        close.MouseLeave += (_, _) => { close.Background = WpfBrushes.Transparent; close.BorderBrush = WpfBrushes.Transparent; close.Foreground = muted; };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var headerBorder = new Border
        {
            Child = header,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        shell.Children.Add(headerBorder);

        var rows = new StackPanel { Margin = new Thickness(22, 18, 22, 22) };
        foreach (var (key, action) in new[]
        {
            (hotkeys.OpenClip, "Open Clip"),
            (hotkeys.PasteSelected, "Paste selected item"),
            (hotkeys.CopySelected, "Copy selected item"),
            (hotkeys.PinSelected, "Pin or unpin selected item"),
            (hotkeys.OpenActions, "Open actions"),
            (hotkeys.OpenSelected, "Open selected link, file, or image"),
            (hotkeys.EditSelected, "Edit selected text"),
            (hotkeys.SaveDebugLog, "Save debug log snapshot"),
            (hotkeys.DeleteSelected, "Delete selected item"),
            (hotkeys.CloseClip, "Close Clip, close a document preview, or escape modals"),
        })
        {
            rows.Children.Add(HotkeyRow(key, action, surface, text, muted, line));
        }

        Grid.SetRow(rows, 1);
        shell.Children.Add(rows);
        Content = root;
    }

    private static Grid HotkeyRow(string key, string action, WpfBrush surface, WpfBrush text, WpfBrush muted, WpfBrush line)
    {
        var row = new Grid { MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var box = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new TextBlock { Text = key, Foreground = text, FontSize = 12, FontWeight = FontWeights.SemiBold },
        };
        row.Children.Add(box);
        var label = new TextBlock { Text = action, Foreground = muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }
}

internal sealed class OpenWithWindow : Window
{
    private static readonly Dictionary<string, List<WatcherAppChoice>> AppCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();
    private static readonly string PersistedCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip", "open-with-app-cache.json");
    private readonly string _targetPath;
    private readonly WpfBrush _bg;
    private readonly WpfBrush _surface;
    private readonly WpfBrush _surface2;
    private readonly WpfBrush _surface3;
    private readonly WpfBrush _text;
    private readonly WpfBrush _muted;
    private readonly WpfBrush _line;
    private readonly WpfBrush _selected;
    private readonly WpfBrush _accentSoft;
    private readonly WpfBrush _selectedBorder;
    private readonly WpfTextBox _search = new();
    private readonly WpfListBox _apps = new();
    private readonly TextBlock _status = new();
    private List<WatcherAppChoice> _allApps = [];

    public static void WarmCacheAsync()
    {
        LoadPersistedCache();
        _ = Task.Run(() =>
        {
            try
            {
                var watch = Stopwatch.StartNew();
                var target = Path.Combine(Path.GetTempPath(), "clip-openwith-warmup.txt");
                var apps = WatcherAppDiscovery.GetApps(target).ToList();
                lock (CacheGate)
                {
                    AppCache[CacheKey(target)] = apps;
                }

                SavePersistedCache();
                ShellLog.Info($"open-with warm cache completed count={apps.Count} elapsedMs={watch.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "open-with warm cache failed");
            }
        });
    }

    public OpenWithWindow(string targetPath, WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush surface3, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush selected, WpfBrush accentSoft, WpfBrush selectedBorder)
    {
        _targetPath = targetPath;
        _bg = bg;
        _surface = surface;
        _surface2 = surface2;
        _surface3 = surface3;
        _text = text;
        _muted = muted;
        _line = line;
        _selected = selected;
        _accentSoft = accentSoft;
        _selectedBorder = selectedBorder;

        Title = "Open With";
        Width = 620;
        Height = 520;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = bg;
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), MainWindow.ThinScrollBarStyle(muted));
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        KeyDown += OnKeyDown;

        var root = new Border
        {
            Background = bg,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        root.Child = shell;

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = $"Open with {Path.GetFileName(targetPath)}",
            Foreground = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var close = PlainButton("Close");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var searchShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(16, 8, 16, 7),
            Padding = new Thickness(10, 0, 10, 0),
        };
        _search.Background = WpfBrushes.Transparent;
        _search.Foreground = text;
        _search.BorderThickness = new Thickness(0);
        _search.FontSize = 13;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RenderApps();
        searchShell.Child = _search;
        Grid.SetRow(searchShell, 1);
        shell.Children.Add(searchShell);

        _apps.Background = bg;
        _apps.Foreground = text;
        _apps.BorderThickness = new Thickness(0);
        _apps.Margin = new Thickness(12, 0, 8, 0);
        _apps.MouseDoubleClick += (_, _) => AcceptSelection();
        _apps.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _apps.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        Grid.SetRow(_apps, 2);
        shell.Children.Add(_apps);

        var footer = new Grid { Background = surface2, Margin = new Thickness(0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = muted;
        _status.FontSize = 12;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(16, 0, 0, 0);
        footer.Children.Add(_status);
        var browse = PlainButton("Browse...");
        browse.Margin = new Thickness(0, 0, 12, 0);
        browse.Click += (_, _) => BrowseForApp();
        Grid.SetColumn(browse, 1);
        footer.Children.Add(browse);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);

        Content = root;
        Loaded += async (_, _) =>
        {
            _search.Focus();
            LoadPersistedCache();
            if (TryGetCachedApps(_targetPath, out var cached))
            {
                _allApps = cached;
                _status.Text = $"{_allApps.Count} apps";
            }
            else
            {
                _status.Text = "Loading apps...";
            }

            RenderApps();
            await LoadAppsAsync();
        };
    }

    public WatcherAppChoice? SelectedApp { get; private set; }

    private WpfButton PlainButton(string label)
    {
        var button = new WpfButton
        {
            Content = label,
            Foreground = _muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            FocusVisualStyle = null,
            Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Button}">
  <Border x:Name="Root" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" Margin="{TemplateBinding Padding}"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Root" Property="Opacity" Value="0.85"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""),
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = _accentSoft;
            button.BorderBrush = _selectedBorder;
            button.Foreground = _text;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = WpfBrushes.Transparent;
            button.BorderBrush = WpfBrushes.Transparent;
            button.Foreground = _muted;
        };
        return button;
    }

    private async Task LoadAppsAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            ShellLog.Info($"open-with async load started path={_targetPath}");
            _allApps = await Task.Run(() => WatcherAppDiscovery.GetApps(_targetPath).ToList());
            lock (CacheGate)
            {
                AppCache[CacheKey(_targetPath)] = _allApps;
            }

            SavePersistedCache();
            _status.Text = $"{_allApps.Count} apps";
            ShellLog.Info($"open-with async load completed count={_allApps.Count} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            _allApps = [];
            _status.Text = "App list failed. Use Browse.";
            ShellLog.Error(ex, $"open-with async load failed elapsedMs={watch.ElapsedMilliseconds}");
        }

        RenderApps();
    }

    private void RenderApps()
    {
        var query = _search.Text.Trim();
        var apps = _allApps
            .Where(app => string.IsNullOrWhiteSpace(query) ||
                app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (app.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                (app.AppUserModelId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(app => app.IsDefault)
            .ThenByDescending(app => app.IsRecent)
            .ThenBy(app => app.Name)
            .Take(80)
            .ToList();

        _apps.Items.Clear();
        if (_allApps.Count == 0)
        {
            _apps.Items.Add(new WpfListBoxItem
            {
                Content = RowContent(null, "Loading apps...", "You can still close this window."),
                Foreground = _muted,
                IsEnabled = false,
            });
            return;
        }

        foreach (var app in apps)
        {
            var item = new WpfListBoxItem
            {
                Tag = app,
                Content = RowContent(IconForApp(app), app.Name, app.IsDefault ? "Default app" : app.Source),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(4, 1, 4, 1),
                Background = WpfBrushes.Transparent,
                Foreground = _text,
            };
            _apps.Items.Add(item);
        }

        if (_apps.Items.Count > 0)
        {
            _apps.SelectedIndex = 0;
        }
    }

    private StackPanel RowContent(ImageSource? icon, string title, string subtitle)
    {
        var outer = new StackPanel { Orientation = WpfOrientation.Horizontal };
        outer.Children.Add(new WpfImage
        {
            Source = icon,
            Width = 26,
            Height = 26,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var panel = new StackPanel { Orientation = WpfOrientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, Foreground = _text, FontSize = 13, FontWeight = FontWeights.Medium });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = _muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        outer.Children.Add(panel);
        return outer;
    }

    private static bool TryGetCachedApps(string targetPath, out List<WatcherAppChoice> apps)
    {
        lock (CacheGate)
        {
            if (AppCache.TryGetValue(CacheKey(targetPath), out var exact))
            {
                apps = exact;
                return true;
            }

            if (AppCache.TryGetValue(".txt", out var warm))
            {
                apps = warm;
                return true;
            }
        }

        apps = [];
        return false;
    }

    private static string CacheKey(string targetPath) => Directory.Exists(targetPath) ? "<folder>" : Path.GetExtension(targetPath).ToLowerInvariant();

    private static void LoadPersistedCache()
    {
        lock (CacheGate)
        {
            if (AppCache.Count > 0 || !File.Exists(PersistedCachePath))
            {
                return;
            }

            try
            {
                var cache = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<CachedAppChoice>>>(File.ReadAllText(PersistedCachePath)) ?? [];
                foreach (var (key, apps) in cache)
                {
                    AppCache[key] = apps
                        .Select(app => new WatcherAppChoice(app.Name, app.ExecutablePath, app.Source, app.IsDefault, app.IsRecent, app.AppUserModelId))
                        .ToList();
                }

                ShellLog.Info($"open-with persisted cache loaded keys={AppCache.Count}");
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "open-with persisted cache load failed");
            }
        }
    }

    private static void SavePersistedCache()
    {
        lock (CacheGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PersistedCachePath)!);
                var cache = AppCache.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                        .Select(app => new CachedAppChoice(app.Name, app.ExecutablePath, app.Source, app.IsDefault, app.IsRecent, app.AppUserModelId))
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(PersistedCachePath, System.Text.Json.JsonSerializer.Serialize(cache));
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, "open-with persisted cache save failed");
            }
        }
    }

    private sealed record CachedAppChoice(string Name, string? ExecutablePath, string Source, bool IsDefault, bool IsRecent, string? AppUserModelId);

    private ImageSource? IconForApp(WatcherAppChoice app)
    {
        var key = app.AppUserModelId ?? app.ExecutablePath ?? app.Name;
        lock (CacheGate)
        {
            if (IconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        try
        {
            DrawingImage? image = null;
            if (app.IsDefault)
            {
                image = WatcherShellIconReader.TryGetIcon(_targetPath, large: false);
            }
            else if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                image = WatcherShellIconReader.TryGetIcon($"shell:AppsFolder\\{app.AppUserModelId}", large: false) ??
                    WatcherPackageLogoLookup.TryGetIcon(app.AppUserModelId) ??
                    WatcherStartMenuIconLookup.TryGetIcon(app.Name);
            }
            else if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(app.ExecutablePath);
                image = icon?.ToBitmap();
            }

            image ??= WatcherStartMenuIconLookup.TryGetIcon(app.Name) ?? System.Drawing.SystemIcons.Application.ToBitmap();
            var source = MainWindow.BitmapFromDrawingImage(image);
            image.Dispose();
            lock (CacheGate)
            {
                IconCache[key] = source;
            }

            return source;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with icon failed app={app.Name}");
            return null;
        }
    }

    private void AcceptSelection()
    {
        if (_apps.SelectedItem is not WpfListBoxItem { Tag: WatcherAppChoice app })
        {
            return;
        }

        SelectedApp = app;
        Close();
    }

    private void BrowseForApp()
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Applications|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        SelectedApp = new WatcherAppChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Browse");
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            AcceptSelection();
            e.Handled = true;
        }
    }
}

internal sealed class ExcludedAppPickerWindow : Window
{
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object IconCacheGate = new();
    private readonly WpfBrush _text;
    private readonly WpfBrush _muted;
    private readonly WpfBrush _surface;
    private readonly WpfBrush _line;
    private readonly WpfBrush _accentSoft;
    private readonly WpfBrush _selectedBorder;
    private readonly WpfTextBox _search = new();
    private readonly WpfListBox _apps = new();
    private readonly TextBlock _status = new();
    private List<WatcherAppChoice> _allApps = [];

    public ExcludedAppPickerWindow(WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush surface3, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush selected, WpfBrush accentSoft, WpfBrush selectedBorder)
    {
        _text = text;
        _muted = muted;
        _surface = surface;
        _line = line;
        _accentSoft = accentSoft;
        _selectedBorder = selectedBorder;

        Title = "Choose Excluded App";
        Width = 620;
        Height = 520;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = bg;
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), MainWindow.ThinScrollBarStyle(muted));
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        KeyDown += OnKeyDown;

        var root = new Border
        {
            Background = bg,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        root.Child = shell;

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        header.Children.Add(new TextBlock
        {
            Text = "Choose app to exclude",
            Foreground = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0),
        });
        var close = PlainButton("Close");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var searchShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(16, 8, 16, 7),
            Padding = new Thickness(10, 0, 10, 0),
        };
        _search.Background = WpfBrushes.Transparent;
        _search.Foreground = text;
        _search.BorderThickness = new Thickness(0);
        _search.FontSize = 13;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => RenderApps();
        searchShell.Child = _search;
        Grid.SetRow(searchShell, 1);
        shell.Children.Add(searchShell);

        _apps.Background = bg;
        _apps.Foreground = text;
        _apps.BorderThickness = new Thickness(0);
        _apps.Margin = new Thickness(12, 0, 8, 0);
        _apps.MouseDoubleClick += (_, _) => AcceptSelection();
        _apps.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _apps.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        Grid.SetRow(_apps, 2);
        shell.Children.Add(_apps);

        var footer = new Grid { Background = surface2 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = muted;
        _status.FontSize = 12;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(16, 0, 0, 0);
        footer.Children.Add(_status);
        var browse = PlainButton("Browse...");
        browse.Margin = new Thickness(0, 0, 12, 0);
        browse.Click += (_, _) => BrowseForApp();
        Grid.SetColumn(browse, 1);
        footer.Children.Add(browse);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);

        Content = root;
        Loaded += async (_, _) =>
        {
            _search.Focus();
            await LoadAppsAsync();
        };
    }

    public WatcherAppChoice? SelectedApp { get; private set; }

    private WpfButton PlainButton(string label)
    {
        var button = new WpfButton
        {
            Content = label,
            Foreground = _muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            FocusVisualStyle = null,
            Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Button}">
  <Border x:Name="Root" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" Margin="{TemplateBinding Padding}"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Root" Property="Opacity" Value="0.85"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""),
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = _accentSoft;
            button.BorderBrush = _selectedBorder;
            button.Foreground = _text;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = WpfBrushes.Transparent;
            button.BorderBrush = WpfBrushes.Transparent;
            button.Foreground = _muted;
        };
        return button;
    }

    private async Task LoadAppsAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            _status.Text = "Loading apps...";
            var target = Path.Combine(Path.GetTempPath(), "clip-privacy-app-picker.txt");
            _allApps = await Task.Run(() => WatcherAppDiscovery.GetApps(target).Where(app => !string.IsNullOrWhiteSpace(app.ExecutablePath)).ToList());
            _status.Text = $"{_allApps.Count} apps";
            ShellLog.Info($"privacy app picker loaded count={_allApps.Count} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            _allApps = [];
            _status.Text = "App list failed. Use Browse.";
            ShellLog.Error(ex, "privacy app picker load failed");
        }

        RenderApps();
    }

    private void RenderApps()
    {
        var query = _search.Text.Trim();
        var apps = _allApps
            .Where(app => string.IsNullOrWhiteSpace(query) ||
                app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (app.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                (app.AppUserModelId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(app => app.IsRecent)
            .ThenBy(app => app.Name)
            .Take(80)
            .ToList();

        _apps.Items.Clear();
        if (_allApps.Count == 0)
        {
            _apps.Items.Add(new WpfListBoxItem
            {
                Content = RowContent(null, "Loading apps...", "Use Browse if the app is not listed."),
                Foreground = _muted,
                IsEnabled = false,
            });
            return;
        }

        foreach (var app in apps)
        {
            var item = new WpfListBoxItem
            {
                Tag = app,
                Content = RowContent(IconForApp(app), app.Name, app.ExecutablePath ?? app.Source),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(4, 1, 4, 1),
                Background = WpfBrushes.Transparent,
                Foreground = _text,
            };
            _apps.Items.Add(item);
        }

        if (_apps.Items.Count > 0)
        {
            _apps.SelectedIndex = 0;
        }
    }

    private StackPanel RowContent(ImageSource? icon, string title, string subtitle)
    {
        var outer = new StackPanel { Orientation = WpfOrientation.Horizontal };
        outer.Children.Add(new WpfImage
        {
            Source = icon,
            Width = 26,
            Height = 26,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var panel = new StackPanel { Orientation = WpfOrientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, Foreground = _text, FontSize = 13, FontWeight = FontWeights.Medium });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = _muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        outer.Children.Add(panel);
        return outer;
    }

    private ImageSource? IconForApp(WatcherAppChoice app)
    {
        var key = app.AppUserModelId ?? app.ExecutablePath ?? app.Name;
        lock (IconCacheGate)
        {
            if (IconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        try
        {
            DrawingImage? image = null;
            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                image = WatcherShellIconReader.TryGetIcon($"shell:AppsFolder\\{app.AppUserModelId}", large: false) ??
                    WatcherPackageLogoLookup.TryGetIcon(app.AppUserModelId) ??
                    WatcherStartMenuIconLookup.TryGetIcon(app.Name);
            }
            else if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(app.ExecutablePath);
                image = icon?.ToBitmap();
            }

            image ??= WatcherStartMenuIconLookup.TryGetIcon(app.Name) ?? System.Drawing.SystemIcons.Application.ToBitmap();
            var source = MainWindow.BitmapFromDrawingImage(image);
            image.Dispose();
            lock (IconCacheGate)
            {
                IconCache[key] = source;
            }

            return source;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"privacy app picker icon failed app={app.Name}");
            return null;
        }
    }

    private void AcceptSelection()
    {
        if (_apps.SelectedItem is not WpfListBoxItem { Tag: WatcherAppChoice app })
        {
            return;
        }

        SelectedApp = app;
        DialogResult = true;
    }

    private void BrowseForApp()
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Choose an app to exclude",
            Filter = "Applications|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        SelectedApp = new WatcherAppChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Browse");
        DialogResult = true;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            AcceptSelection();
            e.Handled = true;
        }
    }
}

internal sealed record SettingsPalette(WpfBrush Bg, WpfBrush Surface, WpfBrush Surface2, WpfBrush Surface3, WpfBrush Text, WpfBrush Muted, WpfBrush Line, WpfBrush Line2, WpfBrush Accent, WpfBrush AccentSoft, WpfBrush Selected, WpfBrush SelectedBorder);

internal sealed class SettingsWindow : Window
{
    private readonly Grid _content = new();
    private readonly Dictionary<string, WpfButton> _nav = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipShellSettings _settings;
    private ClipUpdateStatus _updateStatus;
    private readonly Action<ClipThemePreference> _applyTheme;
    private readonly Action _refreshClipboardManagerTextTheme;
    private readonly Action<AppIconPreference> _applyAppIcon;
    private readonly Action<bool> _applyRunAtStartup;
    private readonly Action<int?> _applyHistoryLimit;
    private readonly Action<long?> _applyMaxItemSize;
    private readonly Action<bool, bool> _applyUpdateSettings;
    private readonly Action<Action<ClipUpdateStatus>> _checkForUpdates;
    private readonly Func<ClipUpdateStatus, Task> _installUpdate;
    private readonly Action<bool> _clearHistory;
    private readonly Action _openDataFolder;
    private readonly Action _openDebugLog;
    private readonly Action<string> _changeClipboardFolder;
    private readonly Action _resetClipboardFolder;
    private readonly Action<ClipHotkeySettings> _applyHotkeys;
    private readonly Action<ClipPrivacySettings> _applyPrivacy;
    private readonly Action<PasteFormatPreference> _applyDefaultPasteFormat;
    private readonly Action _resetAllSettings;
    private readonly Func<SettingsPalette> _paletteProvider;
    private readonly ImageSource _dropdownIcon;
    private readonly System.Windows.Threading.DispatcherTimer _themeApplyTimer = new(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
    private SettingsPalette? _paletteOverride;
    private ThemeMorphIcon? _themeIcon;
    private Border? _root;
    private Grid? _header;
    private Border? _headerBorder;
    private TextBlock? _headerTitle;
    private WpfButton? _closeButton;
    private Border? _sidebarBorder;
    private StackPanel? _sidebar;
    private ScrollViewer? _contentScroll;
    private WpfBrush _bg = WpfBrushes.Transparent;
    private WpfBrush _surface = WpfBrushes.Transparent;
    private WpfBrush _surface2 = WpfBrushes.Transparent;
    private WpfBrush _surface3 = WpfBrushes.Transparent;
    private WpfBrush _text = WpfBrushes.Black;
    private WpfBrush _muted = WpfBrushes.Gray;
    private WpfBrush _line = WpfBrushes.Transparent;
    private WpfBrush _line2 = WpfBrushes.Transparent;
    private WpfBrush _accent = WpfBrushes.Teal;
    private WpfBrush _accentSoft = WpfBrushes.Transparent;
    private WpfBrush _selected = WpfBrushes.Transparent;
    private WpfBrush _selectedBorder = WpfBrushes.Transparent;
    private string _currentPage = "General";

    public SettingsWindow(ClipShellSettings settings, ClipUpdateStatus updateStatus, Action<ClipThemePreference> applyTheme, Action refreshClipboardManagerTextTheme, Action<AppIconPreference> applyAppIcon, Action<bool> applyRunAtStartup, Action<int?> applyHistoryLimit, Action<long?> applyMaxItemSize, Action<bool, bool> applyUpdateSettings, Action<Action<ClipUpdateStatus>> checkForUpdates, Func<ClipUpdateStatus, Task> installUpdate, Action openDataFolder, Action openDebugLog, Action<bool> clearHistory, Action<string> changeClipboardFolder, Action resetClipboardFolder, Action<ClipHotkeySettings> applyHotkeys, Action<ClipPrivacySettings> applyPrivacy, Action<PasteFormatPreference> applyDefaultPasteFormat, Action resetAllSettings, ImageSource dropdownIcon, Func<SettingsPalette> paletteProvider)
    {
        _settings = settings;
        _updateStatus = updateStatus;
        _applyTheme = applyTheme;
        _refreshClipboardManagerTextTheme = refreshClipboardManagerTextTheme;
        _applyAppIcon = applyAppIcon;
        _applyRunAtStartup = applyRunAtStartup;
        _applyHistoryLimit = applyHistoryLimit;
        _applyMaxItemSize = applyMaxItemSize;
        _applyUpdateSettings = applyUpdateSettings;
        _checkForUpdates = checkForUpdates;
        _installUpdate = installUpdate;
        _clearHistory = clearHistory;
        _openDataFolder = openDataFolder;
        _openDebugLog = openDebugLog;
        _changeClipboardFolder = changeClipboardFolder;
        _resetClipboardFolder = resetClipboardFolder;
        _applyHotkeys = applyHotkeys;
        _applyPrivacy = applyPrivacy;
        _applyDefaultPasteFormat = applyDefaultPasteFormat;
        _resetAllSettings = resetAllSettings;
        _paletteProvider = paletteProvider;
        _dropdownIcon = dropdownIcon;
        ApplyPalette(_paletteProvider());
        _themeApplyTimer.Tick += (_, _) => ApplyPendingTheme();

        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), MainWindow.ThinScrollBarStyle(_muted));
        Title = "Clip Settings";
        Width = 720;
        Height = 500;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Background = _bg;
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => CenterOnCursorScreen();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        var root = new Border
        {
            Background = _bg,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
        };
        _root = root;
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Child = shell;

        var header = new Grid { Background = _surface2 };
        _header = header;
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        var headerTitle = new TextBlock
        {
            Text = "Settings",
            Foreground = _text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        };
        _headerTitle = headerTitle;
        header.Children.Add(headerTitle);
        var close = new WpfButton
        {
            Content = "Close",
            Foreground = _muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Template = TransparentButtonTemplate(),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _closeButton = close;
        close.MouseEnter += (_, _) =>
        {
            close.Background = _accentSoft;
            close.BorderBrush = _selectedBorder;
            close.Foreground = _text;
        };
        close.MouseLeave += (_, _) =>
        {
            close.Background = WpfBrushes.Transparent;
            close.BorderBrush = WpfBrushes.Transparent;
            close.Foreground = _muted;
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var headerBorder = new Border
        {
            Child = header,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        _headerBorder = headerBorder;
        shell.Children.Add(headerBorder);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        shell.Children.Add(body);

        var sidebar = new StackPanel
        {
            Background = _surface2,
            Margin = new Thickness(12, 14, 12, 12),
        };
        _sidebar = sidebar;
        foreach (var page in new[] { "General", "History", "Shortcuts", "Privacy", "App Overrides", "Appearance", "About" })
        {
            var button = NavButton(page);
            button.MouseEnter += (_, _) => ApplyNavButtonTheme(page, button);
            button.MouseLeave += (_, _) => ApplyNavButtonTheme(page, button);
            button.Click += (_, _) => ShowPage(page);
            _nav[page] = button;
            sidebar.Children.Add(button);
        }
        var sidebarBorder = new Border
        {
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = sidebar,
        };
        _sidebarBorder = sidebarBorder;
        body.Children.Add(sidebarBorder);

        _content.Background = _surface;
        _content.Margin = new Thickness(0);
        var contentScroll = new ScrollViewer
        {
            Content = _content,
            Background = _surface,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _contentScroll = contentScroll;
        Grid.SetColumn(contentScroll, 1);
        body.Children.Add(contentScroll);

        Content = root;
        ShowPage("General");
    }

    private void CenterOnCursorScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        var screenWidth = bottomRight.X - topLeft.X;
        var screenHeight = bottomRight.Y - topLeft.Y;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        Left = topLeft.X + Math.Max(0, (screenWidth - w) / 2);
        Top = topLeft.Y + Math.Max(0, (screenHeight - h) / 2);
    }

    private WpfButton NavButton(string label)
    {
        return new WpfButton
        {
            Content = label,
            Template = TransparentButtonTemplate(),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Height = 36,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(11, 0, 11, 0),
            Background = WpfBrushes.Transparent,
            Foreground = _muted,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            FontSize = 13,
        };
    }

    private static ControlTemplate TransparentButtonTemplate()
    {
        return (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="5">
    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                      Margin="{TemplateBinding Padding}"/>
  </Border>
</ControlTemplate>
""");
    }

    private static ControlTemplate SubtleSettingsButtonTemplate()
    {
        return (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="6">
    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                      Margin="{TemplateBinding Padding}"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.82"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
    }

    private void ApplyPalette(SettingsPalette palette)
    {
        _bg = palette.Bg;
        _surface = palette.Surface;
        _surface2 = palette.Surface2;
        _surface3 = palette.Surface3;
        _text = palette.Text;
        _muted = palette.Muted;
        _line = palette.Line;
        _line2 = palette.Line2;
        _accent = palette.Accent;
        _accentSoft = palette.AccentSoft;
        _selected = palette.Selected;
        _selectedBorder = palette.SelectedBorder;
    }

    private static SettingsPalette PaletteForTheme(ClipThemePreference preference)
    {
        var useDark = preference switch
        {
            ClipThemePreference.Light => false,
            ClipThemePreference.Dark => true,
            _ => MainWindow.IsWindowsDarkMode(),
        };

        return new SettingsPalette(
            FrozenBrush(useDark ? "#1A1A1A" : "#F7F7F7"),
            FrozenBrush(useDark ? "#212121" : "#FFFFFF"),
            FrozenBrush(useDark ? "#272727" : "#EDEDED"),
            FrozenBrush(useDark ? "#323232" : "#DCDCDC"),
            FrozenBrush(useDark ? "#F1F1F1" : "#1A1A1A"),
            FrozenBrush(useDark ? "#989898" : "#646464"),
            FrozenBrush(useDark ? "#494949" : "#B8B8B8"),
            FrozenBrush(useDark ? "#5A5A5A" : "#989898"),
            FrozenBrush(useDark ? "#8A9CCC" : "#3B5BDB"),
            FrozenBrush(useDark ? "#232A45" : "#E1E7FB"),
            FrozenBrush(useDark ? "#324068" : "#C9D3F5"),
            FrozenBrush(useDark ? "#6878A8" : "#5C7CFA"));
    }

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void RefreshTheme(bool rebuildPage = true)
    {
        ApplyPalette(_paletteOverride ?? _paletteProvider());
        Background = _bg;
        if (_root is not null)
        {
            _root.Background = _bg;
            _root.BorderBrush = _line;
        }

        if (_header is not null)
        {
            _header.Background = _surface2;
        }

        if (_headerBorder is not null)
        {
            _headerBorder.BorderBrush = _line;
        }

        if (_headerTitle is not null)
        {
            _headerTitle.Foreground = _text;
        }

        if (_closeButton is not null && !_closeButton.IsMouseOver)
        {
            _closeButton.Background = WpfBrushes.Transparent;
            _closeButton.Foreground = _muted;
        }

        if (_sidebar is not null)
        {
            _sidebar.Background = _surface2;
        }

        if (_sidebarBorder is not null)
        {
            _sidebarBorder.Background = _surface2;
            _sidebarBorder.BorderBrush = _line;
        }

        _content.Background = _surface;
        if (_contentScroll is not null)
        {
            _contentScroll.Background = _surface;
        }

        RefreshNavigationTheme();

        if (rebuildPage)
        {
            ShowPage(_currentPage);
        }
    }

    private void ShowPage(string page)
    {
        _currentPage = page;
        RefreshNavigationTheme();

        _content.Children.Clear();
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        panel.Children.Add(BuildPageHeader(page));
        panel.Children.Add(PageSubtitle(page));

        if (string.Equals(page, "General", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(StartupRow());
            panel.Children.Add(UpdateCheckRow());
            panel.Children.Add(DefaultPasteFormatRow());
        }

        if (string.Equals(page, "Appearance", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(ThemeRow());
            panel.Children.Add(AppIconRow());
        }

        if (string.Equals(page, "History", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(HistoryLimitRow());
            panel.Children.Add(MaxItemSizeRow());
            panel.Children.Add(ClearHistoryRow());
            panel.Children.Add(DataFolderRow());
        }

        if (string.Equals(page, "Shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var row in HotkeyRows())
            {
                panel.Children.Add(row);
            }

            panel.Children.Add(ResetHotkeysRow());
        }

        if (string.Equals(page, "Privacy", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(AddExcludedAppRow());
            foreach (var app in _settings.Privacy.ExcludedApps)
            {
                panel.Children.Add(ExcludedAppRow(app));
            }
        }

        if (string.Equals(page, "App Overrides", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(AddAppOverrideRow());
            foreach (var entry in _settings.AppOverrides)
            {
                panel.Children.Add(AppOverrideRow(entry));
            }
        }

        if (string.Equals(page, "About", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(Row("Version", ClipUpdateService.CurrentVersion));
            panel.Children.Add(Row("Updates", _settings.CheckForUpdatesOnStartup
                ? "Checks automatically"
                : "Manual checks only"));
            panel.Children.Add(Row("Data folder", _settings.EffectiveClipboardFolderPath()));
            panel.Children.Add(Row("Update status", _updateStatus.Message));
            panel.Children.Add(AboutActionsRow());
        }

        foreach (var row in RowsFor(page))
        {
            panel.Children.Add(Row(row.Label, row.Value));
        }

        if (string.Equals(page, "General", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(ResetDefaultsFooter());
        }

        _content.Children.Add(panel);
    }

    private void RefreshNavigationTheme()
    {
        foreach (var (name, button) in _nav)
        {
            ApplyNavButtonTheme(name, button);
        }
    }

    private void ApplyNavButtonTheme(string page, WpfButton button)
    {
        var active = string.Equals(_currentPage, page, StringComparison.OrdinalIgnoreCase);
        if (active)
        {
            button.Background = _selected;
            button.Foreground = _text;
            button.BorderBrush = _selectedBorder;
            return;
        }

        button.Background = button.IsMouseOver ? _surface3 : WpfBrushes.Transparent;
        button.Foreground = button.IsMouseOver ? _text : _muted;
        button.BorderBrush = button.IsMouseOver ? _line2 : WpfBrushes.Transparent;
    }

    private FrameworkElement BuildPageHeader(string page)
    {
        var info = InfoDescriptionFor(page);
        if (info is null)
        {
            return new TextBlock
            {
                Text = page,
                Foreground = _text,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = page,
            Foreground = _text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var badge = InfoBadge();
        badge.Click += (_, _) => ShowInfoPopup(page, info);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        return grid;
    }

    private static string? InfoDescriptionFor(string page) => page switch
    {
        "Privacy" => "Apps listed here are excluded from future Clip history to help prevent saving copied information from sensitive apps, such as password managers, banking apps, and private browsers.",
        "App Overrides" => "Apps here will use custom hotkeys for two actions: Open Clip (which key opens Clip while that app is focused) and Paste (which key Clip sends when pasting into that app). For example, if Photoshop already uses Alt+V, you can override Open Clip to a different shortcut while in Photoshop, or change Paste to send a different keystroke. Add an app, pick the action, and set the hotkey.",
        _ => null,
    };

    private WpfButton InfoBadge()
    {
        var glyph = new TextBlock
        {
            Text = "i",
            Foreground = _muted,
            FontFamily = new System.Windows.Media.FontFamily("Cambria, Georgia, Segoe UI"),
            FontStyle = FontStyles.Italic,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var ring = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderBrush = _muted,
            BorderThickness = new Thickness(1),
            Background = WpfBrushes.Transparent,
            Child = glyph,
        };
        var button = new WpfButton
        {
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = ring,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "What is this?",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            FocusVisualStyle = null,
        };
        button.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type Button}">
  <Border Background="{TemplateBinding Background}" CornerRadius="11">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True">
      <Setter Property="Opacity" Value="0.75"/>
    </Trigger>
    <Trigger Property="IsPressed" Value="True">
      <Setter Property="Opacity" Value="0.5"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        return button;
    }

    private void ShowInfoPopup(string title, string description)
    {
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = _text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = _muted,
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });

        var close = SecondaryButton("Got it");
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(close);

        var border = new Border
        {
            Background = _surface,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };

        var shell = new Grid
        {
            Background = WpfBrushes.Transparent,
            Margin = new Thickness(24),
        };
        shell.Children.Add(border);

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.Center,
            PlacementTarget = this,
            Child = shell,
        };
        close.Click += (_, _) => popup.IsOpen = false;
        popup.IsOpen = true;
    }

    private TextBlock PageSubtitle(string page)
    {
        var subtitle = page switch
        {
            "General" => "Behavior of the Clip clipboard manager",
            "History" => "Storage, limits, and cleanup",
            "Shortcuts" => "Keyboard controls for Clip",
            "Privacy" => "Apps excluded from clipboard history",
            "App Overrides" => "Custom hotkeys per app for Clip actions",
            "Appearance" => "Theme and icon preferences",
            "About" => "Version, updates, and support files",
            _ => string.Empty,
        };

        return new TextBlock
        {
            Text = subtitle,
            Foreground = _muted,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18),
        };
    }

    private IEnumerable<(string Label, string Value)> RowsFor(string page)
    {
        return page switch
        {
            "History" => new[]
            {
                ("Pinned items", "Kept until unpinned"),
                ("Duplicate handling", "Same content updates copy count"),
            },
            "Shortcuts" => [],
            "Appearance" => [],
            "Privacy" => [],
            "App Overrides" => [],
            "About" => [],
            _ => [],
        };
    }

    private Border PrivacyNote()
    {
        return new Border
        {
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = "Apps listed here are excluded from future Clip history to help prevent saving copied information from sensitive apps, such as password managers, banking apps, and private browsers.",
                Foreground = _muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
            },
        };
    }

    private Border AddExcludedAppRow()
    {
        var button = SecondaryButton("Add app");
        button.Width = 104;
        button.Click += (_, _) =>
        {
            var picker = new ExcludedAppPickerWindow(_bg, _surface, _surface2, _surface3, _text, _muted, _line, _selected, _accentSoft, _selectedBorder)
            {
                Owner = this,
            };
            if (picker.ShowDialog() == true && picker.SelectedApp is not null)
            {
                var privacy = CopyPrivacy();
                privacy.AddExcludedApp(picker.SelectedApp.Name, picker.SelectedApp.ExecutablePath);
                ApplyPrivacyChange(privacy);
                ShowPage("Privacy");
            }
        };

        return ControlRow("Add excluded app", "Choose from installed apps or browse for an .exe.", button);
    }

    private Border ExcludedAppRow(ClipExcludedApp app)
    {
        var button = SecondaryButton("Remove");
        button.Click += (_, _) =>
        {
            var privacy = CopyPrivacy();
            privacy.RemoveExcludedApp(app);
            ApplyPrivacyChange(privacy);
            ShowPage("Privacy");
        };

        return ControlRow(app.Name, string.IsNullOrWhiteSpace(app.ExecutablePath) ? "Excluded from clipboard history." : app.ExecutablePath, button);
    }

    private ClipPrivacySettings CopyPrivacy()
    {
        return new ClipPrivacySettings
        {
            ExcludedApps = _settings.Privacy.ExcludedApps
                .Select(app => ClipExcludedApp.Create(app.Name, app.ExecutablePath))
                .Where(app => app is not null)
                .Select(app => app!)
                .ToList(),
        };
    }

    private void ApplyPrivacyChange(ClipPrivacySettings privacy)
    {
        _applyPrivacy(privacy);
        _settings.Privacy = privacy;
    }

    private Border AppOverrideNote()
    {
        return new Border
        {
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = "Apps here will use custom hotkeys for two actions: Open Clip (which key opens Clip while that app is focused) and Paste (which key Clip sends when pasting into that app). For example, if Photoshop already uses Alt+V, you can override Open Clip to a different shortcut while in Photoshop, or change Paste to send a different keystroke. Add an app, pick the action, and set the hotkey.",
                Foreground = _muted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
            },
        };
    }

    private Border AddAppOverrideRow()
    {
        var button = SecondaryButton("Add app");
        button.Width = 104;
        button.Click += (_, _) =>
        {
            var picker = new ExcludedAppPickerWindow(_bg, _surface, _surface2, _surface3, _text, _muted, _line, _selected, _accentSoft, _selectedBorder)
            {
                Owner = this,
            };
            if (picker.ShowDialog() == true && picker.SelectedApp is not null)
            {
                var name = ProcessNameFromAppEntry(picker.SelectedApp.Name, picker.SelectedApp.ExecutablePath);
                if (string.IsNullOrWhiteSpace(name)) return;
                _settings.AppOverrides.Add(new ClipAppOverride
                {
                    AppName = name,
                    ExecutablePath = picker.SelectedApp.ExecutablePath,
                    Action = ClipAppOverride.ActionPaste,
                    Hotkey = "Alt+V",
                });
                _settings.Save();
                ShowPage("App Overrides");
            }
        };

        return ControlRow("Add app override", "Choose an app, then pick an action and a custom hotkey for it.", button);
    }

    private Border AppOverrideRow(ClipAppOverride entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            var resolved = ResolveExecutablePathFromProcessName(entry.AppName);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                entry.ExecutablePath = resolved;
                _settings.Save();
            }
        }

        var actionDropdown = StyledDropdown(entry.Action, ClipAppOverride.AvailableActions, selected =>
        {
            if (string.Equals(entry.Action, selected, StringComparison.OrdinalIgnoreCase)) return;
            entry.Action = selected;
            _settings.Save();
        });
        actionDropdown.Width = 108;

        var hotkeyInput = HotkeyInput(entry.Hotkey, requireModifier: true, value =>
        {
            entry.Hotkey = value;
            _settings.Save();
        });
        hotkeyInput.Width = 108;

        var remove = SecondaryButton("Remove");
        remove.Click += (_, _) =>
        {
            _settings.AppOverrides.Remove(entry);
            _settings.Save();
            ShowPage("App Overrides");
        };

        var controls = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
        };
        controls.Children.Add(actionDropdown);
        controls.Children.Add(new Border { Width = 6 });
        controls.Children.Add(hotkeyInput);
        controls.Children.Add(new Border { Width = 6 });
        controls.Children.Add(remove);

        var grid = new Grid { MinHeight = 64 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = entry.AppName,
            Foreground = _text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(nameText, 0);
        Grid.SetColumn(nameText, 0);
        grid.Children.Add(nameText);

        var pathText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.ExecutablePath) ? "Path not available — re-add to refresh." : entry.ExecutablePath!,
            Foreground = _muted,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 12),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(pathText, 1);
        Grid.SetColumn(pathText, 0);
        Grid.SetColumnSpan(pathText, 3);
        grid.Children.Add(pathText);

        Grid.SetRow(controls, 0);
        Grid.SetColumn(controls, 2);
        grid.Children.Add(controls);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static string? ResolveExecutablePathFromProcessName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        try
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string ProcessNameFromAppEntry(string? name, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                return Path.GetFileNameWithoutExtension(executablePath);
            }
            catch
            {
            }
        }

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        return trimmed;
    }

    private Border StartupRow()
    {
        var enabled = StartupRegistration.IsEnabled();
        var toggle = StartupToggle(enabled);
        return ControlRow(
            "Run at startup",
            "Start Clip when you log in to Windows.",
            toggle);
    }

    private Border UpdateCheckRow()
    {
        var toggle = ToggleButton(_settings.CheckForUpdatesOnStartup, next =>
        {
            _settings.CheckForUpdatesOnStartup = next;
            _applyUpdateSettings(_settings.CheckForUpdatesOnStartup, _settings.InstallUpdatesAutomatically);
        });

        return ControlRow("Check for updates", "Look for new Clip releases when the app opens and while it runs.", toggle);
    }

    private Border HistoryLimitRow()
    {
        return ControlRow(
            "History limit",
            "Maximum unpinned items to keep.",
            StyledDropdown(HistoryLimitLabel(_settings.HistoryLimit), new[] { "100", "250", "500", "1000", "Unlimited" }, selected =>
            {
                var limit = string.Equals(selected, "Unlimited", StringComparison.OrdinalIgnoreCase)
                    ? (int?)null
                    : int.Parse(selected, System.Globalization.CultureInfo.InvariantCulture);
                if (limit == _settings.HistoryLimit)
                {
                    return;
                }

                _settings.HistoryLimit = limit;
                _applyHistoryLimit(limit);
                ShellLog.Info($"settings history limit changed limit={selected}");
            }));
    }

    private static string HistoryLimitLabel(int? limit) => limit is null ? "Unlimited" : limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private Border MaxItemSizeRow()
    {
        return ControlRow(
            "Max item size",
            "Ignore copied items larger than this.",
            StyledDropdown(ClipItemSizeLimit.MaxItemSizeLabel(_settings.MaxItemSizeBytes), new[] { "10 MB", "25 MB", "50 MB", "100 MB", "Unlimited" }, selected =>
            {
                var maxBytes = ParseMaxItemSize(selected);
                if (maxBytes == _settings.MaxItemSizeBytes)
                {
                    return;
                }

                _settings.MaxItemSizeBytes = maxBytes;
                _applyMaxItemSize(maxBytes);
            }));
    }

    private static long? ParseMaxItemSize(string label)
    {
        if (label.Equals("Unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var numberText = label.Replace("MB", "", StringComparison.OrdinalIgnoreCase).Trim();
        return long.TryParse(numberText, out var megabytes) ? megabytes * 1024 * 1024 : 50L * 1024 * 1024;
    }

    private Border ClearHistoryRow()
    {
        const double clearControlWidth = 142;
        var includePinned = false;
        var generalOption = ClearHistorySegment("General", selected: true);
        var allOption = ClearHistorySegment("All", selected: false);
        void Refresh()
        {
            ApplyClearHistorySegmentState(generalOption, !includePinned);
            ApplyClearHistorySegmentState(allOption, includePinned);
        }

        generalOption.MouseLeftButtonDown += (_, _) =>
        {
            includePinned = false;
            Refresh();
        };
        allOption.MouseLeftButtonDown += (_, _) =>
        {
            includePinned = true;
            Refresh();
        };

        var selector = new Grid
        {
            Width = clearControlWidth,
            Height = 30,
            Background = WpfBrushes.Transparent,
            ClipToBounds = true,
        };
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selector.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selector.Children.Add(generalOption);
        Grid.SetColumn(allOption, 1);
        selector.Children.Add(allOption);

        var selectorShell = new Border
        {
            Width = clearControlWidth,
            Height = 30,
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = selector,
        };

        var clear = SecondaryButton("Clear");
        clear.Width = clearControlWidth;
        clear.Height = 28;
        clear.Margin = new Thickness(0, 6, 0, 0);
        clear.Click += (_, _) => ConfirmClearHistory(includePinned);

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 6),
        };
        actions.Children.Add(selectorShell);
        actions.Children.Add(clear);

        return ControlRow("Clear history", "General keeps pinned items. All removes everything.", actions, minHeight: 78);
    }

    private Border DataFolderRow()
    {
        var open = SecondaryButton("Open");
        open.Width = 72;
        open.Click += (_, _) => _openDataFolder();

        var change = SecondaryButton("Change");
        change.Width = 86;
        change.Margin = new Thickness(8, 0, 0, 0);
        change.Click += (_, _) => PickClipboardFolder();

        var reset = SecondaryButton("Reset");
        reset.Width = 72;
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.Click += (_, _) =>
        {
            _resetClipboardFolder();
            ShowPage("History");
        };

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        actions.Children.Add(open);
        actions.Children.Add(change);
        actions.Children.Add(reset);

        return ActionOverDetailRow(
            "Clipboard folder",
            _settings.EffectiveClipboardFolderPath(),
            actions,
            minHeight: 76);
    }

    private Border AboutActionsRow()
    {
        var check = SecondaryButton("Check");
        check.Width = 74;
        check.Click += (_, _) =>
        {
            _updateStatus = new ClipUpdateStatus("Checking", "Checking for updates...", ClipUpdateService.CurrentVersion);
            ShowPage("About");
            _checkForUpdates(status =>
            {
                _updateStatus = status;
                ShowPage("About");
            });
        };

        var data = SecondaryButton("Data");
        data.Width = 72;
        data.Margin = new Thickness(8, 0, 0, 0);
        data.Click += (_, _) => _openDataFolder();

        var log = SecondaryButton("Log");
        log.Width = 64;
        log.Margin = new Thickness(8, 0, 0, 0);
        log.Click += (_, _) => _openDebugLog();

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        if (_updateStatus.State == "Update available" && !string.IsNullOrWhiteSpace(_updateStatus.DownloadUrl))
        {
            var update = SecondaryButton($"Update to {_updateStatus.LatestVersion}");
            update.MinWidth = 140;
            update.Margin = new Thickness(0, 0, 8, 0);
            var capturedStatus = _updateStatus;
            update.Click += (_, _) =>
            {
                update.IsEnabled = false;
                update.Content = "Installing...";
                _ = _installUpdate(capturedStatus);
            };
            actions.Children.Add(update);
        }

        actions.Children.Add(check);
        actions.Children.Add(data);
        actions.Children.Add(log);

        return ActionOverDetailRow("Tools", "Check updates, open data, or save/open the debug log.", actions, minHeight: 64);
    }

    private void PickClipboardFolder()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose where Clip saves clipboard content.",
            SelectedPath = _settings.EffectiveClipboardFolderPath(),
            ShowNewFolderButton = true,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var clipboardFolder = string.Equals(Path.GetFileName(dialog.SelectedPath), "Clipboard History", StringComparison.OrdinalIgnoreCase)
            ? dialog.SelectedPath
            : Path.Combine(dialog.SelectedPath, "Clipboard History");
        _changeClipboardFolder(clipboardFolder);
        ShowPage("History");
    }

    private void ConfirmClearHistory(bool includePinned)
    {
        var message = includePinned
            ? "Clear all saved clipboard history, including pinned items and their saved files?"
            : "Clear general clipboard history and saved files while keeping pinned items?";
        var confirm = System.Windows.MessageBox.Show(
            this,
            message,
            "Clear history",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _clearHistory(includePinned);
        ShowPage("History");
    }

    private Border ClearHistorySegment(string text, bool selected)
    {
        var segment = new Border
        {
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ApplyClearHistorySegmentState(segment, selected);
        return segment;
    }

    private void ApplyClearHistorySegmentState(Border segment, bool selected)
    {
        segment.Background = selected ? _surface : WpfBrushes.Transparent;
        if (segment.Child is TextBlock label)
        {
            label.Foreground = selected ? _accent : _muted;
        }
    }

    private Border DefaultPasteFormatRow()
    {
        return ControlRow(
            "Default paste format",
            "Choose how saved text is pasted.",
            StyledDropdown(PasteFormatLabel(_settings.DefaultPasteFormat), new[] { "Plain text", "Original formatting" }, selected =>
            {
                var preference = string.Equals(selected, "Original formatting", StringComparison.OrdinalIgnoreCase)
                    ? PasteFormatPreference.OriginalFormatting
                    : PasteFormatPreference.PlainText;
                if (preference == _settings.DefaultPasteFormat)
                {
                    return;
                }

                _settings.DefaultPasteFormat = preference;
                _applyDefaultPasteFormat(preference);
            }));
    }

    private Border ResetAllSettingsRow()
    {
        var button = SecondaryButton("Reset");
        button.Click += (_, _) =>
        {
            var confirm = System.Windows.MessageBox.Show(
                this,
                "Reset all settings to their defaults? This restores startup, updates, appearance, paste format, history limit, max item size, clipboard folder, hotkeys, and privacy exclusions.",
                "Reset settings",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            _resetAllSettings();
            ShowPage("General");
        };

        return ControlRow("Reset all settings", "Restore defaults for every settings section.", button);
    }

    private Border ResetDefaultsFooter()
    {
        var button = SecondaryButton("Reset to defaults");
        button.Width = 128;
        button.Height = 28;
        button.Click += (_, _) =>
        {
            var confirm = System.Windows.MessageBox.Show(
                this,
                "Reset all settings to their defaults? This restores startup, updates, appearance, paste format, history limit, max item size, clipboard folder, hotkeys, and privacy exclusions.",
                "Reset settings",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            _resetAllSettings();
            ShowPage("General");
        };

        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "Changes save automatically.",
            Foreground = _muted,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return new Border { Child = grid };
    }

    private static string PasteFormatLabel(PasteFormatPreference preference)
    {
        return preference switch
        {
            PasteFormatPreference.OriginalFormatting => "Original formatting",
            _ => "Plain text",
        };
    }

    private IEnumerable<Border> HotkeyRows()
    {
        yield return HotkeyRow("Open Clip", "Bring up the app.", _settings.Hotkeys.OpenClip, true, value => ApplyHotkeyChange(openClip: value));
        yield return HotkeyRow("Paste selected", "Paste selected item.", _settings.Hotkeys.PasteSelected, false, value => ApplyHotkeyChange(pasteSelected: value));
        yield return HotkeyRow("Copy selected", "Copy selected item.", _settings.Hotkeys.CopySelected, false, value => ApplyHotkeyChange(copySelected: value));
        yield return HotkeyRow("Pin selected", "Pin or unpin selected item.", _settings.Hotkeys.PinSelected, false, value => ApplyHotkeyChange(pinSelected: value));
        yield return HotkeyRow("Open actions", "Open the item action menu.", _settings.Hotkeys.OpenActions, false, value => ApplyHotkeyChange(openActions: value));
        yield return HotkeyRow("Open selected", "Open selected link, file, or image.", _settings.Hotkeys.OpenSelected, false, value => ApplyHotkeyChange(openSelected: value));
        yield return HotkeyRow("Edit selected", "Edit selected text.", _settings.Hotkeys.EditSelected, false, value => ApplyHotkeyChange(editSelected: value));
        yield return HotkeyRow("Save debug log", "Save a log snapshot.", _settings.Hotkeys.SaveDebugLog, true, value => ApplyHotkeyChange(saveDebugLog: value));
        yield return HotkeyRow("Delete selected", "Delete selected item.", _settings.Hotkeys.DeleteSelected, false, value => ApplyHotkeyChange(deleteSelected: value));
        yield return HotkeyRow("Close", "Close Clip or escape previews.", _settings.Hotkeys.CloseClip, false, value => ApplyHotkeyChange(closeClip: value));
    }

    private Border HotkeyRow(string label, string hint, string current, bool requireModifier, Action<string> apply)
    {
        return ControlRow(label, hint, HotkeyInput(current, requireModifier, apply));
    }

    private WpfTextBox HotkeyInput(string current, bool requireModifier, Action<string> apply)
    {
        var input = new WpfTextBox
        {
            Text = current,
            Width = 170,
            Height = 30,
            Padding = new Thickness(10, 5, 10, 0),
            Background = _surface2,
            Foreground = _text,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CaretBrush = _text,
            IsReadOnly = true,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        input.GotKeyboardFocus += (_, _) => input.Text = "Type shortcut";
        input.LostKeyboardFocus += (_, _) =>
        {
            if (input.Text == "Type shortcut")
            {
                input.Text = current;
            }
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (!TryCreateGestureFromKeyEvent(e, requireModifier, out var gesture))
            {
                input.Text = requireModifier ? "Use Ctrl, Alt, Shift, or Win" : "Invalid shortcut";
                e.Handled = true;
                return;
            }

            input.Text = gesture.DisplayText;
            apply(gesture.DisplayText);
            Keyboard.ClearFocus();
            e.Handled = true;
        };

        return input;
    }

    private static bool TryCreateGestureFromKeyEvent(System.Windows.Input.KeyEventArgs e, bool requireModifier, out ClipHotkeyGesture gesture)
    {
        gesture = default;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return false;
        }

        var modifiers = Keyboard.Modifiers;
        var text = ClipHotkeyGesture.Format(modifiers, key);
        return requireModifier ? ClipHotkeyGesture.TryParseGlobal(text, out gesture) : ClipHotkeyGesture.TryParse(text, out gesture);
    }

    private Border ResetHotkeysRow()
    {
        var button = SecondaryButton("Reset");
        button.Click += (_, _) =>
        {
            var hotkeys = new ClipHotkeySettings();
            hotkeys.ResetToDefaults();
            _applyHotkeys(hotkeys);
            ShowPage("Shortcuts");
        };

        return ControlRow("Reset hotkeys", "Restore the default shortcuts.", button);
    }

    private void ApplyHotkeyChange(string? openClip = null, string? pasteSelected = null, string? copySelected = null, string? pinSelected = null, string? openActions = null, string? openSelected = null, string? editSelected = null, string? saveDebugLog = null, string? deleteSelected = null, string? closeClip = null)
    {
        var hotkeys = new ClipHotkeySettings
        {
            OpenClip = openClip ?? _settings.Hotkeys.OpenClip,
            PasteSelected = pasteSelected ?? _settings.Hotkeys.PasteSelected,
            CopySelected = copySelected ?? _settings.Hotkeys.CopySelected,
            PinSelected = pinSelected ?? _settings.Hotkeys.PinSelected,
            OpenActions = openActions ?? _settings.Hotkeys.OpenActions,
            OpenSelected = openSelected ?? _settings.Hotkeys.OpenSelected,
            EditSelected = editSelected ?? _settings.Hotkeys.EditSelected,
            SaveDebugLog = saveDebugLog ?? _settings.Hotkeys.SaveDebugLog,
            DeleteSelected = deleteSelected ?? _settings.Hotkeys.DeleteSelected,
            CloseClip = closeClip ?? _settings.Hotkeys.CloseClip,
        };
        _applyHotkeys(hotkeys);
        _settings.Hotkeys = hotkeys;
    }

    private WpfButton StartupToggle(bool enabled)
    {
        return ToggleButton(enabled, next => _applyRunAtStartup(next));
    }

    private WpfButton ToggleButton(bool enabled, Action<bool> apply)
    {
        var trackOff = _line2;
        var trackBorderOff = _line2;
        var knob = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = WpfBrushes.White,
            HorizontalAlignment = enabled ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3),
        };
        var track = new Border
        {
            Width = 42,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = enabled ? _accent : trackOff,
            BorderBrush = enabled ? _accent : trackBorderOff,
            BorderThickness = new Thickness(1),
            Child = knob,
        };
        var toggle = new WpfButton
        {
            Width = 46,
            Height = 34,
            Padding = new Thickness(0),
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = track,
            Tag = enabled,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        toggle.Click += (_, _) =>
        {
            var next = toggle.Tag is not true;
            toggle.Tag = next;
            knob.HorizontalAlignment = next ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
            track.Background = next ? _accent : trackOff;
            track.BorderBrush = next ? _accent : trackBorderOff;
            apply(next);
        };

        return toggle;
    }

    private Border ThemeRow()
    {
        return ControlRow(
            "Theme",
            "Choose System, Light, or Dark.",
            ThemeToggleDropdown(),
            minHeight: 66);
    }

    private Border AppIconRow()
    {
        return ControlRow(
            "App icon",
            "Choose Light or Dark.",
            AppIconPicker());
    }

    private FrameworkElement ThemeToggleDropdown()
    {
        var host = new Grid { Width = 74, Height = 30 };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        var toggle = AnimatedThemeToggle(_settings.Theme);
        var themeIcon = (ThemeMorphIcon)toggle.Tag;
        Grid.SetColumn(toggle, 0);
        host.Children.Add(toggle);

        var arrow = new WpfButton
        {
            Width = 28,
            Height = 30,
            Padding = new Thickness(0),
            Background = _surface2,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Content = new WpfImage { Source = _dropdownIcon, Width = 11, Height = 11 },
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
        };
        arrow.MouseEnter += (_, _) => arrow.Background = _accentSoft;
        arrow.MouseLeave += (_, _) => arrow.Background = _surface2;
        Grid.SetColumn(arrow, 1);
        host.Children.Add(arrow);

        var optionHost = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = host,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = _surface,
                BorderBrush = _line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = 112,
                Child = optionHost,
            },
        };

        foreach (var item in new[] { "System", "Light", "Dark" })
        {
            optionHost.Children.Add(ThemeOptionRow(item, selected =>
            {
                ApplyThemeThroughToggle(selected, themeIcon);
                var closeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                closeTimer.Tick += (_, _) =>
                {
                    closeTimer.Stop();
                    popup.IsOpen = false;
                };
                closeTimer.Start();
            }));
        }

        arrow.Click += (_, _) => popup.IsOpen = true;
        return host;
    }

    private WpfButton AnimatedThemeToggle(ClipThemePreference current)
    {
        var dark = current switch
        {
            ClipThemePreference.Dark => true,
            ClipThemePreference.Light => false,
            _ => MainWindow.IsWindowsDarkMode(),
        };

        var icon = new ThemeMorphIcon(_text, dark ? 1 : 0)
        {
            Width = 26,
            Height = 26,
        };

        var button = new WpfButton
        {
            Width = 42,
            Height = 30,
            Padding = new Thickness(7, 1, 7, 1),
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
            Content = icon,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
            Tag = icon,
        };
        button.MouseEnter += (_, _) =>
        {
            button.BorderBrush = _line2;
            button.Background = WpfBrushes.Transparent;
            button.Opacity = 1;
        };
        button.MouseLeave += (_, _) =>
        {
            button.BorderBrush = WpfBrushes.Transparent;
            button.Background = WpfBrushes.Transparent;
            button.Opacity = 1;
        };
        button.PreviewMouseLeftButtonDown += (_, _) => button.Opacity = 0.72;
        button.PreviewMouseLeftButtonUp += (_, _) => button.Opacity = 1;
        button.Click += (_, _) =>
        {
            var next = MainWindow.NextThemeTogglePreference(PendingTheme ?? _settings.Theme, MainWindow.IsWindowsDarkMode());
            AnimateAndApplyTheme(next, icon);
        };
        return button;
    }

    private ClipThemePreference? PendingTheme { get; set; }

    private void ApplyThemeThroughToggle(ClipThemePreference theme, ThemeMorphIcon icon)
    {
        if (theme == _settings.Theme && PendingTheme is null)
        {
            return;
        }

        AnimateAndApplyTheme(theme, icon);
    }

    private void AnimateAndApplyTheme(ClipThemePreference theme, ThemeMorphIcon icon)
    {
        PendingTheme = theme;
        _themeIcon = icon;
        var dark = theme switch
        {
            ClipThemePreference.Dark => true,
            ClipThemePreference.Light => false,
            _ => MainWindow.IsWindowsDarkMode(),
        };
        icon.AnimateTo(
            dark,
            midway: () => { },
            completed: () => { });

        _themeApplyTimer.Stop();
        _themeApplyTimer.Start();
    }

    private void ApplyPendingTheme()
    {
        _themeApplyTimer.Stop();
        if (PendingTheme is not { } theme)
        {
            return;
        }

        ApplyThemeSelection(theme, refreshImmediately: false);
        _paletteOverride = null;
        RefreshTheme(rebuildPage: false);
        RefreshVisibleSettingsContentTheme(_content);
        _refreshClipboardManagerTextTheme();
        _themeIcon?.SetInk(_text);
        PendingTheme = null;
        ShellLog.Info($"settings and main theme applied theme={theme}");
    }

    private sealed class ThemeMorphIcon : Grid
    {
        private SolidColorBrush _ink;
        private readonly Grid _sun = new();
        private readonly Grid _moon = new();
        private readonly ScaleTransform _sunScale = new();
        private readonly ScaleTransform _moonScale = new();
        private readonly RotateTransform _sunRotate = new();
        private readonly RotateTransform _moonRotate = new();

        public ThemeMorphIcon(WpfBrush ink, double progress)
        {
            _ink = DetachedBrush(ink);
            ClipToBounds = false;
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
            BuildIcon(progress >= 0.5);
        }

        public void SetInk(WpfBrush ink)
        {
            _ink = DetachedBrush(ink);
            ApplyInk(_sun);
            ApplyInk(_moon);
        }

        public void AnimateTo(bool dark, Action midway, Action completed)
        {
            midway();
            var duration = TimeSpan.FromMilliseconds(320);
            Animate(_sun, OpacityProperty, dark ? 0 : 1, duration);
            Animate(_moon, OpacityProperty, dark ? 1 : 0, duration, completed);
            Animate(_sunScale, ScaleTransform.ScaleXProperty, dark ? 0.86 : 1, duration);
            Animate(_sunScale, ScaleTransform.ScaleYProperty, dark ? 0.86 : 1, duration);
            Animate(_moonScale, ScaleTransform.ScaleXProperty, dark ? 1 : 0.86, duration);
            Animate(_moonScale, ScaleTransform.ScaleYProperty, dark ? 1 : 0.86, duration);
            Animate(_sunRotate, RotateTransform.AngleProperty, dark ? 42 : 0, duration);
            Animate(_moonRotate, RotateTransform.AngleProperty, dark ? 0 : -42, duration);
        }

        private void BuildIcon(bool dark)
        {
            Children.Clear();
            BuildSun();
            BuildMoon();
            Children.Add(_sun);
            Children.Add(_moon);
            SetInitialState(dark);
        }

        private void BuildSun()
        {
            _sun.Width = 24;
            _sun.Height = 24;
            _sun.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            _sun.VerticalAlignment = VerticalAlignment.Center;
            _sun.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            _sun.RenderTransform = new TransformGroup { Children = { _sunScale, _sunRotate } };
            _sun.Children.Add(new WpfEllipse
            {
                Width = 9.5,
                Height = 9.5,
                Fill = _ink,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _sun.Children.Add(new WpfPath
            {
                Data = Geometry.Parse("M12,1 L12,3 M12,21 L12,23 M1,12 L3,12 M21,12 L23,12 M4.2,4.2 L5.7,5.7 M18.3,5.7 L19.8,4.2 M4.2,19.8 L5.7,18.3 M18.3,18.3 L19.8,19.8"),
                Stroke = _ink,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
            });
        }

        private void BuildMoon()
        {
            _moon.Width = 24;
            _moon.Height = 24;
            _moon.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            _moon.VerticalAlignment = VerticalAlignment.Center;
            _moon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            _moon.RenderTransform = new TransformGroup { Children = { _moonScale, _moonRotate } };
            _moon.Children.Add(new WpfPath
            {
                Data = Geometry.Parse("M21,12.8 A9,9 0 1 1 11.2,3 A7,7 0 1 0 21,12.8 Z"),
                Fill = _ink,
                Stretch = Stretch.None,
            });
        }

        private void SetInitialState(bool dark)
        {
            _sun.Opacity = dark ? 0 : 1;
            _moon.Opacity = dark ? 1 : 0;
            _sunScale.ScaleX = _sunScale.ScaleY = dark ? 0.86 : 1;
            _moonScale.ScaleX = _moonScale.ScaleY = dark ? 1 : 0.86;
            _sunRotate.Angle = dark ? 42 : 0;
            _moonRotate.Angle = dark ? 0 : -42;
        }

        private void ApplyInk(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is WpfShape shape)
                {
                    shape.Fill = shape.Fill is not null ? _ink : shape.Fill;
                    shape.Stroke = shape.Stroke is not null ? _ink : shape.Stroke;
                }

                ApplyInk(child);
            }
        }

        private static void Animate(DependencyObject target, DependencyProperty property, double to, TimeSpan duration, Action? completed = null)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(duration),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            if (completed is not null)
            {
                animation.Completed += (_, _) => completed();
            }

            if (target is UIElement element)
            {
                element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            }
            else if (target is Animatable animatable)
            {
                animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            }
        }

        private static SolidColorBrush DetachedBrush(WpfBrush brush)
        {
            if (brush is SolidColorBrush solid)
            {
                var detached = new SolidColorBrush(solid.Color);
                detached.Freeze();
                return detached;
            }

            var fallback = new SolidColorBrush(Colors.White);
            fallback.Freeze();
            return fallback;
        }
    }

    private void RefreshVisibleSettingsContentTheme(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case TextBlock text:
                    text.Foreground = IsPrimarySettingsText(text) ? _text : _muted;
                    break;
                case WpfButton button:
                    button.Foreground = _text;
                    if (button.Background != WpfBrushes.Transparent)
                    {
                        button.Background = _surface2;
                    }
                    if (button.BorderBrush != WpfBrushes.Transparent)
                    {
                        button.BorderBrush = _line;
                    }
                    break;
                case Border border:
                    if (border.BorderThickness != new Thickness(0))
                    {
                        border.BorderBrush = _line;
                    }
                    break;
            }

            RefreshVisibleSettingsContentTheme(child);
        }
    }

    private static bool IsPrimarySettingsText(TextBlock text)
    {
        return text.FontWeight == FontWeights.SemiBold ||
            text.FontWeight == FontWeights.Bold ||
            text.FontSize >= 15 ||
            string.Equals(text.Text, "Theme", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text.Text, "App icon", StringComparison.OrdinalIgnoreCase);
    }

    private Border ThemeOptionRow(string item, Action<ClipThemePreference> onSelected)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7, 10, 7),
            Background = WpfBrushes.Transparent,
            BorderBrush = WpfBrushes.Transparent,
            BorderThickness = new Thickness(1),
        };
        row.Child = new TextBlock
        {
            Text = item,
            Foreground = string.Equals(item, _settings.Theme.ToString(), StringComparison.OrdinalIgnoreCase) ? _accent : _muted,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = _accentSoft;
            row.BorderBrush = _selectedBorder;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = WpfBrushes.Transparent;
            row.BorderBrush = WpfBrushes.Transparent;
        };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (Enum.TryParse<ClipThemePreference>(item, out var theme))
            {
                onSelected(theme);
            }

            e.Handled = true;
        };
        return row;
    }

    private void ApplyThemeSelection(ClipThemePreference theme, bool refreshImmediately = true)
    {
        if (theme == _settings.Theme)
        {
            return;
        }

        _applyTheme(theme);
        if (refreshImmediately)
        {
            RefreshTheme(rebuildPage: false);
        }

        ShellLog.Info($"settings theme changed theme={theme}");
    }

    private FrameworkElement AppIconPicker()
    {
        var host = new Grid { Width = 74, Height = 30 };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var light = AppIconButton(AppIconPreference.Light);
        var dark = AppIconButton(AppIconPreference.Dark);
        Grid.SetColumn(light, 0);
        Grid.SetColumn(dark, 2);
        host.Children.Add(light);
        host.Children.Add(dark);
        return host;
    }

    private WpfButton AppIconButton(AppIconPreference preference)
    {
        var active = preference == _settings.AppIcon;
        var button = new WpfButton
        {
            Width = 32,
            Height = 30,
            Padding = new Thickness(3),
            Background = WpfBrushes.Transparent,
            BorderBrush = active ? _selectedBorder : WpfBrushes.Transparent,
            BorderThickness = new Thickness(active ? 1 : 0),
            Content = new WpfImage
            {
                Source = LoadAppIconImage(preference),
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
            },
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
            ToolTip = $"{preference} app icon",
        };
        RenderOptions.SetBitmapScalingMode(button, BitmapScalingMode.HighQuality);
        button.MouseEnter += (_, _) => button.BorderBrush = _selectedBorder;
        button.MouseLeave += (_, _) => button.BorderBrush = active ? _selectedBorder : WpfBrushes.Transparent;
        button.Click += (_, _) =>
        {
            if (preference == _settings.AppIcon)
            {
                return;
            }

            _applyAppIcon(preference);
            ShellLog.Info($"settings app icon changed icon={preference}");
            ShowPage(_currentPage);
        };
        return button;
    }

    private static ImageSource LoadAppIconImage(AppIconPreference preference)
    {
        var fileName = preference == AppIconPreference.Dark ? "clip-tile-dark.svg" : "clip-tile-light.svg";
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "app-icons", fileName);
        if (!File.Exists(path))
        {
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "app-icons", fileName));
        }

        var document = SvgDocument.FromSvg<SvgDocument>(File.ReadAllText(path));
        using var bitmap = new System.Drawing.Bitmap(64, 64);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        using var rendered = document.Draw(64, 64);
        graphics.DrawImage(rendered, 0, 0, 64, 64);
        return MainWindow.BitmapFromDrawingImage(bitmap);
    }

    private WpfButton StyledDropdown(string selected, IReadOnlyList<string> items, Action<string> onSelected)
    {
        var label = new TextBlock
        {
            Text = selected,
            Foreground = _text,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(label);
        var arrow = new WpfImage
        {
            Source = _dropdownIcon,
            Width = 11,
            Height = 11,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(arrow, 1);
        content.Children.Add(arrow);

        var button = new WpfButton
        {
            Width = 170,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = _surface2,
            Foreground = _text,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Content = content,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
        };
        button.MouseEnter += (_, _) => button.Background = _accentSoft;
        button.MouseLeave += (_, _) => button.Background = _surface2;

        var optionHost = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = _surface,
                BorderBrush = _line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = 170,
                Child = optionHost,
            },
        };

        foreach (var item in items)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Background = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(1),
            };
            row.Child = new TextBlock
            {
                Text = item,
                Foreground = string.Equals(item, selected, StringComparison.OrdinalIgnoreCase) ? _accent : _muted,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
            };
            row.MouseEnter += (_, _) =>
            {
                row.Background = _accentSoft;
                row.BorderBrush = _selectedBorder;
            };
            row.MouseLeave += (_, _) =>
            {
                row.Background = WpfBrushes.Transparent;
                row.BorderBrush = WpfBrushes.Transparent;
            };
            row.MouseLeftButtonDown += (_, e) =>
            {
                popup.IsOpen = false;
                label.Text = item;
                onSelected(item);
                foreach (Border optionRow in optionHost.Children)
                {
                    var isSelected = optionRow.Child is TextBlock text && string.Equals(text.Text, item, StringComparison.OrdinalIgnoreCase);
                    optionRow.Background = WpfBrushes.Transparent;
                    if (optionRow.Child is TextBlock optionText)
                    {
                        optionText.Foreground = isSelected ? _accent : _muted;
                    }
                }

                e.Handled = true;
            };
            optionHost.Children.Add(row);
        }

        button.Click += (_, _) => popup.IsOpen = true;
        return button;
    }

    private WpfButton SecondaryButton(string text)
    {
        var button = new WpfButton
        {
            Content = text,
            Width = 88,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Background = _surface2,
            Foreground = _text,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = SubtleSettingsButtonTemplate(),
            FocusVisualStyle = null,
        };
        button.MouseEnter += (_, _) => button.Background = _accentSoft;
        button.MouseLeave += (_, _) => button.Background = _surface2;
        return button;
    }

    private Border ControlRow(string label, string hint, FrameworkElement control, double minHeight = 58)
    {
        var grid = new Grid { MinHeight = minHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = _text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = hint,
            Foreground = _muted,
            FontSize = 12,
            LineHeight = 16,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        grid.Children.Add(textPanel);
        Grid.SetColumn(control, 2);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Border ActionOverDetailRow(string label, string detail, FrameworkElement actions, double minHeight)
    {
        var grid = new Grid { MinHeight = minHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = _text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 16, 0),
        });

        Grid.SetColumn(actions, 1);
        actions.Margin = new Thickness(0, 9, 0, 0);
        actions.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(actions);

        var detailText = new TextBlock
        {
            Text = detail,
            Foreground = _muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 16, 12),
        };
        Grid.SetRow(detailText, 1);
        Grid.SetColumnSpan(detailText, 2);
        grid.Children.Add(detailText);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Border Row(string label, string value)
    {
        var grid = new Grid { MinHeight = 46 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = _muted,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var valueBox = new WpfTextBox
        {
            Text = value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = WpfBrushes.Transparent,
            Foreground = _text,
            FontSize = 13,
            TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueBox, 1);
        grid.Children.Add(valueBox);

        return new Border
        {
            Child = grid,
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }
}

internal sealed class MenuAction
{
    public static readonly MenuAction Separator = new("", static () => { }, isSeparator: true);

    public MenuAction(string label, Action invoke, bool enabled = true, bool danger = false, bool isSeparator = false, string shortcut = "", IReadOnlyList<MenuAction>? children = null)
    {
        Label = label;
        Invoke = invoke;
        Enabled = enabled;
        Danger = danger;
        IsSeparator = isSeparator;
        Shortcut = shortcut;
        Children = children ?? [];
    }

    public static MenuAction Submenu(string label, IReadOnlyList<MenuAction> children) => new(label, static () => { }, children: children);

    public string Label { get; }
    public Action Invoke { get; }
    public bool Enabled { get; }
    public bool Danger { get; }
    public bool IsSeparator { get; }
    public string Shortcut { get; }
    public IReadOnlyList<MenuAction> Children { get; }
}

internal static class ShellLog
{
    private static readonly object Gate = new();
    private static readonly string LogRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip");
    public static readonly string Path = System.IO.Path.Combine(LogRoot, "shell.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception exception, string message) => Write("ERROR", $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(LogRoot);
            File.AppendAllText(Path, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        }
    }
}

internal sealed class RenameWindow : Window
{
    private readonly WpfTextBox _box = new();
    public string Value => _box.Text;

    public RenameWindow(string value, WpfBrush background, WpfBrush foreground, WpfBrush muted, WpfBrush line, WpfBrush surface, WpfBrush accentSoft, WpfBrush selected, WpfBrush selectedBorder)
    {
        Title = "Rename";
        Width = 420;
        Height = 190;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = background;
        Foreground = foreground;
        ShowInTaskbar = false;
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);

        _box.Text = value;
        _box.FocusVisualStyle = null;
        _box.Margin = new Thickness(0);
        _box.Padding = new Thickness(12, 8, 12, 8);
        _box.Background = WpfBrushes.Transparent;
        _box.Foreground = foreground;
        _box.BorderThickness = new Thickness(0);
        _box.FontSize = 13;
        _box.SelectionBrush = accentSoft;
        _box.MaxLength = 120;
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };

        var fieldBackground = surface;
        var primaryBackground = accentSoft;
        var primaryBorder = selectedBorder;
        var hoverBackground = selected;

        var grid = new Grid { Background = background };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Rename",
            Foreground = foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(18, 16, 18, 2),
        };
        grid.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "Leave blank to use the original title.",
            Foreground = muted,
            FontSize = 12,
            Margin = new Thickness(18, 0, 18, 12),
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        var body = new StackPanel { Margin = new Thickness(18, 0, 18, 18) };
        var boxShell = new Border
        {
            Background = fieldBackground,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = _box,
        };
        body.Children.Add(boxShell);

        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = ModalButton("Cancel", foreground, line, fieldBackground, primaryBackground, primaryBorder, hoverBackground, false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = ModalButton("Save", foreground, line, fieldBackground, primaryBackground, primaryBorder, hoverBackground, true);
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        body.Children.Add(buttons);

        Grid.SetRow(body, 2);
        grid.Children.Add(body);
        Content = grid;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    private static bool IsLightBrush(WpfBrush brush) => MainWindow.IsLightBackground(brush);

    private static WpfButton ModalButton(string text, WpfBrush foreground, WpfBrush line, WpfBrush fieldBackground, WpfBrush primaryBackground, WpfBrush primaryBorder, WpfBrush hoverBackground, bool primary)
    {
        var idleBackground = primary ? primaryBackground : fieldBackground;
        var idleBorder = primary ? primaryBorder : line;
        var hoverBg = primary ? hoverBackground : primaryBackground;
        var button = new WpfButton
        {
            Content = text,
            Height = 32,
            MinWidth = primary ? 74 : 68,
            Padding = new Thickness(14, 0, 14, 0),
            Background = idleBackground,
            BorderBrush = idleBorder,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };
        button.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="{x:Type Button}" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Border x:Name="Root" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Root" Property="Opacity" Value="0.85"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        button.MouseEnter += (_, _) => { button.Background = hoverBg; button.BorderBrush = primaryBorder; };
        button.MouseLeave += (_, _) => { button.Background = idleBackground; button.BorderBrush = idleBorder; };
        return button;
    }
}

internal sealed class TextEditWindow : Window
{
    private readonly System.Windows.Controls.TextBox _box = new();
    public string Value => _box.Text;

    public TextEditWindow(string value, System.Windows.Media.Brush background, System.Windows.Media.Brush foreground, System.Windows.Media.Brush line, System.Windows.Media.Brush surface, System.Windows.Media.Brush textCursor, System.Windows.Media.Brush accentSoft, System.Windows.Media.Brush selected, System.Windows.Media.Brush selectedBorder)
    {
        Title = "Edit Text";
        Width = 640;
        Height = 420;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = background;
        Foreground = foreground;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);

        var editorBackground = surface;
        var primaryBackground = accentSoft;
        var primaryBorder = selectedBorder;
        var secondaryBackground = surface;
        var selectionBrush = accentSoft;
        var hoverBackground = selected;

        _box.Text = value;
        _box.TextWrapping = TextWrapping.Wrap;
        _box.AcceptsReturn = true;
        _box.FocusVisualStyle = null;
        _box.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(_box, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_box, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(_box, TextHintingMode.Fixed);
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _box.Margin = new Thickness(0);
        _box.Padding = new Thickness(14);
        _box.Background = WpfBrushes.Transparent;
        _box.Foreground = foreground;
        _box.BorderThickness = new Thickness(0);
        _box.FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas");
        _box.FontSize = 13;
        _box.CaretBrush = textCursor;
        _box.SelectionBrush = selectionBrush;

        var grid = new Grid { Background = background };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(18, 14, 18, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Edit Text",
            Foreground = foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var trim = ModalButton("Trim", foreground, line, secondaryBackground, primaryBackground, primaryBorder, hoverBackground, false);
        trim.Margin = new Thickness(0, 0, 8, 0);
        trim.Click += (_, _) => _box.Text = _box.Text.Trim();
        Grid.SetColumn(trim, 1);
        header.Children.Add(trim);
        grid.Children.Add(header);

        var editorShell = new Border
        {
            Background = editorBackground,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(18, 0, 18, 0),
            Child = _box,
        };
        Grid.SetRow(editorShell, 1);
        grid.Children.Add(editorShell);

        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(18, 14, 18, 18) };
        var cancel = ModalButton("Cancel", foreground, line, secondaryBackground, primaryBackground, primaryBorder, hoverBackground, false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = ModalButton("Save", foreground, line, secondaryBackground, primaryBackground, primaryBorder, hoverBackground, true);
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
    }

    private static WpfButton ModalButton(string text, WpfBrush foreground, WpfBrush line, WpfBrush secondaryBackground, WpfBrush primaryBackground, WpfBrush primaryBorder, WpfBrush hoverBackground, bool primary)
    {
        var idleBackground = primary ? primaryBackground : secondaryBackground;
        var idleBorder = primary ? primaryBorder : line;
        var hoverBg = primary ? hoverBackground : primaryBackground;
        var button = new WpfButton
        {
            Content = text,
            Height = 32,
            MinWidth = primary ? 74 : 68,
            Padding = new Thickness(14, 0, 14, 0),
            Background = idleBackground,
            BorderBrush = idleBorder,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };
        button.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="{x:Type Button}" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Border x:Name="Root" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.85"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        button.MouseEnter += (_, _) =>
        {
            button.Background = hoverBg;
            button.BorderBrush = primaryBorder;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = idleBackground;
            button.BorderBrush = idleBorder;
        };
        return button;
    }
}
