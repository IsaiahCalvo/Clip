using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Microsoft.Web.WebView2.Core;
using Svg;
using DrawingImage = System.Drawing.Image;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfImage = System.Windows.Controls.Image;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfListBoxItem = System.Windows.Controls.ListBoxItem;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPen = System.Windows.Media.Pen;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfPath = System.Windows.Shapes.Path;
using WpfShape = System.Windows.Shapes.Shape;
using WatcherAppChoice = Clip.Core.AppChoice;
using WatcherAppDiscovery = Clip.Core.OpenWithAppDiscovery;
using WatcherAppLauncher = Clip.Core.OpenWithAppLauncher;
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


public partial class MainWindow : Window
{
    private const int OpenHotkeyId = 0x4350;
    private const int DebugLogHotkeyId = 0x4351;
    private const int OpenOverrideHotkeyId = 0x4352;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const int WmHotkey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int DwmwaWindowCornerPreference = 33;
    private const int RowIconDecodePixels = 48;
    private const int PreviewImageDecodePixels = 900;
    private const int MaxCachedRasterImages = 256;
    private const int TextPreviewCharacterLimit = 80_000;
    private const int InitialRenderEntryBatch = 3;
    private const int DeferredRenderEntryBatch = 36;
    private const int InitialSummaryFirstPaintLimit = 8;
    private const int DebugOpenSurfaceMaxAttempts = 80;
    private const long SummaryPreloadMaximumBytes = 2L * 1024 * 1024;
    private static readonly TimeSpan WindowsHistoryImportMinimumInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WindowsHistoryImportAfterShowDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ClipboardDuplicateBurstWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PaletteSessionKeepAlive = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, ImageSource> SvgImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SvgTextCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SvgCacheGate = new();
    private static readonly Dictionary<string, ImageSource> RasterImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RasterImageCacheGate = new();
    private static readonly ConcurrentDictionary<string, Style> ThinScrollBarStyleCache = new(StringComparer.OrdinalIgnoreCase);
    private static System.Drawing.Rectangle _cachedMouseScreenWorkingArea;
    private static bool _hasCachedMouseScreenWorkingArea;

    private readonly ClipShellSettings _settings = ClipShellSettings.Load();
    private readonly ClipboardHistoryStore _store;
    private readonly PeriodicWorkThrottle _windowsHistoryImportThrottle = new(WindowsHistoryImportMinimumInterval);
    private readonly ClipboardCaptureBurstGate _clipboardCaptureBurstGate = new(ClipboardDuplicateBurstWindow);
    private readonly SemaphoreSlim _clipboardPersistGate = new(1, 1);
    private readonly object _clipboardPersistTasksGate = new();
    private readonly List<Task> _clipboardPersistTasks = [];
    private readonly ClipUpdateService _updates = new();
    private readonly Dictionary<string, Border> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly System.Windows.Threading.DispatcherTimer _hotkeyRetryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Threading.DispatcherTimer _outsideClickTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly System.Windows.Threading.DispatcherTimer _clipboardSettleTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly System.Windows.Threading.DispatcherTimer _startupUpdateCheckTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly System.Windows.Threading.DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromHours(4) };
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly System.Windows.Threading.DispatcherTimer _historyPreloadTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly System.Windows.Threading.DispatcherTimer _paletteSessionExitTimer = new() { Interval = PaletteSessionKeepAlive };
    private bool _settingsCachesWarmed;
    private IReadOnlyList<ClipboardHistoryItem> _allItems = [];
    private ClipboardHistoryItem? _selected;
    private ClipboardHistoryItem? _pendingTextClipboardItem;
    private uint _lastClipboardSequenceNumber;
    private HwndSource? _source;
    private bool _openHotkeyRegistered;
    private bool _debugLogHotkeyRegistered;
    private bool _openHotkeyUnavailable;
    private bool _debugLogHotkeyUnavailable;
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
    private bool _historySummariesPreloaded;
    private bool _historyPreloadInProgress;
    private bool _historyImportInProgress;
    private Task<(IReadOnlyList<ClipboardHistoryItem> Items, long QueryElapsedMs)>? _recentFirstPaintPreloadTask;
    private bool _paletteRequested;
    private bool _paletteOpen;
    private bool _paletteNoActivate;
    private bool _paletteSessionExitRequested;
    private bool _isClosing;
    private bool _chromeIconsReady;
    private bool _appHeaderIconReady;
    private int _renderGeneration;
    private int _loadGeneration;
    private System.Windows.Threading.DispatcherOperation? _backgroundFullRefreshOperation;
    private IReadOnlyList<(string? Header, ClipboardHistoryItem? Item)> _deferredRenderEntries = [];
    private int _deferredRenderIndex;
    private int _deferredRenderGeneration;
    private string _deferredRenderReason = string.Empty;
    private Stopwatch? _deferredRenderWatch;
    private string? _debugOpenSurface;
    private int _debugOpenSurfaceAttempts;
    private IntPtr _returnFocusHwnd;
    private IntPtr _returnFocusChildHwnd;
    private AutomationElement? _returnFocusElement;
    private string _returnFocusElementSummary = "none";
    private string? _returnFocusValueBefore;
    private bool _returnFocusCommitsPasteWithEnter;
    private bool _returnFocusCouldNeedNoActivate;
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
    private Border? _inlineModalOverlay;
    private Border? _settingsOverlay;
    private SettingsWindow? _hostedSettings;
    private bool _settingsOverlayKeepPaletteOnClose;
    private Border? _prewarmedSettingsOverlay;
    private SettingsWindow? _prewarmedSettings;
    private bool _prewarmedSettingsReady;
    private bool _settingsPrewarmQueued;
    private bool _windowsHistoryImportAfterShowQueued;
    private FrameworkElement? _htmlPreview;
    private Action<System.Drawing.Color>? _setHtmlPreviewBackground;
    private string? _currentPreviewImagePath;
    private string? _currentPreviewPdfPath;
    private ClipUpdateStatus _lastUpdateStatus = ClipUpdateStatus.NotChecked(ClipUpdateService.CurrentVersion);
    public bool KeepOpenForDebug { get; set; }
    public string? DebugInitialSearch { get; set; }
    public int? DebugAutoConcealMs { get; set; }
    public bool DebugOpenSettings { get; set; }
    public string? DebugOpenSurface { get; set; }
    public string? TrayStartupAction { get; set; }
    public bool PaletteSessionMode { get; set; }
    public bool PaletteSessionStartHidden { get; set; }
    public bool KeepWarmSession { get; set; }
    internal ClipUpdateStatus LastUpdateStatus => _lastUpdateStatus;
    internal AppIconPreference AppIconPreference => _settings.AppIcon;
    internal event Action<AppIconPreference>? AppIconChanged;
    internal event Action<string>? UserNotificationRequested;
    internal event Action<string>? UpdateNotification;

    public MainWindow()
    {
        _store = new ClipboardHistoryStore(contentRootPath: _settings.EffectiveClipboardFolderPath(), enableLoadMaintenance: false);
        InitializeComponent();
        RenderOptions.SetClearTypeHint(Shell, ClearTypeHint.Enabled);
        ApplyTheme(_settings.Theme, save: false);
        Opacity = 0;
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
        _startupUpdateCheckTimer.Tick += (_, _) =>
        {
            _startupUpdateCheckTimer.Stop();
            if (_settings.CheckForUpdatesOnStartup)
            {
                _ = CheckForUpdatesAsync(showToastWhenCurrent: false);
            }
        };
        _updateCheckTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(showToastWhenCurrent: false);
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            QueueLoadItems(selectFirst: false, reason: "search");
        };
        _historyPreloadTimer.Tick += (_, _) =>
        {
            _historyPreloadTimer.Stop();
            PreloadHistorySummariesIfCheap();
        };
        _paletteSessionExitTimer.Tick += (_, _) => ExitPaletteSessionIfIdle();
    }

    public void InitializeShell()
    {
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WndProc);
            var hwnd = new WindowInteropHelper(this).Handle;
            ApplyRoundedWindowCorners(hwnd);
            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
            var hotkey = false;
            var listener = false;
            if (!PaletteSessionMode)
            {
                hotkey = EnsureHotkeyRegistered("startup");
                listener = AddClipboardFormatListener(hwnd);
                InstallForegroundHook();
            }

            ShellLog.Info($"window initialized hwnd={hwnd} session={PaletteSessionMode} hotkey={hotkey} listener={listener} clipboardSequence={_lastClipboardSequenceNumber} win32={Marshal.GetLastWin32Error()}");
        };

        Loaded += async (_, _) =>
        {
            var showAfterPreRender =
                _paletteRequested ||
                (PaletteSessionMode && !PaletteSessionStartHidden) ||
                !string.IsNullOrWhiteSpace(DebugOpenSurface) ||
                (KeepOpenForDebug && !DebugOpenSettings);
            if (showAfterPreRender || (PaletteSessionMode && KeepWarmSession))
            {
                StartRecentFirstPaintPreload();
            }

            MoveOffscreen();
            Opacity = 1;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            ConcealPalette("startup");
            WarmMouseScreenCache();
            PositionOnMouseScreen(log: false);

            ShellLog.Info("window pre-rendered while hidden");
            if (showAfterPreRender)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => ShowPalette()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            if (!string.IsNullOrWhiteSpace(TrayStartupAction))
            {
                _ = Dispatcher.BeginInvoke(new Action(RunTrayStartupAction), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            if (!PaletteSessionMode && _settings.CheckForUpdatesOnStartup)
            {
                _startupUpdateCheckTimer.Start();
            }

            if (!PaletteSessionMode)
            {
                ApplyUpdateCheckSchedule();
                if (DebugOpenSettings)
                {
                    WarmSettingsCachesSoon();
                    PrewarmHostedSettingsSoon();
                }

                _ = Task.Run(() => ClipboardSharePayload.CleanupStaleTemporaryFilesIfDue());
            }

            if (DebugOpenSettings && !PaletteSessionMode)
            {
                _ = Dispatcher.BeginInvoke(new Action(OpenSettingsForDebug), System.Windows.Threading.DispatcherPriority.SystemIdle);
            }
        };

        Closing += (_, _) =>
        {
            _isClosing = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeyRetryTimer.Stop();
            _searchTimer.Stop();
            _historyPreloadTimer.Stop();
            _paletteSessionExitTimer.Stop();
            _startupUpdateCheckTimer.Stop();
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

            FlushPendingClipboardPersists();
            if (!PaletteSessionMode)
            {
                UninstallForegroundHook();
                RemoveClipboardFormatListener(hwnd);
            }

            ShellLog.Info("window closing");
        };

        Show();
    }

    private void WarmSettingsCachesSoon()
    {
        if (_settingsCachesWarmed)
        {
            return;
        }

        _settingsCachesWarmed = true;
        _ = Dispatcher.BeginInvoke(
            new Action(SettingsWindow.WarmCaches),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void PrewarmHostedSettingsSoon()
    {
        if (_settingsPrewarmQueued ||
            _prewarmedSettingsOverlay is not null ||
            PaletteSessionMode ||
            _isClosing ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _settingsPrewarmQueued = true;
        _ = Dispatcher.BeginInvoke(
            new Action(PrewarmHostedSettings),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void PrewarmHostedSettings()
    {
        _settingsPrewarmQueued = false;
        if (_isClosing ||
            _settingsOverlay is not null ||
            _prewarmedSettingsOverlay is not null ||
            _paletteOpen ||
            Opacity > 0 ||
            IsHitTestVisible ||
            Shell.Child is not Grid host)
        {
            return;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var settings = CreateSettingsWindow();
            var overlay = CreateHostedSettingsOverlay(settings);
            overlay.Opacity = 0;
            overlay.IsHitTestVisible = false;
            host.Children.Add(overlay);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _prewarmedSettings = settings;
                _prewarmedSettingsOverlay = overlay;
                _prewarmedSettingsReady = true;
                ShellLog.Info($"settings prewarmed elapsedMs={watch.ElapsedMilliseconds}");
                SchedulePrewarmedSettingsExpiry(overlay);
            }), System.Windows.Threading.DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            ClearPrewarmedHostedSettings();
            ShellLog.Error(ex, "settings prewarm failed");
        }
    }

    private void ClearPrewarmedHostedSettings()
    {
        if (_prewarmedSettingsOverlay?.Parent is System.Windows.Controls.Panel parent)
        {
            parent.Children.Remove(_prewarmedSettingsOverlay);
        }

        _prewarmedSettingsOverlay = null;
        _prewarmedSettings = null;
        _prewarmedSettingsReady = false;
        _settingsPrewarmQueued = false;
    }

    private void SchedulePrewarmedSettingsExpiry(Border overlay)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(2.5)).ContinueWith(_task =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_settingsOverlay is null &&
                    ReferenceEquals(_prewarmedSettingsOverlay, overlay) &&
                    !_paletteOpen &&
                    Opacity == 0)
                {
                    ClearPrewarmedHostedSettings();
                    ShellLog.Info("settings prewarm cleared reason=idle");
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    public void ShowPalette(bool loadItems = true)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _paletteRequested = true;
        _paletteOpen = true;
        _paletteSessionExitTimer.Stop();
        var watch = Stopwatch.StartNew();
        var ownHwnd = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != ownHwnd)
        {
            CaptureReturnFocus(foreground);
        }

        _paletteNoActivate = _returnFocusCouldNeedNoActivate && ShouldShowPaletteWithoutActivation(_returnFocusHwnd, _returnFocusElement);
        ApplyNoActivatePaletteStyle(_paletteNoActivate);

        if (!IsVisible)
        {
            Show();
        }

        Opacity = 0;
        IsHitTestVisible = false;
        PositionOnMouseScreen();
        EnsureAppHeaderIcon();
        EnsureChromeIcons();
        Opacity = 1;
        IsHitTestVisible = true;
        if (ShouldActivatePaletteWindow(_paletteNoActivate))
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ActivatePaletteWindow(ownHwnd)), System.Windows.Threading.DispatcherPriority.Input);
        }

        _outsideClickTimer.Start();
        ShellLog.Info($"palette shown elapsedMs={watch.ElapsedMilliseconds} selected={_selected?.Id ?? "none"} rows={_rows.Count} dirty={_itemsDirtySinceRender} noActivate={_paletteNoActivate}");

        if (loadItems && (_itemsDirtySinceRender || _rows.Count == 0))
        {
            QueueLoadItems(selectFirst: _selected is null, reason: "show-refresh");
        }

        if (loadItems)
        {
            ScheduleDebugInitialSearch();
            ScheduleDebugOpenSurface();
        }

        if (loadItems && !PaletteSessionMode)
        {
            QueueWindowsHistoryImportAfterShow();
            PromptForKnownUpdate();
        }

        if (loadItems)
        {
            ScheduleDebugAutoConceal();
        }
    }

    public void HandleExternalShowPaletteSignal()
    {
        var action = TrayActionRequest.Consume();
        if (string.IsNullOrWhiteSpace(action))
        {
            ShowPalette();
            return;
        }

        RunTrayAction(action);
    }

    private void RunTrayStartupAction()
    {
        var action = TrayStartupAction;
        TrayStartupAction = null;
        if (!string.IsNullOrWhiteSpace(action))
        {
            RunTrayAction(action);
        }
    }

    private void RunTrayAction(string action)
    {
        switch (NormalizeTrayAction(action))
        {
            case "settings":
                ShowPalette();
                OpenSettingsFromTray();
                break;
            case "check-updates":
                ShowPalette();
                CheckForUpdatesFromTray();
                break;
            case "save-log":
                WriteDebugSnapshot("tray");
                ShowToast("Log snapshot saved");
                break;
            default:
                ShowPalette();
                break;
        }
    }

    private static string NormalizeTrayAction(string action) =>
        action.Trim().ToLowerInvariant();

    internal static bool ShouldActivatePaletteWindow(bool noActivate) => !noActivate;

    private void QueueWindowsHistoryImportAfterShow()
    {
        if (_windowsHistoryImportAfterShowQueued)
        {
            return;
        }

        _windowsHistoryImportAfterShowQueued = true;
        _ = Task.Delay(WindowsHistoryImportAfterShowDelay).ContinueWith(_ =>
        {
            if (_isClosing)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _windowsHistoryImportAfterShowQueued = false;
                    if (_isClosing || !_paletteOpen)
                    {
                        return;
                    }

                    _ = ImportWindowsClipboardHistoryAsync("show", refreshVisible: true);
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }, TaskScheduler.Default);
    }

    private void ScheduleDebugInitialSearch()
    {
        if (string.IsNullOrWhiteSpace(DebugInitialSearch))
        {
            return;
        }

        var text = DebugInitialSearch;
        DebugInitialSearch = null;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            SearchBox.Text = text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            _searchTimer.Stop();
            QueueLoadItems(selectFirst: false, reason: "search");
            ShellLog.Info($"debug search applied queryLength={text.Length}");
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ScheduleDebugOpenSurface()
    {
        if (string.IsNullOrWhiteSpace(DebugOpenSurface))
        {
            return;
        }

        _debugOpenSurface = DebugOpenSurface.Trim();
        DebugOpenSurface = null;
        _debugOpenSurfaceAttempts = 0;
        QueueDebugOpenSurface();
    }

    private void QueueDebugOpenSurface(int delayMs = 40)
    {
        if (delayMs <= 0)
        {
            _ = Dispatcher.BeginInvoke(new Action(TryOpenDebugSurface), System.Windows.Threading.DispatcherPriority.ContextIdle);
            return;
        }

        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(TryOpenDebugSurface), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }, TaskScheduler.Default);
    }

    private void TryOpenDebugSurface()
    {
        if (string.IsNullOrWhiteSpace(_debugOpenSurface) || _isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        var surface = NormalizeDebugSurface(_debugOpenSurface);
        var item = FindDebugSurfaceItem(surface);
        if (item is null)
        {
            if (++_debugOpenSurfaceAttempts < DebugOpenSurfaceMaxAttempts)
            {
                QueueDebugOpenSurface();
                return;
            }

            ShellLog.Info($"debug surface skipped surface={_debugOpenSurface} reason=no-item");
            _debugOpenSurface = null;
            return;
        }

        _debugOpenSurface = null;
        ShellLog.Info($"debug surface opening surface={surface} item={item.Id}");
        switch (surface)
        {
            case "rename":
                RenameItem(item);
                break;
            case "edit-text":
                EditText(item);
                break;
            case "open-with":
                OpenWith(item);
                break;
            default:
                ShellLog.Info($"debug surface skipped surface={surface} reason=unknown");
                break;
        }
    }

    private ClipboardHistoryItem? FindDebugSurfaceItem(string surface)
    {
        foreach (var item in DebugSurfaceCandidates())
        {
            var fullItem = _store.GetItem(item.Id) ?? item;
            if (surface switch
                {
                    "edit-text" => fullItem.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link,
                    "open-with" => HasOpenWithTarget(fullItem),
                    _ => true,
                })
            {
                return fullItem;
            }
        }

        return null;
    }

    private IEnumerable<ClipboardHistoryItem> DebugSurfaceCandidates()
    {
        if (_selected is not null)
        {
            yield return _selected;
        }

        foreach (var item in _allItems)
        {
            if (_selected is null || !string.Equals(item.Id, _selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                yield return item;
            }
        }
    }

    private static string NormalizeDebugSurface(string surface)
    {
        return surface.Trim().ToLowerInvariant() switch
        {
            "edit" or "text" or "edit-text" => "edit-text",
            "open" or "openwith" or "open-with" => "open-with",
            "rename" => "rename",
            var normalized => normalized,
        };
    }

    private static bool HasOpenWithTarget(ClipboardHistoryItem item)
    {
        var targetPath = item.Kind == ClipboardItemKind.Image ? item.AssetPath : item.FilePaths.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(targetPath) && (File.Exists(targetPath) || Directory.Exists(targetPath));
    }

    private void ScheduleDebugAutoConceal()
    {
        if (DebugAutoConcealMs is not int delayMs || delayMs <= 0)
        {
            return;
        }

        DebugAutoConcealMs = null;
        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => ConcealPalette("debug-auto-conceal")), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void ActivatePaletteWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, ShowWindowRestore);
        }
        else
        {
            ShowWindow(hwnd, ShowWindowShow);
        }

        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
        var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    public void CheckForUpdatesFromTray()
    {
        _ = CheckForUpdatesAsync(showToastWhenCurrent: true, promptIfAvailable: true, nativeNotify: true);
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
        _paletteOpen = false;
        Opacity = 0;
        IsHitTestVisible = false;
        MoveOffscreen();
        DisposeHtmlPreview();
        ShellLog.Info($"palette concealed reason={reason}");
        if (PaletteSessionMode && KeepWarmSession)
        {
            _paletteSessionExitTimer.Stop();
            ShellLog.Info($"palette session kept resident reason={reason}");
            return;
        }

        if (PaletteSessionMode && !string.Equals(reason, "startup", StringComparison.OrdinalIgnoreCase))
        {
            if (_paletteSessionExitRequested || _isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (string.Equals(reason, "debug-auto-conceal", StringComparison.OrdinalIgnoreCase))
            {
                ExitPaletteSession(reason);
                return;
            }

            _paletteSessionExitTimer.Stop();
            _paletteSessionExitTimer.Start();
            ShellLog.Info($"palette session kept warm reason={reason} keepAliveMs={(int)PaletteSessionKeepAlive.TotalMilliseconds}");
        }
        else if (PaletteSessionMode && string.Equals(reason, "startup", StringComparison.OrdinalIgnoreCase))
        {
            _paletteSessionExitTimer.Stop();
            _paletteSessionExitTimer.Start();
            ShellLog.Info($"palette session startup guard keepAliveMs={(int)PaletteSessionKeepAlive.TotalMilliseconds}");
        }
    }

    private void ExitPaletteSessionIfIdle()
    {
        _paletteSessionExitTimer.Stop();
        if (!PaletteSessionMode || KeepWarmSession || _paletteOpen || _isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        ExitPaletteSession("idle-timeout");
    }

    private void ExitPaletteSession(string reason)
    {
        _paletteSessionExitRequested = true;
        _paletteSessionExitTimer.Stop();
        ShellLog.Info($"palette session exiting reason={reason}");
        _ = Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()), System.Windows.Threading.DispatcherPriority.Background);
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
        ShellLog.Snapshot("=== Snapshot ===");
        ShellLog.Snapshot($"reason={reason} visible={IsVisible} paletteOpen={_paletteOpen} selected={_selected?.Id ?? "none"} kind={_selected?.Kind.ToString() ?? "none"} filter={_kindFilter} date={_dateFilter} file={_fileFilter}");
        ShellLog.Snapshot($"items all={_allItems.Count} renderedRows={_rows.Count} search={SearchBox.Text}");
        if (_selected is not null)
        {
            ShellLog.Snapshot($"selected preview={_selected.Preview} pinned={_selected.IsPinned} source={_selected.SourceApplication} path={_selected.SourceApplicationPath}");
        }

        ShellLog.Snapshot($"scroll listV={ListScroll.VerticalOffset}/{ListScroll.ScrollableHeight} listH={ListScroll.HorizontalOffset}/{ListScroll.ScrollableWidth} info={InfoScroll.VerticalOffset}/{InfoScroll.ScrollableHeight}");
        ShellLog.Snapshot($"ui popupOpen={ActionMenuPopup.IsOpen} rows={ItemsHost.Children.Count} infoRows={InfoHost.Children.Count}");
        ShellLog.Snapshot("=== End Snapshot ===");
        ShellLog.Flush();
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
            ShellLog.Info($"{_settings.Hotkeys.OpenClip} received open={_paletteOpen}");
            if (_paletteOpen)
            {
                ConcealPalette("hotkey-toggle");
            }
            else
            {
                ShowPalette();
            }

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
            if (ShouldSkipClipboardSequence())
            {
                handled = true;
                return IntPtr.Zero;
            }

            CaptureClipboard();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private bool ShouldSkipClipboardSequence()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == 0)
        {
            return false;
        }

        if (sequence == _lastClipboardSequenceNumber)
        {
            ShellLog.Info($"clipboard skipped duplicate sequence={sequence}");
            return true;
        }

        _lastClipboardSequenceNumber = sequence;
        return false;
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

        if (!_openHotkeyRegistered && !_openHotkeyUnavailable)
        {
            _openHotkeyRegistered = RegisterConfiguredHotkey(
                hwnd,
                OpenHotkeyId,
                _settings.Hotkeys.OpenClip,
                ClipHotkeyDefaults.OpenClip,
                "open",
                reason,
                out _openHotkeyUnavailable);
        }

        if (!_debugLogHotkeyRegistered && !_debugLogHotkeyUnavailable)
        {
            _debugLogHotkeyRegistered = RegisterConfiguredHotkey(
                hwnd,
                DebugLogHotkeyId,
                _settings.Hotkeys.SaveDebugLog,
                ClipHotkeyDefaults.SaveDebugLog,
                "debug-log",
                reason,
                out _debugLogHotkeyUnavailable);
        }

        if (_openHotkeyRegistered && _debugLogHotkeyRegistered)
        {
            _hotkeyRetryTimer.Stop();
        }
        else if ((_openHotkeyUnavailable || _openHotkeyRegistered) &&
            (_debugLogHotkeyUnavailable || _debugLogHotkeyRegistered))
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
                _openHotkeyUnavailable = false;
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
            if (!_openHotkeyRegistered && !_openHotkeyUnavailable)
            {
                EnsureHotkeyRegistered("foreground-default");
            }
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private static bool RegisterConfiguredHotkey(IntPtr hwnd, int id, string configured, string fallback, string name, string reason, out bool unavailable)
    {
        unavailable = false;
        if (!ClipHotkeyGesture.TryParseGlobal(configured, out var gesture) && !ClipHotkeyGesture.TryParseGlobal(fallback, out gesture))
        {
            ShellLog.Info($"hotkey register skipped name={name} configured={configured} reason={reason}");
            return false;
        }

        var registered = RegisterHotKey(hwnd, id, gesture.WinModifiers, gesture.VirtualKey);
        var win32 = Marshal.GetLastWin32Error();
        unavailable = !registered && win32 == ErrorHotkeyAlreadyRegistered;
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
                    ContentHash = HashText(string.Join("|", files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))),
                    SourceApplication = source.Name,
                    SourceApplicationPath = source.Path,
                };
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image is not null)
                {
                    QueueImageClipboardCapture(image, source);
                    return;
                }
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                var captureRichText = _settings.DefaultPasteFormat == PasteFormatPreference.OriginalFormatting;
                var htmlText = captureRichText ? ClipboardTextOrNull(System.Windows.TextDataFormat.Html) : null;
                var rtfText = captureRichText ? ClipboardTextOrNull(System.Windows.TextDataFormat.Rtf) : null;
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
        if (ShouldSkipDuplicateClipboardBurst(item))
        {
            return;
        }

        QueueClipboardItemSave(item, "clipboard-live");
    }

    private bool ShouldSkipDuplicateClipboardBurst(ClipboardHistoryItem item)
    {
        var fingerprint = ClipboardFingerprint(item);
        if (!_clipboardCaptureBurstGate.ShouldSkip(fingerprint, DateTimeOffset.UtcNow))
        {
            return false;
        }

        DeleteUnsavedCaptureAsset(item);
        ShellLog.Info($"clipboard skipped duplicate burst kind={item.Kind} source={item.SourceApplication} preview={item.Preview}");
        return true;
    }

    private static string? ClipboardFingerprint(ClipboardHistoryItem item)
    {
        return string.IsNullOrWhiteSpace(item.ContentHash)
            ? null
            : $"{item.Kind}:{item.ContentHash}";
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

        QueueClipboardItemSave(pending, "clipboard-live");
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

    private void QueueClipboardItemSave(ClipboardHistoryItem item, string renderReason)
    {
        if (!ClipItemSizeLimit.Allows(item, _settings.MaxItemSizeBytes))
        {
            var itemBytes = ClipItemSizeLimit.EstimateBytes(item);
            ShellLog.Info($"clipboard skipped oversized kind={item.Kind} bytes={itemBytes} limit={ClipItemSizeLimit.MaxItemSizeLabel(_settings.MaxItemSizeBytes)} source={item.SourceApplication} preview={item.Preview}");
            DeleteUnsavedCaptureAsset(item);
            ShowToast("Clipboard item skipped: too large");
            return;
        }

        var maxItems = EffectiveHistoryLimit();
        var persist = PersistClipboardItemAsync(item, maxItems);
        TrackClipboardPersist(persist);
        _ = persist.ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                DeleteUnsavedCaptureAsset(item);
                ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("clipboard persist failed"), "clipboard persist failed");
                return;
            }

            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => ApplySavedClipboardItem(task.Result, renderReason)), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private async Task<ClipboardHistoryItem> PersistClipboardItemAsync(ClipboardHistoryItem item, int maxItems)
    {
        await _clipboardPersistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _store.AddOrUpdate(item, maxItems)).ConfigureAwait(false);
        }
        finally
        {
            _clipboardPersistGate.Release();
        }
    }

    private void ApplySavedClipboardItem(ClipboardHistoryItem saved, string renderReason)
    {
        ShellLog.Info($"clipboard captured id={saved.Id} kind={saved.Kind} source={saved.SourceApplication} preview={saved.Preview}");
        ClearRecentFirstPaintPreload();
        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
        if (_paletteOpen)
        {
            RenderItems(reason: renderReason);
        }
        else
        {
            _itemsDirtySinceRender = true;
        }
    }

    private void PreloadHistorySummariesIfCheap()
    {
        if (_historySummariesPreloaded || _historyPreloadInProgress || _paletteOpen || !CanPreloadHistorySummaries())
        {
            return;
        }

        _historyPreloadInProgress = true;
        var watch = Stopwatch.StartNew();
        _ = Task.Run(() => _store.QueryItemSummaries()).ContinueWith(task =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _historyPreloadInProgress = false;
                if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                if (task.IsFaulted)
                {
                    ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("history preload failed"), "history preload failed");
                    return;
                }

                if (_paletteOpen || !string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    return;
                }

                _allItems = task.Result;
                _historySummariesPreloaded = true;
                _itemsDirtySinceRender = true;
                ShellLog.Info($"history summaries preloaded count={_allItems.Count} elapsedMs={watch.ElapsedMilliseconds}");
            }), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void StartRecentFirstPaintPreload()
    {
        if (_recentFirstPaintPreloadTask is not null || !_store.HasCurrentRecentSummaryIndex())
        {
            return;
        }

        _recentFirstPaintPreloadTask = Task.Run(() =>
        {
            var queryWatch = Stopwatch.StartNew();
            var items = _store.QueryRecentItemSummaries(InitialSummaryFirstPaintLimit);
            return ((IReadOnlyList<ClipboardHistoryItem>)items, queryWatch.ElapsedMilliseconds);
        });
    }

    private void ClearRecentFirstPaintPreload()
    {
        _recentFirstPaintPreloadTask = null;
    }

    private bool CanPreloadHistorySummaries()
    {
        try
        {
            if (!_store.HasCurrentSummaryIndex() || !File.Exists(_store.HistoryIndexFilePath))
            {
                return false;
            }

            return new FileInfo(_store.HistoryIndexFilePath).Length <= SummaryPreloadMaximumBytes;
        }
        catch
        {
            return false;
        }
    }

    private void QueueImageClipboardCapture(BitmapSource image, (string? Name, string? Path) source)
    {
        var capturedAt = DateTimeOffset.Now;
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var path = _store.NewAssetFilePath(".png");

        if (!image.IsFrozen && image.CanFreeze)
        {
            image.Freeze();
        }

        if (!image.IsFrozen)
        {
            try
            {
                CaptureClipboardItem(CreateImageClipboardItem(image, path, width, height, source, capturedAt));
            }
            catch (Exception ex)
            {
                DeleteCaptureFile(path);
                ShellLog.Error(ex, "clipboard image capture failed");
            }

            return;
        }

        _ = Task.Run(() => CreateImageClipboardItem(image, path, width, height, source, capturedAt)).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                DeleteCaptureFile(path);
                ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("clipboard image capture failed"), "clipboard image capture failed");
                return;
            }

            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                DeleteCaptureFile(path);
                return;
            }

            _ = Dispatcher.BeginInvoke(new Action(() => CaptureClipboardItem(task.Result)), System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private static ClipboardHistoryItem CreateImageClipboardItem(BitmapSource image, string path, int width, int height, (string? Name, string? Path) source, DateTimeOffset capturedAt)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        return new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Image,
            AssetPath = path,
            Preview = $"Image {width} x {height}",
            ContentHash = HashFile(path),
            ImageWidth = width,
            ImageHeight = height,
            SourceApplication = source.Name,
            SourceApplicationPath = source.Path,
            CreatedAt = capturedAt,
            LastUsedAt = capturedAt,
            FirstCopiedAt = capturedAt,
            LastCopiedAt = capturedAt,
        };
    }

    private void TrackClipboardPersist(Task task)
    {
        lock (_clipboardPersistTasksGate)
        {
            _clipboardPersistTasks.Add(task);
        }

        _ = task.ContinueWith(completed =>
        {
            lock (_clipboardPersistTasksGate)
            {
                _clipboardPersistTasks.Remove(completed);
            }
        }, TaskScheduler.Default);
    }

    private void FlushPendingClipboardPersists()
    {
        Task[] pending;
        lock (_clipboardPersistTasksGate)
        {
            pending = _clipboardPersistTasks.Where(task => !task.IsCompleted).ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "clipboard persist flush failed");
        }
    }

    private static void DeleteCaptureFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
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

    private void QueueLoadItems(bool selectFirst, string reason)
    {
        var generation = ++_loadGeneration;
        var query = SearchBox.Text;
        var totalWatch = Stopwatch.StartNew();
        var isSearch = string.Equals(reason, "search", StringComparison.OrdinalIgnoreCase);
        if (isSearch)
        {
            CancelBackgroundFullSummaryRefresh(reason);
        }

        if (string.IsNullOrWhiteSpace(query) && _historySummariesPreloaded)
        {
            try
            {
                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = false;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: true);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs=0 elapsedMs={totalWatch.ElapsedMilliseconds} preloaded=True");
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, $"load preloaded items failed reason={reason}");
            }

            return;
        }

        if (ShouldUseRecentSummaryFirstPaint(query, reason))
        {
            try
            {
                var preloaded = false;
                long queryElapsedMs;
                var preloadTask = _recentFirstPaintPreloadTask;
                if (preloadTask is { IsCompletedSuccessfully: true })
                {
                    _allItems = preloadTask.Result.Items;
                    queryElapsedMs = preloadTask.Result.QueryElapsedMs;
                    preloaded = true;
                    ClearRecentFirstPaintPreload();
                }
                else
                {
                    var queryWatch = Stopwatch.StartNew();
                    _allItems = _store.QueryRecentItemSummaries(InitialSummaryFirstPaintLimit);
                    queryElapsedMs = queryWatch.ElapsedMilliseconds;
                }

                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = true;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: true);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs={queryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds} recent=True preloaded={preloaded}");
                QueueFullSummaryRefreshAfterFirstPaint(generation, selectFirst);
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, $"load recent items failed reason={reason}");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(query) && _store.HasCurrentSummaryIndex())
        {
            try
            {
                var queryWatch = Stopwatch.StartNew();
                _allItems = _store.QueryItemSummaries();
                _historySummariesPreloaded = true;
                var queryElapsedMs = queryWatch.ElapsedMilliseconds;
                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = false;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: true);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs={queryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds} inline=True");
            }
            catch (Exception ex)
            {
                ShellLog.Error(ex, $"load items failed reason={reason}");
            }

            return;
        }

        _ = Task.Run(() =>
        {
            var queryWatch = Stopwatch.StartNew();
            var items = _store.QueryItemSummaries(query);
            return (Items: items, QueryElapsedMs: queryWatch.ElapsedMilliseconds);
        }).ContinueWith(task =>
        {
            var dispatcherPriority = isSearch
                ? System.Windows.Threading.DispatcherPriority.Send
                : System.Windows.Threading.DispatcherPriority.Background;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _loadGeneration)
                {
                    ShellLog.Info($"load items canceled reason={reason} generation={generation}");
                    return;
                }

                if (task.IsFaulted)
                {
                    ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("load items failed"), $"load items failed reason={reason}");
                    return;
                }

                _allItems = task.Result.Items;
                _historySummariesPreloaded = string.IsNullOrWhiteSpace(query);
                var visibleItems = RenderItems(reason);
                _itemsDirtySinceRender = false;
                SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: false);

                ShellLog.Info($"load items reason={reason} count={_allItems.Count} queryElapsedMs={task.Result.QueryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds}");
            }), dispatcherPriority);
        });
    }

    private void CancelBackgroundFullSummaryRefresh(string reason)
    {
        var operation = _backgroundFullRefreshOperation;
        if (operation is null)
        {
            return;
        }

        _backgroundFullRefreshOperation = null;
        if (operation.Status == System.Windows.Threading.DispatcherOperationStatus.Pending)
        {
            _ = operation.Abort();
            ShellLog.Info($"background full refresh canceled reason={reason}");
        }
    }

    private bool ShouldUseRecentSummaryFirstPaint(string? query, string reason)
    {
        return string.IsNullOrWhiteSpace(query) &&
            string.Equals(reason, "show-refresh", StringComparison.OrdinalIgnoreCase) &&
            _store.HasCurrentRecentSummaryIndex();
    }

    private void QueueFullSummaryRefreshAfterFirstPaint(int generation, bool selectFirst)
    {
        var totalWatch = Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            await Task.Delay(180);
            var queryWatch = Stopwatch.StartNew();
            var items = _store.QueryItemSummaries();
            return (Items: items, QueryElapsedMs: queryWatch.ElapsedMilliseconds);
        }).ContinueWith(task =>
        {
            QueueApply(0);

            void QueueApply(int delayMs)
            {
                if (delayMs <= 0)
                {
                    _backgroundFullRefreshOperation = Dispatcher.BeginInvoke(new Action(Apply), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    return;
                }

                _ = Task.Delay(delayMs).ContinueWith(_ =>
                {
                    if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        return;
                    }

                    _backgroundFullRefreshOperation = Dispatcher.BeginInvoke(new Action(Apply), System.Windows.Threading.DispatcherPriority.ContextIdle);
                }, TaskScheduler.Default);
            }

            void Apply()
            {
                _backgroundFullRefreshOperation = null;
                if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                if (generation != _loadGeneration || !string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    ShellLog.Info($"load items canceled reason=background-full-refresh generation={generation}");
                    return;
                }

                if (task.IsFaulted)
                {
                    ShellLog.Error(task.Exception?.GetBaseException() ?? new InvalidOperationException("load items failed"), "load items failed reason=background-full-refresh");
                    return;
                }

                _allItems = task.Result.Items;
                _historySummariesPreloaded = true;
                if (_paletteOpen)
                {
                    if (IsPaletteInteractionBusy())
                    {
                        QueueApply(120);
                        return;
                    }

                    var visibleItems = RenderItems("background-full-refresh");
                    _itemsDirtySinceRender = false;
                    SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: false);
                }
                else
                {
                    _itemsDirtySinceRender = true;
                }

                ShellLog.Info($"load items reason=background-full-refresh count={_allItems.Count} queryElapsedMs={task.Result.QueryElapsedMs} elapsedMs={totalWatch.ElapsedMilliseconds}");
            }
        }, TaskScheduler.Default);
    }

    private bool IsPaletteInteractionBusy()
    {
        return _suppressDeactivate ||
            ActionMenuPopup.IsOpen ||
            ShareSubmenuPopup.IsOpen ||
            IsContextMenuOpen(this);
    }

    private void LoadItems(bool selectFirst, string reason)
    {
        _loadGeneration++;
        var watch = Stopwatch.StartNew();
        try
        {
            _allItems = _store.QueryItemSummaries(SearchBox.Text);
            _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
            var visibleItems = RenderItems(reason);
            _itemsDirtySinceRender = false;
            SelectInitialItemIfNeeded(selectFirst, visibleItems, defer: false);
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

    private void SelectInitialItemIfNeeded(bool selectFirst, IReadOnlyList<ClipboardHistoryItem> visibleItems, bool defer)
    {
        if (!selectFirst || _selected is not null)
        {
            return;
        }

        var first = visibleItems.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        if (!defer)
        {
            SelectItem(first, reason: "initial");
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_selected is null)
            {
                SelectItem(first, reason: "initial");
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task ImportWindowsClipboardHistoryAsync(string reason, bool refreshVisible)
    {
        if (_historyImportInProgress)
        {
            return;
        }

        if (!_windowsHistoryImportThrottle.TryBegin(DateTimeOffset.UtcNow))
        {
            ShellLog.Info($"windows history import skipped reason={reason} throttle={WindowsHistoryImportMinimumInterval}");
            return;
        }

        _historyImportInProgress = true;
        var watch = Stopwatch.StartNew();
        try
        {
            var imported = await ImportWindowsClipboardHistoryInHelperAsync(EffectiveHistoryLimit());
            if (imported > 0)
            {
                _itemsDirtySinceRender = true;
                _historySummariesPreloaded = false;
                if (refreshVisible && _paletteOpen)
                {
                    _ = Dispatcher.BeginInvoke(new Action(() => QueueLoadItems(selectFirst: _selected is null, reason: $"windows-history-{reason}")), System.Windows.Threading.DispatcherPriority.Background);
                }
            }

            ShellLog.Info($"windows history import reason={reason} imported={imported} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"windows history import failed reason={reason}");
        }
        finally
        {
            _historyImportInProgress = false;
        }
    }

    private static async Task<int> ImportWindowsClipboardHistoryInHelperAsync(int maxItems)
    {
        var helper = FindWindowsHistoryExecutable();
        if (helper is null)
        {
            ShellLog.Info("windows history import skipped helper=missing");
            return 0;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helper) ?? AppContext.BaseDirectory,
            },
        };
        process.StartInfo.ArgumentList.Add("import-windows-history");
        process.StartInfo.ArgumentList.Add("--max");
        process.StartInfo.ArgumentList.Add(maxItems.ToString());

        if (!process.Start())
        {
            return 0;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            ShellLog.Info($"windows history import helper failed exit={process.ExitCode} error={error.Trim()}");
            return 0;
        }

        return ParseImportCount(output);
    }

    private static string? FindWindowsHistoryExecutable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Clip.WindowsHistory.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory, "Clip.WindowsHistory.exe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return null;
    }

    private static int ParseImportCount(string output)
    {
        foreach (var line in output.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (int.TryParse(line.Trim(), out var count))
            {
                return count;
            }
        }

        return 0;
    }

    private IReadOnlyList<ClipboardHistoryItem> RenderItems(string reason)
    {
        var watch = Stopwatch.StartNew();
        var selectedId = _selected?.Id;
        var visibleItems = FilteredItems();
        var entries = RenderEntries(visibleItems);
        var generation = ++_renderGeneration;
        ItemsHost.Children.Clear();
        _rows.Clear();
        _deferredRenderEntries = [];
        _deferredRenderIndex = 0;
        _deferredRenderGeneration = generation;
        _deferredRenderReason = string.Empty;
        _deferredRenderWatch = null;
        UpdateFilterVisuals();

        var nextIndex = AddRenderEntries(entries, 0, InitialRenderEntryBatch);

        if (selectedId is not null && _rows.TryGetValue(selectedId, out var selectedRow))
        {
            selectedRow.Background = (WpfBrush)FindResource("Selected");
            selectedRow.BorderBrush = (WpfBrush)FindResource("SelectedBorder");
            selectedRow.BorderThickness = new Thickness(1);
        }

        if (nextIndex < entries.Count)
        {
            _deferredRenderEntries = entries;
            _deferredRenderIndex = nextIndex;
            _deferredRenderGeneration = generation;
            _deferredRenderReason = reason;
            _deferredRenderWatch = watch;
            _ = Dispatcher.BeginInvoke(new Action(() => AppendDeferredRowsIfNeeded(force: ListScroll.ScrollableHeight <= 0)), System.Windows.Threading.DispatcherPriority.Background);
        }

        ShellLog.Info($"render items reason={reason} rows={_rows.Count}/{visibleItems.Count} selected={selectedId ?? "none"} elapsedMs={watch.ElapsedMilliseconds} deferred={nextIndex < entries.Count}");
        return visibleItems;
    }

    private static List<(string? Header, ClipboardHistoryItem? Item)> RenderEntries(IReadOnlyList<ClipboardHistoryItem> visibleItems)
    {
        var entries = new List<(string? Header, ClipboardHistoryItem? Item)>();
        foreach (var group in GroupItems(visibleItems))
        {
            if (group.Items.Count == 0)
            {
                continue;
            }

            entries.Add(($"{group.Header.ToUpperInvariant()}  {group.Items.Count}", null));
            foreach (var item in group.Items)
            {
                entries.Add((null, item));
            }
        }

        return entries;
    }

    private int AddRenderEntries(IReadOnlyList<(string? Header, ClipboardHistoryItem? Item)> entries, int start, int count)
    {
        var end = Math.Min(entries.Count, start + count);
        for (var index = start; index < end; index++)
        {
            var entry = entries[index];
            if (entry.Header is not null)
            {
                var header = new TextBlock
                {
                    Text = entry.Header,
                    Foreground = (WpfBrush)FindResource("Muted"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(16, 12, 8, 4),
                };
                ItemsHost.Children.Add(header);
                continue;
            }

            if (entry.Item is null)
            {
                continue;
            }

            var row = BuildRow(entry.Item);
            ItemsHost.Children.Add(row);
            _rows[entry.Item.Id] = row;
        }

        return end;
    }

    private void AppendDeferredRowsIfNeeded(bool force = false)
    {
        if (_deferredRenderEntries.Count == 0 || _deferredRenderIndex >= _deferredRenderEntries.Count)
        {
            return;
        }

        if (_deferredRenderGeneration != _renderGeneration)
        {
            ShellLog.Info($"render items canceled reason={_deferredRenderReason} start={_deferredRenderIndex}");
            _deferredRenderEntries = [];
            return;
        }

        if (!force && ListScroll.ScrollableHeight - ListScroll.VerticalOffset > 140)
        {
            return;
        }

        _deferredRenderIndex = AddRenderEntries(_deferredRenderEntries, _deferredRenderIndex, DeferredRenderEntryBatch);
        if (_deferredRenderIndex < _deferredRenderEntries.Count)
        {
            ShellLog.Info($"render items appended reason={_deferredRenderReason} rows={_rows.Count} next={_deferredRenderIndex}/{_deferredRenderEntries.Count}");
            return;
        }

        ShellLog.Info($"render items complete reason={_deferredRenderReason} rows={_rows.Count} elapsedMs={_deferredRenderWatch?.ElapsedMilliseconds ?? 0}");
        _deferredRenderEntries = [];
        _deferredRenderIndex = 0;
        _deferredRenderReason = string.Empty;
        _deferredRenderWatch = null;
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
                image.Source = IconFor(imageItem, 96, preferRichPreview: false);
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
            Source = IconFor(item, 96, preferRichPreview: false),
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

        var revealPath = ClipboardItemRevealTarget.GetPath(item);
        if (revealPath is not null)
        {
            if (item.Kind is not (ClipboardItemKind.Image or ClipboardItemKind.Files))
            {
                actions.Add(MenuAction.Separator);
            }

            actions.Add(new MenuAction("Show in File Explorer", () => ShowInFileExplorer(item)));
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
                TextPreview.Text = TextFilePreviewReader.Format(TextPayload(item), TextPreviewCharacterLimit);
                TextPreview.Foreground = (WpfBrush)FindResource("Text");
                TextPreview.Visibility = Visibility.Visible;
                ShellLog.Info($"preview text id={item.Id} chars={TextPreview.Text.Length}");
                return;
            }

            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
            {
                ImagePreview.Source = LoadCachedBitmap(item.AssetPath, PreviewImageDecodePixels);
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
                    ImagePreview.Source = LoadCachedBitmap(path, PreviewImageDecodePixels);
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
                    await ShowHtmlPreviewAsync(path);
                });
                ShellLog.Info($"preview html path={path} elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }

            if (IsTextFile(ext))
            {
                var text = await TextFilePreviewReader.ReadAsync(path, TextPreviewCharacterLimit);

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

    private IReadOnlyList<ClipboardHistoryItem> FilteredItems()
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
            var today = DateTime.Today;
            items = items.Where(i => DateKey(i, today) == _dateFilter);
        }

        if (_kindFilter == "files" && _fileFilter != "all")
        {
            items = items.Where(i => i.FilePaths.Any(path => FileKindKey(path) == _fileFilter));
        }

        return items.ToList();
    }

    private static IEnumerable<(string Header, List<ClipboardHistoryItem> Items)> GroupItems(IEnumerable<ClipboardHistoryItem> items)
    {
        var pinned = new List<ClipboardHistoryItem>();
        var todayItems = new List<ClipboardHistoryItem>();
        var yesterday = new List<ClipboardHistoryItem>();
        var week = new List<ClipboardHistoryItem>();
        var month = new List<ClipboardHistoryItem>();
        var year = new List<ClipboardHistoryItem>();
        var older = new List<ClipboardHistoryItem>();
        var today = DateTime.Today;

        foreach (var item in items)
        {
            if (item.IsPinned)
            {
                pinned.Add(item);
                continue;
            }

            switch (DateKey(item, today))
            {
                case "today":
                    todayItems.Add(item);
                    break;
                case "yesterday":
                    yesterday.Add(item);
                    break;
                case "week":
                    week.Add(item);
                    break;
                case "month":
                    month.Add(item);
                    break;
                case "year":
                    year.Add(item);
                    break;
                default:
                    older.Add(item);
                    break;
            }
        }

        pinned.Sort((left, right) => left.PinOrder.CompareTo(right.PinOrder));
        SortByLastCopied(todayItems);
        SortByLastCopied(yesterday);
        SortByLastCopied(week);
        SortByLastCopied(month);
        SortByLastCopied(year);
        SortByLastCopied(older);

        yield return ("Pinned items", pinned);
        yield return ("Today", todayItems);
        yield return ("Yesterday", yesterday);
        yield return ("This week", week);
        yield return ("This month", month);
        yield return ("This year", year);
        yield return ("Older", older);
    }

    private void SetFilter(string kind)
    {
        _kindFilter = kind;
        if (kind != "files")
        {
            _fileFilter = "all";
        }

        var visibleItems = RenderItems($"filter-{kind}");
        SelectItem(visibleItems.FirstOrDefault(), $"filter-{kind}");
        ShellLog.Info($"filter changed kind={kind} date={_dateFilter} file={_fileFilter}");
    }

    private void TogglePin(ClipboardHistoryItem item)
    {
        var next = !item.IsPinned;
        if (_store.SetPinned(item.Id, next))
        {
            item.IsPinned = next;
            _allItems = _store.QueryItemSummaries(SearchBox.Text);
            _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
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

        _allItems = _store.QueryItemSummaries(SearchBox.Text);
        _historySummariesPreloaded = string.IsNullOrWhiteSpace(SearchBox.Text);
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
        var selected = ClipboardItemForPasteFormat(_selected);
        SetClipboard(selected, _settings.DefaultPasteFormat);
        ShellLog.Info($"copy selected id={_selected.Id}");
    }

    private void PasteSelected()
    {
        if (_selected is null) return;
        var selected = ClipboardItemForPasteFormat(_selected);
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

    private ClipboardHistoryItem ClipboardItemForPasteFormat(ClipboardHistoryItem item)
    {
        if (NeedsFullText(item) ||
            (_settings.DefaultPasteFormat == PasteFormatPreference.OriginalFormatting &&
            ClipboardPasteData.HasOriginalFormatting(item)))
        {
            return _store.GetItem(item.Id) ?? item;
        }

        return item;
    }

    private ClipboardHistoryItem FullTextItem(ClipboardHistoryItem item)
    {
        return NeedsFullText(item) ? _store.GetItem(item.Id) ?? item : item;
    }

    private static bool NeedsFullText(ClipboardHistoryItem item)
    {
        if (item.Kind is not (ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color))
        {
            return false;
        }

        if (item.Text is null)
        {
            return true;
        }

        return item.CharacterCount is int characterCount && item.Text.Length < characterCount;
    }

    private void CaptureReturnFocus(IntPtr foreground)
    {
        var watch = Stopwatch.StartNew();
        _returnFocusHwnd = foreground;
        var windowTitle = WindowTitle(foreground);
        var windowClass = WindowClass(foreground);
        _returnFocusCouldNeedNoActivate = CouldNeedNoActivatePalette(foreground, windowTitle);
        var needsAutomation = IsFileExplorerWindowClass(windowClass) || _returnFocusCouldNeedNoActivate;
        var processName = needsAutomation ? TryGetProcessNameForWindow(foreground) : null;
        _returnFocusChildHwnd = ShouldSkipFocusedChildCapture(needsAutomation)
            ? IntPtr.Zero
            : FocusedChildWindow(foreground);
        _returnFocusElement = needsAutomation ? FocusedAutomationElement() : null;
        _returnFocusElementSummary = _returnFocusElement is null ? "none" : "captured";
        _returnFocusValueBefore = null;
        _returnFocusCommitsPasteWithEnter = _returnFocusCouldNeedNoActivate && ShouldCommitPasteWithEnter(_returnFocusHwnd, _returnFocusElement);
        ShellLog.Info($"return focus captured hwnd={_returnFocusHwnd} child={_returnFocusChildHwnd} process={processName ?? "unknown"} element={_returnFocusElementSummary} elapsedMs={watch.ElapsedMilliseconds}");
    }

    private static bool ShouldSkipFocusedChildCapture(bool needsAutomation)
    {
        return !needsAutomation;
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
        item = FullTextItem(item);
        var watch = Stopwatch.StartNew();
        if (TryShowTextEditOverlay(item, watch))
        {
            return;
        }

        var editor = new TextEditWindow(TextPayload(item), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Line"), (WpfBrush)FindResource("Surface"), (WpfBrush)FindResource("TextCursor"), (WpfBrush)FindResource("AccentSoft"), (WpfBrush)FindResource("Selected"), (WpfBrush)FindResource("SelectedBorder"))
        {
            Owner = this,
        };
        editor.ContentRendered += (_, _) => ShellLog.Info($"edit-text rendered elapsedMs={watch.ElapsedMilliseconds}");
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

    private bool TryShowTextEditOverlay(ClipboardHistoryItem item, Stopwatch watch)
    {
        if (Shell.Child is not Grid root)
        {
            return false;
        }

        CloseInlineModal(showPalette: false);
        var background = (WpfBrush)FindResource("Bg");
        var foreground = (WpfBrush)FindResource("Text");
        var line = (WpfBrush)FindResource("Line");
        var surface = (WpfBrush)FindResource("Surface");
        var textCursor = (WpfBrush)FindResource("TextCursor");
        var accentSoft = (WpfBrush)FindResource("AccentSoft");
        var selected = (WpfBrush)FindResource("Selected");
        var selectedBorder = (WpfBrush)FindResource("SelectedBorder");

        var overlay = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(76, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
        };

        var panel = new Border
        {
            Width = 640,
            Height = 420,
            Background = background,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        var box = new WpfTextBox
        {
            Text = TextPayload(item),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FocusVisualStyle = null,
            SnapsToDevicePixels = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0),
            Padding = new Thickness(14),
            Background = WpfBrushes.Transparent,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas"),
            FontSize = 13,
            CaretBrush = textCursor,
            SelectionBrush = accentSoft,
        };
        TextOptions.SetTextFormattingMode(box, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(box, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(box, TextHintingMode.Fixed);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseInlineModal();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                CommitTextEditOverlay(item, box.Text);
                e.Handled = true;
            }
        };

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
        var trim = InlineModalButton("Trim", foreground, line, surface, accentSoft, selectedBorder, selected, primary: false);
        trim.Margin = new Thickness(0, 0, 8, 0);
        trim.Click += (_, _) => { box.Text = box.Text.Trim(); };
        Grid.SetColumn(trim, 1);
        header.Children.Add(trim);
        grid.Children.Add(header);

        var editorShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(18, 0, 18, 0),
            Child = box,
        };
        Grid.SetRow(editorShell, 1);
        grid.Children.Add(editorShell);

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(18, 14, 18, 18),
        };
        var cancel = InlineModalButton("Cancel", foreground, line, surface, accentSoft, selectedBorder, selected, primary: false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => CloseInlineModal();
        var save = InlineModalButton("Save", foreground, line, surface, accentSoft, selectedBorder, selected, primary: true);
        save.Click += (_, _) => CommitTextEditOverlay(item, box.Text);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        panel.Child = grid;
        overlay.Child = panel;
        System.Windows.Controls.Panel.SetZIndex(overlay, 900);
        root.Children.Add(overlay);
        _inlineModalOverlay = overlay;
        _suppressDeactivate = true;
        root.UpdateLayout();
        ShellLog.Info($"edit-text rendered elapsedMs={watch.ElapsedMilliseconds} hosted=True");

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }), System.Windows.Threading.DispatcherPriority.Background);

        return true;
    }

    private void CommitTextEditOverlay(ClipboardHistoryItem item, string value)
    {
        CloseInlineModal(showPalette: false);
        _store.EditText(item.Id, value);
        item.Text = value;
        item.Preview = ClipboardHistoryStore.PreviewText(value);
        LoadItems(selectFirst: false, reason: "edit-text");
        SelectItem(_store.GetItem(item.Id), "edit-text");
        ShowPalette();
    }

    private void RenameItem(ClipboardHistoryItem item)
    {
        var watch = Stopwatch.StartNew();
        if (TryShowRenameOverlay(item, watch))
        {
            return;
        }

        var editor = new RenameWindow(TitleFor(item), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Muted"), (WpfBrush)FindResource("Line"), (WpfBrush)FindResource("Surface"), (WpfBrush)FindResource("AccentSoft"), (WpfBrush)FindResource("Selected"), (WpfBrush)FindResource("SelectedBorder"))
        {
            Owner = this,
        };
        editor.ContentRendered += (_, _) => ShellLog.Info($"rename rendered elapsedMs={watch.ElapsedMilliseconds}");
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

    private bool TryShowRenameOverlay(ClipboardHistoryItem item, Stopwatch watch)
    {
        if (Shell.Child is not Grid root)
        {
            return false;
        }

        CloseInlineModal(showPalette: false);
        var background = (WpfBrush)FindResource("Bg");
        var foreground = (WpfBrush)FindResource("Text");
        var muted = (WpfBrush)FindResource("Muted");
        var line = (WpfBrush)FindResource("Line");
        var surface = (WpfBrush)FindResource("Surface");
        var accentSoft = (WpfBrush)FindResource("AccentSoft");
        var selected = (WpfBrush)FindResource("Selected");
        var selectedBorder = (WpfBrush)FindResource("SelectedBorder");

        var overlay = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(76, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
        };
        overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay))
            {
                CloseInlineModal();
                e.Handled = true;
            }
        };

        var panel = new Border
        {
            Width = 420,
            Background = background,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        var box = new WpfTextBox
        {
            Text = TitleFor(item),
            FocusVisualStyle = null,
            Margin = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            Background = WpfBrushes.Transparent,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            SelectionBrush = accentSoft,
            MaxLength = 120,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitRenameOverlay(item, box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseInlineModal();
                e.Handled = true;
            }
        };

        var grid = new Grid { Background = background, Margin = new Thickness(18, 16, 18, 18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = "Rename",
            Foreground = foreground,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        var hint = new TextBlock
        {
            Text = "Leave blank to use the original title.",
            Foreground = muted,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        var boxShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = box,
        };
        Grid.SetRow(boxShell, 2);
        grid.Children.Add(boxShell);

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = InlineModalButton("Cancel", foreground, line, surface, accentSoft, selectedBorder, selected, primary: false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => CloseInlineModal();
        var save = InlineModalButton("Save", foreground, line, surface, accentSoft, selectedBorder, selected, primary: true);
        save.Click += (_, _) => CommitRenameOverlay(item, box.Text);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        panel.Child = grid;
        overlay.Child = panel;
        System.Windows.Controls.Panel.SetZIndex(overlay, 900);
        root.Children.Add(overlay);
        _inlineModalOverlay = overlay;
        _suppressDeactivate = true;
        root.UpdateLayout();
        ShellLog.Info($"rename rendered elapsedMs={watch.ElapsedMilliseconds} hosted=True");

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);

        return true;
    }

    private void CommitRenameOverlay(ClipboardHistoryItem item, string value)
    {
        CloseInlineModal(showPalette: false);
        _store.Rename(item.Id, value);
        var updated = _store.GetItem(item.Id);
        _selected = null;
        LoadItems(selectFirst: false, reason: "rename");
        SelectItem(updated, "rename");
        ShellLog.Info($"rename item id={item.Id}");
        ShowPalette();
    }

    private void CloseInlineModal(bool showPalette = true)
    {
        if (_inlineModalOverlay?.Parent is System.Windows.Controls.Panel parent)
        {
            parent.Children.Remove(_inlineModalOverlay);
        }

        _inlineModalOverlay = null;
        _suppressDeactivate = false;
        if (showPalette)
        {
            ShowPalette();
            SearchBox.Focus();
        }
    }

    private static WpfButton InlineModalButton(string text, WpfBrush foreground, WpfBrush line, WpfBrush secondaryBackground, WpfBrush primaryBackground, WpfBrush primaryBorder, WpfBrush hoverBackground, bool primary)
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
            Template = ClipControlTemplates.CenterButton,
        };
        button.MouseEnter += (_, _) => { button.Background = hoverBg; button.BorderBrush = primaryBorder; };
        button.MouseLeave += (_, _) => { button.Background = idleBackground; button.BorderBrush = idleBorder; };
        return button;
    }

    private void AppendText(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
        var existing = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
        var payload = TextPayload(item);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            System.Windows.Clipboard.SetText(existing + payload);
        }
    }

    private void OpenItem(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
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
            if (TryShowOpenWithOverlay(targetPath, watch))
            {
                return;
            }

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
            picker.ContentRendered += (_, _) => ShellLog.Info($"open-with rendered elapsedMs={watch.ElapsedMilliseconds}");

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

    private bool TryShowOpenWithOverlay(string targetPath, Stopwatch watch)
    {
        if (Shell.Child is not Grid root)
        {
            return false;
        }

        CloseInlineModal(showPalette: false);
        var background = (WpfBrush)FindResource("Bg");
        var foreground = (WpfBrush)FindResource("Text");
        var muted = (WpfBrush)FindResource("Muted");
        var line = (WpfBrush)FindResource("Line");
        var surface = (WpfBrush)FindResource("Surface");
        var surface2 = (WpfBrush)FindResource("Surface2");
        var accentSoft = (WpfBrush)FindResource("AccentSoft");
        var selected = (WpfBrush)FindResource("Selected");
        var selectedBorder = (WpfBrush)FindResource("SelectedBorder");
        var apps = new List<WatcherAppChoice>();

        var overlay = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(76, 0, 0, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
        };

        var panel = new Border
        {
            Width = 620,
            Height = 520,
            Background = background,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

        var header = new Grid { Background = surface2 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = $"Open with {Path.GetFileName(targetPath)}",
            Foreground = foreground,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var close = InlineModalButton("Close", foreground, line, surface2, accentSoft, selectedBorder, selected, primary: false);
        close.Margin = new Thickness(0, 0, 12, 0);
        close.Click += (_, _) => CloseInlineModal();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var search = new WpfTextBox
        {
            Background = WpfBrushes.Transparent,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var searchShell = new Border
        {
            Background = surface,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(16, 8, 16, 7),
            Padding = new Thickness(10, 0, 10, 0),
            Child = search,
        };
        Grid.SetRow(searchShell, 1);
        shell.Children.Add(searchShell);

        var appHost = new Border
        {
            Background = background,
            Margin = new Thickness(12, 0, 8, 0),
            Child = OpenWithOverlayRow("Loading apps...", "Use Browse if the app is not listed.", foreground, muted),
        };
        Grid.SetRow(appHost, 2);
        shell.Children.Add(appHost);

        var footer = new Grid { Background = surface2 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var status = new TextBlock
        {
            Text = "Loading apps...",
            Foreground = muted,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        footer.Children.Add(status);
        var browse = InlineModalButton("Browse...", foreground, line, surface2, accentSoft, selectedBorder, selected, primary: false);
        browse.Margin = new Thickness(0, 0, 12, 0);
        browse.Click += (_, _) => BrowseOpenWithOverlay(targetPath);
        Grid.SetColumn(browse, 1);
        footer.Children.Add(browse);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);

        panel.Child = shell;
        overlay.Child = panel;
        System.Windows.Controls.Panel.SetZIndex(overlay, 900);
        root.Children.Add(overlay);
        _inlineModalOverlay = overlay;
        _suppressDeactivate = true;
        root.UpdateLayout();
        ShellLog.Info($"open-with rendered elapsedMs={watch.ElapsedMilliseconds} hosted=True");

        WpfListBox? appList = null;
        search.TextChanged += (_, _) => Render();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseInlineModal();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Accept();
                e.Handled = true;
            }
        };
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            search.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            appList = new WpfListBox
            {
                Background = background,
                Foreground = foreground,
                BorderThickness = new Thickness(0),
            };
            appList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            appList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            appList.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Accept();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    CloseInlineModal();
                    e.Handled = true;
                }
            };
            appList.MouseDoubleClick += (_, _) => Accept();
            appHost.Child = appList;
            Render();
            _ = LoadOpenWithOverlayAppsAsync(targetPath, loaded =>
            {
                apps = loaded;
                status.Text = $"{apps.Count} apps";
                Render();
            });
        }), System.Windows.Threading.DispatcherPriority.Background);

        return true;

        void Render()
        {
            if (appList is null)
            {
                return;
            }

            var query = search.Text.Trim();
            var visibleApps = apps
                .Where(app => string.IsNullOrWhiteSpace(query) ||
                    app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (app.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                    (app.AppUserModelId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
                .OrderByDescending(app => app.IsDefault)
                .ThenByDescending(app => app.IsRecent)
                .ThenBy(app => app.Name)
                .Take(80)
                .ToList();

            appList.Items.Clear();
            if (apps.Count == 0)
            {
                appList.Items.Add(new WpfListBoxItem
                {
                    Content = OpenWithOverlayRow("Loading apps...", "Use Browse if the app is not listed.", foreground, muted),
                    Foreground = muted,
                    IsEnabled = false,
                });
                return;
            }

            foreach (var app in visibleApps)
            {
                appList.Items.Add(new WpfListBoxItem
                {
                    Tag = app,
                    Content = OpenWithOverlayRow(app.Name, app.IsDefault ? "Default app" : app.Source, foreground, muted),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(4, 1, 4, 1),
                    Background = WpfBrushes.Transparent,
                    Foreground = foreground,
                });
            }

            if (appList.Items.Count > 0)
            {
                appList.SelectedIndex = 0;
            }
        }

        void Accept()
        {
            if (appList?.SelectedItem is not WpfListBoxItem { Tag: WatcherAppChoice app })
            {
                return;
            }

            LaunchOpenWithOverlay(targetPath, app);
        }
    }

    private async Task LoadOpenWithOverlayAppsAsync(string targetPath, Action<List<WatcherAppChoice>> apply)
    {
        var loadWatch = Stopwatch.StartNew();
        try
        {
            ShellLog.Info($"open-with async load started path={targetPath}");
            var apps = await Task.Run(() => WatcherAppDiscovery.GetApps(targetPath).ToList());
            apply(apps);
            ShellLog.Info($"open-with async load completed count={apps.Count} elapsedMs={loadWatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with async load failed elapsedMs={loadWatch.ElapsedMilliseconds}");
        }
    }

    private static StackPanel OpenWithOverlayRow(string title, string subtitle, WpfBrush foreground, WpfBrush muted)
    {
        var panel = new StackPanel { Orientation = WpfOrientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, Foreground = foreground, FontSize = 13, FontWeight = FontWeights.Medium });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        return panel;
    }

    private void LaunchOpenWithOverlay(string targetPath, WatcherAppChoice app)
    {
        try
        {
            CloseInlineModal(showPalette: false);
            WatcherAppLauncher.OpenWith(targetPath, app);
            ShellLog.Info($"open-with completed path={targetPath} selected={app.Name} hosted=True");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"open-with launch failed path={targetPath}");
            ShowToast("Open With failed. Log saved.");
        }
        finally
        {
            ShowPalette();
        }
    }

    private void BrowseOpenWithOverlay(string targetPath)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Applications|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            LaunchOpenWithOverlay(targetPath, new WatcherAppChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Browse"));
        }
    }

    private void ShowInFileExplorer(ClipboardHistoryItem item)
    {
        var path = ClipboardItemRevealTarget.GetPath(item);
        try
        {
            if (!FileExplorerReveal.TryReveal(path))
            {
                ShowToast("Path not found");
                return;
            }

            ShellLog.Info($"show in file explorer id={item.Id} path={path}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, $"show in file explorer failed id={item.Id} path={path}");
            ShowToast("Could not open File Explorer");
        }
    }

    private void ShareItem(ClipboardHistoryItem item)
    {
        item = FullTextItem(item);
        ClipboardSharePayload? payload = null;
        try
        {
            if (!WindowsShareService.IsSupported())
            {
                ShowToast("Sharing is not available on this PC.");
                return;
            }

            payload = ClipboardSharePayload.Create(item);
            var hwnd = new WindowInteropHelper(this).Handle;
            WindowsShareService.ShowShareUI(
                hwnd,
                item,
                payload,
                ShareTitle(item),
                ShareDescription(item),
                ex => ShellLog.Error(ex, $"share data failed id={item.Id}"));
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
        item = FullTextItem(item);
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
        item = FullTextItem(item);
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

    private static Task<CoreWebView2Environment> CreateWebView2EnvironmentAsync()
    {
        Directory.CreateDirectory(ClipStoragePaths.WebView2UserDataFolderPath);
        return CoreWebView2Environment.CreateAsync(userDataFolder: ClipStoragePaths.WebView2UserDataFolderPath);
    }

    private FrameworkElement EnsureHtmlPreview()
    {
        if (_htmlPreview is not null)
        {
            return _htmlPreview;
        }

        var htmlPreview = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            Visibility = Visibility.Collapsed,
            DefaultBackgroundColor = ToDrawingColor((SolidColorBrush)FindResource("Bg")),
        };
        _htmlPreview = htmlPreview;
        _setHtmlPreviewBackground = color => htmlPreview.DefaultBackgroundColor = color;
        PreviewHost.Children.Add(_htmlPreview);
        return _htmlPreview;
    }

    private async Task ShowHtmlPreviewAsync(string path)
    {
        var htmlPreview = (Microsoft.Web.WebView2.Wpf.WebView2)EnsureHtmlPreview();
        htmlPreview.Visibility = Visibility.Visible;
        await htmlPreview.EnsureCoreWebView2Async(await CreateWebView2EnvironmentAsync());
        htmlPreview.Source = new Uri(path);
    }

    // Tears down the WebView2 (and its Chromium processes) so nothing browser-related
    // lingers while the palette is hidden. Recreated lazily on the next HTML preview.
    private void DisposeHtmlPreview()
    {
        if (_htmlPreview is null)
        {
            return;
        }

        try
        {
            PreviewHost.Children.Remove(_htmlPreview);
            (_htmlPreview as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "html preview dispose failed");
        }
        finally
        {
            _htmlPreview = null;
            _setHtmlPreviewBackground = null;
        }
    }

    private void HidePreviews()
    {
        CloseExpandedImage();
        TextPreview.Visibility = Visibility.Collapsed;
        ImagePreview.Visibility = Visibility.Collapsed;
        ExpandImageButton.Visibility = Visibility.Collapsed;
        if (_htmlPreview is not null)
        {
            _htmlPreview.Visibility = Visibility.Collapsed;
        }

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

    private void PositionOnMouseScreen(bool log = true)
    {
        // Center the palette on whichever monitor the mouse is on, using raw Win32 screen pixels
        // for BOTH the monitor work area and the window size. Staying in one coordinate space makes
        // it work across monitors with different display scaling (DPI). The previous WPF DIP-transform
        // math used the window's current monitor scaling for a DIFFERENT target monitor, so on a
        // differently-scaled second screen the window landed off-screen and never appeared.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var windowRect))
        {
            return;
        }

        var work = info.Work;
        var workWidth = work.Right - work.Left;
        var workHeight = work.Bottom - work.Top;
        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;
        var x = work.Left + Math.Max(0, (workWidth - windowWidth) / 2);
        var y = work.Top + Math.Max(0, (workHeight - windowHeight) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate);
        if (log)
        {
            ShellLog.Info($"position(win32) cursor={cursor.X},{cursor.Y} work={work.Left},{work.Top} {workWidth}x{workHeight} win={windowWidth}x{windowHeight} -> {x},{y}");
        }
    }

    private static void WarmMouseScreenCache()
    {
        _ = WorkingAreaForMouse(Forms.Control.MousePosition);
    }

    private static System.Drawing.Rectangle WorkingAreaForMouse(System.Drawing.Point mouse)
    {
        if (_hasCachedMouseScreenWorkingArea && _cachedMouseScreenWorkingArea.Contains(mouse))
        {
            return _cachedMouseScreenWorkingArea;
        }

        _cachedMouseScreenWorkingArea = Forms.Screen.FromPoint(mouse).WorkingArea;
        _hasCachedMouseScreenWorkingArea = true;
        return _cachedMouseScreenWorkingArea;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
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

    private void OpenSettingsForDebug()
    {
        DebugOpenSettings = false;
        OpenSettingsInternal(showPaletteOnClose: false);
    }

    public void PasteLatestFromTray()
    {
        var item = LatestClipboardItem();
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

    private ClipboardHistoryItem? LatestClipboardItem()
    {
        var items = _allItems;
        if (items.Count == 0 || !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            items = _store.QueryItemSummaries();
            _allItems = items;
        }

        return items.OrderByDescending(i => i.LastCopiedAt).FirstOrDefault();
    }

    private void OpenSettingsInternal(bool showPaletteOnClose)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            ShellLog.Info($"settings opening showPaletteOnClose={showPaletteOnClose}");
            SettingsWindow? settings = null;
            Border? overlay = null;
            if (Shell.Child is Grid)
            {
                TryTakePrewarmedHostedSettings(out settings, out overlay);
            }

            settings ??= CreateSettingsWindow();

            if (TryShowHostedSettings(settings, showPaletteOnClose, watch, overlay))
            {
                return;
            }

            _suppressDeactivate = true;
            settings.ContentRendered += (_, _) => ShellLog.Info($"settings rendered elapsedMs={watch.ElapsedMilliseconds} showPaletteOnClose={showPaletteOnClose}");
            settings.Closed += (_, _) => CloseStandaloneSettings(showPaletteOnClose);
            settings.Show();
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings failed");
            _suppressDeactivate = false;
            ShowToast("Settings failed. Log saved.");
        }
    }

    private SettingsWindow CreateSettingsWindow() => new(_settings, _lastUpdateStatus, ApplyTheme, RefreshClipboardManagerTextTheme, ApplyAppIcon, ApplyRunAtStartup, ApplyHistoryLimit, ApplyMaxItemSize, ApplyUpdateSettings, CheckForUpdatesFromSettings, InstallUpdateAsync, OpenDataFolder, OpenDebugLog, ClearHistory, ChangeClipboardFolder, ResetClipboardFolder, ApplyHotkeys, ApplyPrivacy, ApplyDefaultPasteFormat, ResetAllSettings, CurrentSettingsPalette)
    {
        Owner = this,
    };

    private bool TryTakePrewarmedHostedSettings(out SettingsWindow? settings, out Border? overlay)
    {
        settings = null;
        overlay = null;
        if (!_prewarmedSettingsReady || _prewarmedSettings is null || _prewarmedSettingsOverlay is null)
        {
            return false;
        }

        settings = _prewarmedSettings;
        overlay = _prewarmedSettingsOverlay;
        _prewarmedSettings = null;
        _prewarmedSettingsOverlay = null;
        _prewarmedSettingsReady = false;
        ShellLog.Info("settings using prewarmed panel");
        return true;
    }

    private bool TryShowHostedSettings(SettingsWindow settings, bool showPaletteOnClose, Stopwatch watch, Border? preparedOverlay = null)
    {
        if (Shell.Child is not Grid host)
        {
            return false;
        }

        CloseHostedSettings(logClose: false);

        var overlay = preparedOverlay ?? CreateHostedSettingsOverlay(settings);
        overlay.Opacity = 1;
        overlay.IsHitTestVisible = true;

        _hostedSettings = settings;
        _settingsOverlay = overlay;
        _settingsOverlayKeepPaletteOnClose = showPaletteOnClose;
        _suppressDeactivate = true;

        if (!ReferenceEquals(overlay.Parent, host))
        {
            host.Children.Add(overlay);
        }

        if (!IsVisible || Opacity == 0 || !IsHitTestVisible)
        {
            ShowPalette(loadItems: false);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShellLog.Info($"settings rendered elapsedMs={watch.ElapsedMilliseconds} showPaletteOnClose={showPaletteOnClose} hosted=True");
            _ = Dispatcher.BeginInvoke(new Action(() => overlay.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }), System.Windows.Threading.DispatcherPriority.Render);
        return true;
    }

    private Border CreateHostedSettingsOverlay(SettingsWindow settings)
    {
        var content = settings.DetachForHost(CloseHostedSettings);
        content.Width = 720;
        content.Height = 500;
        content.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;

        var overlay = new Border
        {
            Background = WpfBrushes.Transparent,
            Child = content,
            Focusable = true,
        };
        Grid.SetRowSpan(overlay, 4);
        System.Windows.Controls.Panel.SetZIndex(overlay, 1000);
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseHostedSettings();
                e.Handled = true;
            }
        };
        return overlay;
    }

    private void CloseHostedSettings() => CloseHostedSettings(logClose: true);

    private void CloseHostedSettings(bool logClose)
    {
        var hadOverlay = _settingsOverlay is not null;
        if (_settingsOverlay is not null && Shell.Child is Grid host)
        {
            host.Children.Remove(_settingsOverlay);
        }

        _settingsOverlay = null;
        _hostedSettings = null;
        var keepPalette = _settingsOverlayKeepPaletteOnClose;
        _settingsOverlayKeepPaletteOnClose = false;

        if (!hadOverlay)
        {
            return;
        }

        _suppressDeactivate = false;

        if (logClose)
        {
            ShellLog.Info("settings closed");
        }

        if (!keepPalette && !_isClosing)
        {
            ConcealPalette("settings-closed");
        }

        if (!_isClosing)
        {
            PrewarmHostedSettingsSoon();
        }
    }

    private void CloseStandaloneSettings(bool showPaletteOnClose)
    {
        _suppressDeactivate = false;
        ShellLog.Info("settings closed");
        if (showPaletteOnClose && !_isClosing)
        {
            ShowPalette();
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
        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = true;
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
            _startupUpdateCheckTimer.Stop();
            _updateCheckTimer.Stop();
            ShellLog.Info("update check schedule stopped");
        }
    }

    private void CheckForUpdatesFromSettings(Action<ClipUpdateStatus> updateStatus)
    {
        _ = CheckForUpdatesAsync(showToastWhenCurrent: true, updateStatus);
    }

    private async Task CheckForUpdatesAsync(bool showToastWhenCurrent, Action<ClipUpdateStatus>? updateStatus = null, bool promptIfAvailable = false, bool nativeNotify = false)
    {
        if (_updateCheckInProgress)
        {
            ShellLog.Info("update check skipped already running");
            return;
        }

        _updateCheckInProgress = true;
        try
        {
            if (nativeNotify)
            {
                UpdateNotification?.Invoke("Checking for updates…");
            }

            ShellLog.Info("update check started");
            var status = await _updates.CheckAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                _lastUpdateStatus = status;
                updateStatus?.Invoke(status);
                ShellLog.Info($"update check completed state={status.State} current={status.CurrentVersion} latest={status.LatestVersion ?? "none"} download={status.DownloadUrl ?? "none"}");

                if (status.State == "Update available")
                {
                    if (nativeNotify) { UpdateNotification?.Invoke(status.Message); } else { ShowToast(status.Message); }
                    if (promptIfAvailable)
                    {
                        PromptForKnownUpdate();
                    }
                }
                else if (showToastWhenCurrent)
                {
                    if (nativeNotify) { UpdateNotification?.Invoke(status.Message); } else { ShowToast(status.Message); }
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

        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = true;
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
        ResetHistorySummaryCache();
        ShellLog.Info($"clipboard folder changed path={_store.ContentRootPath}");
        ShowToast("Clipboard folder updated");
    }

    private void ResetClipboardFolder()
    {
        _settings.ClipboardFolderPath = null;
        _settings.Save();
        _store.SetContentRootPath(_settings.EffectiveClipboardFolderPath());
        ResetHistorySummaryCache();
        ShellLog.Info($"clipboard folder reset path={_store.ContentRootPath}");
        ShowToast("Clipboard folder reset");
    }

    private void ResetHistorySummaryCache()
    {
        ClearRecentFirstPaintPreload();
        _allItems = [];
        _selected = null;
        _historySummariesPreloaded = false;
        _itemsDirtySinceRender = true;
        _historyPreloadTimer.Stop();
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
        _allItems = _store.QueryItemSummaries();
        _historySummariesPreloaded = true;
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
        _openHotkeyUnavailable = false;

        if (_debugLogHotkeyRegistered)
        {
            UnregisterHotKey(hwnd, DebugLogHotkeyId);
            _debugLogHotkeyRegistered = false;
        }
        _debugLogHotkeyUnavailable = false;

        EnsureHotkeyRegistered(reason);
    }

    private void ApplyTheme(ClipThemePreference preference) => ApplyTheme(preference, save: true);

    private void ApplyTheme(ClipThemePreference preference, bool save)
    {
        ClearPrewarmedHostedSettings();
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
        ApplySystemAccentBrushes(useDark);
        Background = (WpfBrush)FindResource("Bg");
        _setHtmlPreviewBackground?.Invoke(ToDrawingColor((SolidColorBrush)FindResource("Bg")));

        TextPreview.Foreground = (WpfBrush)FindResource("Text");
        TextPreview.CaretBrush = (WpfBrush)FindResource("TextCursor");
        if (TitleText is not null) { TitleText.Foreground = (WpfBrush)FindResource("Text"); }
        if (SubTitleText is not null) { SubTitleText.Foreground = (WpfBrush)FindResource("Muted"); }
        if (save)
        {
            Dispatcher.BeginInvoke(RefreshChromeIconsIfReady, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        else if (_chromeIconsReady)
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
        AppHeaderIcon.Source = RenderAppTileIcon(preference);
        _appHeaderIconReady = true;
        ShellLog.Info($"window header icon applied icon={preference}");
    }

    private void EnsureAppHeaderIcon()
    {
        if (_appHeaderIconReady)
        {
            return;
        }

        ApplyWindowTitleIcon(_settings.AppIcon);
    }

    private void RefreshChromeIcons()
    {
        SettingsIcon.Source = RenderChromeIcon(ChromeIconKind.Settings, "Muted2");
        DateDropIcon.Source = RenderChromeIcon(ChromeIconKind.ChevronDown, _kindFilter == "all" ? "Text" : "Muted2");
        FileDropIcon.Source = RenderChromeIcon(ChromeIconKind.ChevronDown, _kindFilter == "files" ? "Text" : "Muted2");
        ExpandImageIcon.Source = RenderChromeIcon(ChromeIconKind.Expand, "Muted2");
        _chromeIconsReady = true;
    }

    private void EnsureChromeIcons()
    {
        if (_chromeIconsReady)
        {
            return;
        }

        RefreshChromeIcons();
    }

    private void RefreshChromeIconsIfReady()
    {
        if (_chromeIconsReady)
        {
            RefreshChromeIcons();
        }
    }

    private enum ChromeIconKind
    {
        Settings,
        ChevronDown,
        Expand,
    }

    private enum ItemVectorIconKind
    {
        Text,
        Link,
        Folder,
        Image,
        File,
    }

    private ImageSource RenderChromeIcon(ChromeIconKind kind, string colorKey)
    {
        var color = ((SolidColorBrush)FindResource(colorKey)).Color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new WpfPen(brush, kind == ChromeIconKind.Settings ? 1.8 : 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(WpfBrushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));

        switch (kind)
        {
            case ChromeIconKind.Settings:
                drawing.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new System.Windows.Point(12, 12), 4.2, 4.2)));
                for (var i = 0; i < 8; i++)
                {
                    var angle = (Math.PI / 4) * i;
                    drawing.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(
                        new System.Windows.Point(12 + Math.Cos(angle) * 7.1, 12 + Math.Sin(angle) * 7.1),
                        new System.Windows.Point(12 + Math.Cos(angle) * 9.6, 12 + Math.Sin(angle) * 9.6))));
                }
                break;

            case ChromeIconKind.ChevronDown:
                drawing.Children.Add(new GeometryDrawing(null, pen, PolylineGeometry(
                    new System.Windows.Point(6.5, 9),
                    new System.Windows.Point(12, 14.5),
                    new System.Windows.Point(17.5, 9))));
                break;

            case ChromeIconKind.Expand:
                AddLine(drawing, pen, 5, 5, 10, 5);
                AddLine(drawing, pen, 5, 5, 5, 10);
                AddLine(drawing, pen, 5, 5, 10, 10);
                AddLine(drawing, pen, 19, 5, 14, 5);
                AddLine(drawing, pen, 19, 5, 19, 10);
                AddLine(drawing, pen, 19, 5, 14, 10);
                AddLine(drawing, pen, 5, 19, 10, 19);
                AddLine(drawing, pen, 5, 19, 5, 14);
                AddLine(drawing, pen, 5, 19, 10, 14);
                AddLine(drawing, pen, 19, 19, 14, 19);
                AddLine(drawing, pen, 19, 19, 19, 14);
                AddLine(drawing, pen, 19, 19, 14, 14);
                break;
        }

        drawing.Freeze();
        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    internal static ImageSource RenderAppTileIcon(AppIconPreference preference)
    {
        var cacheKey = $"app-tile|{preference}";
        lock (SvgCacheGate)
        {
            if (SvgImageCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var dark = preference == AppIconPreference.Dark;
        var background = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0x21, 0x1F, 0x1C)
            : System.Windows.Media.Color.FromRgb(0xF4, 0xF0, 0xE6));
        var strokeBrush = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0xF4, 0xF0, 0xE6)
            : System.Windows.Media.Color.FromRgb(0x1A, 0x18, 0x16));
        background.Freeze();
        strokeBrush.Freeze();

        var pen = new WpfPen(strokeBrush, 5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(background, null, new RectangleGeometry(new Rect(0, 0, 72, 72), 16.2, 16.2)));
        drawing.Children.Add(new GeometryDrawing(null, pen, PaperclipIconGeometry()));
        drawing.Freeze();

        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();

        lock (SvgCacheGate)
        {
            SvgImageCache[cacheKey] = image;
        }

        return image;
    }

    private static StreamGeometry PaperclipIconGeometry()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(48, 16), isFilled: false, isClosed: false);
            context.LineTo(new System.Windows.Point(48, 50), isStroked: true, isSmoothJoin: true);
            context.ArcTo(new System.Windows.Point(32, 50), new System.Windows.Size(8, 8), rotationAngle: 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            context.LineTo(new System.Windows.Point(32, 24), isStroked: true, isSmoothJoin: true);
            context.ArcTo(new System.Windows.Point(40, 24), new System.Windows.Size(4, 4), rotationAngle: 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            context.LineTo(new System.Windows.Point(40, 46), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private ImageSource RenderItemVectorIcon(ItemVectorIconKind kind, int size)
    {
        var color = BrushHex("Muted2");
        var cacheKey = $"item-vector|{kind}|{size}|{color}";
        lock (SvgCacheGate)
        {
            if (SvgImageCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var stroke = new SolidColorBrush(((SolidColorBrush)FindResource("Muted2")).Color);
        stroke.Freeze();
        var fill = new SolidColorBrush(((SolidColorBrush)FindResource("Muted2")).Color) { Opacity = 0.16 };
        fill.Freeze();
        var pen = new WpfPen(stroke, 1.8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(WpfBrushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));

        switch (kind)
        {
            case ItemVectorIconKind.Text:
                AddDocumentOutline(drawing, pen, fill);
                AddLine(drawing, pen, 8.5, 12, 16.5, 12);
                AddLine(drawing, pen, 8.5, 15, 17, 15);
                AddLine(drawing, pen, 8.5, 18, 14.5, 18);
                break;

            case ItemVectorIconKind.Link:
                drawing.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(3.8, 8.3, 9.4, 7.4), 3.7, 3.7)));
                drawing.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new Rect(10.8, 8.3, 9.4, 7.4), 3.7, 3.7)));
                AddLine(drawing, pen, 9.2, 12, 14.8, 12);
                break;

            case ItemVectorIconKind.Folder:
                drawing.Children.Add(new GeometryDrawing(fill, pen, PolygonGeometry(
                    new System.Windows.Point(3.5, 7.5),
                    new System.Windows.Point(8.8, 7.5),
                    new System.Windows.Point(11, 9.7),
                    new System.Windows.Point(20.5, 9.7),
                    new System.Windows.Point(20.5, 18.5),
                    new System.Windows.Point(3.5, 18.5))));
                break;

            case ItemVectorIconKind.Image:
                drawing.Children.Add(new GeometryDrawing(fill, pen, new RectangleGeometry(new Rect(4.5, 5.5, 15, 13), 2.8, 2.8)));
                drawing.Children.Add(new GeometryDrawing(stroke, null, new EllipseGeometry(new System.Windows.Point(14.8, 9.2), 1.25, 1.25)));
                drawing.Children.Add(new GeometryDrawing(null, pen, PolylineGeometry(
                    new System.Windows.Point(6.8, 16),
                    new System.Windows.Point(10.2, 12.6),
                    new System.Windows.Point(12.7, 15.1),
                    new System.Windows.Point(14.2, 13.6),
                    new System.Windows.Point(17.4, 16.8))));
                break;

            case ItemVectorIconKind.File:
                AddDocumentOutline(drawing, pen, fill);
                AddLine(drawing, pen, 8.5, 14, 16, 14);
                AddLine(drawing, pen, 8.5, 17, 14, 17);
                break;
        }

        drawing.Freeze();
        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();

        lock (SvgCacheGate)
        {
            SvgImageCache[cacheKey] = image;
        }

        return image;
    }

    private static void AddDocumentOutline(DrawingGroup drawing, WpfPen pen, WpfBrush fill)
    {
        drawing.Children.Add(new GeometryDrawing(fill, pen, PolygonGeometry(
            new System.Windows.Point(6.5, 3.8),
            new System.Windows.Point(14.7, 3.8),
            new System.Windows.Point(18.5, 7.6),
            new System.Windows.Point(18.5, 20.2),
            new System.Windows.Point(6.5, 20.2))));
        AddLine(drawing, pen, 14.7, 3.8, 14.7, 8);
        AddLine(drawing, pen, 14.7, 8, 18.5, 8);
    }

    private static void AddLine(DrawingGroup drawing, WpfPen pen, double x1, double y1, double x2, double y2)
    {
        drawing.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new System.Windows.Point(x1, y1), new System.Windows.Point(x2, y2))));
    }

    private static StreamGeometry PolylineGeometry(params System.Windows.Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Length; i++)
            {
                context.LineTo(points[i], isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry PolygonGeometry(params System.Windows.Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            for (var i = 1; i < points.Length; i++)
            {
                context.LineTo(points[i], isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
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

    private void SetBrushColor(string key, System.Windows.Media.Color color)
    {
        if (Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
        }
        else
        {
            Resources[key] = new SolidColorBrush(color);
        }
    }

    // Reads the user's chosen Windows accent color (HKCU\...\DWM\AccentColor, a DWORD stored as
    // 0xAABBGGRR). Returns null if unavailable so callers keep their themed fallback accent.
    internal static System.Windows.Media.Color? GetWindowsAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int raw)
            {
                var abgr = unchecked((uint)raw);
                return System.Windows.Media.Color.FromRgb(
                    (byte)(abgr & 0xFF),
                    (byte)((abgr >> 8) & 0xFF),
                    (byte)((abgr >> 16) & 0xFF));
            }
        }
        catch
        {
        }

        return null;
    }

    // Mixes an accent color over a base surface color (overlayAmount 0..1) to build subtle tints
    // for the selected-row background and active-filter chip.
    internal static System.Windows.Media.Color BlendColors(System.Windows.Media.Color baseColor, System.Windows.Media.Color overlay, double overlayAmount)
    {
        overlayAmount = Math.Clamp(overlayAmount, 0, 1);
        byte Mix(byte b, byte o) => (byte)Math.Round((b * (1 - overlayAmount)) + (o * overlayAmount));
        return System.Windows.Media.Color.FromRgb(Mix(baseColor.R, overlay.R), Mix(baseColor.G, overlay.G), Mix(baseColor.B, overlay.B));
    }

    // Repaints the selection/accent brushes from the live Windows accent so the highlighted row and
    // the active filter chip match the user's system accent. No-ops (keeps the themed fallback) when
    // the accent can't be read.
    private void ApplySystemAccentBrushes(bool useDark)
    {
        var accent = GetWindowsAccentColor();
        if (accent is null)
        {
            return;
        }

        var ac = accent.Value;
        var listBg = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(useDark ? "#272727" : "#EDEDED");
        SetBrushColor("Accent", ac);
        SetBrushColor("SelectedBorder", ac);
        SetBrushColor("Selected", BlendColors(listBg, ac, useDark ? 0.20 : 0.16));
        SetBrushColor("AccentSoft", BlendColors(listBg, ac, useDark ? 0.26 : 0.20));
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
        AppendDeferredRowsIfNeeded();
        e.Handled = true;
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        AppendDeferredRowsIfNeeded();
    }

    private void OnDateDropClick(object sender, RoutedEventArgs e)
    {
        var actions = new[] { ("All", "all"), ("Today", "today"), ("Yesterday", "yesterday"), ("This week", "week"), ("This month", "month"), ("This year", "year"), ("Older", "older") }
            .Select(pair => new MenuAction(pair.Item1, () =>
            {
                _dateFilter = pair.Item2;
                var visibleItems = RenderItems("date-filter");
                SelectItem(visibleItems.FirstOrDefault(), "date-filter");
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
            var visibleItems = RenderItems("file-filter");
            SelectItem(visibleItems.FirstOrDefault(), "file-filter");
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

        // Do NOT auto-hide on focus loss. In remote/RDP/RustDesk sessions the palette frequently
        // can't hold foreground, so concealing here made it flash open and vanish (looked like
        // "Alt+V doesn't work"). The palette still dismisses via Escape, a click outside it
        // (HideIfMousePressedOutsidePalette), or after a paste.
        ShellLog.Info("deactivate ignored (palette stays until escape/outside-click/paste)");
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
        RefreshChromeIconsIfReady();
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
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static int CountWords(string text) => Regex.Matches(text, @"\b[\w']+\b").Count;
    private static long? SizeOf(string? path) => File.Exists(path) ? new FileInfo(path).Length : null;
    private static string FormatBytes(long? bytes) => bytes is null ? "" : bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes / 1024d / 1024d:0.#} MB";
    private static string ContentType(ClipboardHistoryItem item) => item.Kind == ClipboardItemKind.Link ? "Link" : item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1 && Directory.Exists(item.FilePaths[0]) ? "Folder" : item.Kind.ToString();

    private static string DateKey(ClipboardHistoryItem item)
    {
        return DateKey(item, DateTime.Today);
    }

    private static string DateKey(ClipboardHistoryItem item, DateTime today)
    {
        var copied = item.LastCopiedAt.LocalDateTime.Date;
        if (copied == today) return "today";
        if (copied == today.AddDays(-1)) return "yesterday";
        if (copied >= today.AddDays(-7)) return "week";
        if (copied.Year == today.Year && copied.Month == today.Month) return "month";
        return copied.Year == today.Year ? "year" : "older";
    }

    private static void SortByLastCopied(List<ClipboardHistoryItem> items)
    {
        items.Sort((left, right) => right.LastCopiedAt.CompareTo(left.LastCopiedAt));
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

    private ImageSource IconFor(ClipboardHistoryItem item, int size, bool preferRichPreview = true)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Color)
            {
                return RenderColorSwatch(TextPayload(item), size);
            }

            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
            {
                return preferRichPreview
                    ? LoadCachedBitmap(item.AssetPath, RowIconDecodePixels)
                    : RenderItemVectorIcon(ItemVectorIconKind.Image, size);
            }

            if (item.Kind == ClipboardItemKind.Image)
            {
                return RenderItemVectorIcon(ItemVectorIconKind.Image, size);
            }

            if (item.Kind == ClipboardItemKind.Link) return RenderItemVectorIcon(ItemVectorIconKind.Link, size);
            if (item.Kind == ClipboardItemKind.Text) return RenderItemVectorIcon(ItemVectorIconKind.Text, size);
            if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count == 1)
            {
                var path = item.FilePaths[0];
                if (Directory.Exists(path)) return RenderItemVectorIcon(ItemVectorIconKind.Folder, size);
                if (!preferRichPreview)
                {
                    return IsImageFile(Path.GetExtension(path).ToLowerInvariant())
                        ? RenderItemVectorIcon(ItemVectorIconKind.Image, size)
                        : RenderItemVectorIcon(ItemVectorIconKind.File, size);
                }

                if (File.Exists(path) && IsImageFile(Path.GetExtension(path).ToLowerInvariant()))
                {
                    return LoadCachedBitmap(path, RowIconDecodePixels);
                }

                return RenderFileSvg(path, size);
            }

            return RenderItemVectorIcon(ItemVectorIconKind.File, size);
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
                var cacheKey = RasterCacheKey("source", item.SourceApplicationPath, 32);
                if (TryGetCachedRaster(cacheKey, out var cached))
                {
                    return cached;
                }

                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(item.SourceApplicationPath);
                if (icon is not null)
                {
                    using var bitmap = icon.ToBitmap();
                    return RememberRaster(cacheKey, BitmapFromDrawingImage(bitmap));
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
        var cacheKey = $"file-icon|{ext}|{size}";
        if (TryGetCachedRaster(cacheKey, out var cached))
        {
            return cached;
        }

        ImageSource source;
        if (ShouldUseWindowsFileIcon(ext))
        {
            var windowsIcon = WatcherShellIconReader.TryGetIcon(path, large: size >= 48);
            if (windowsIcon is not null)
            {
                using (windowsIcon)
                {
                    source = BitmapFromDrawingImage(windowsIcon);
                    return RememberRaster(cacheKey, source);
                }
            }
        }

        var name = string.IsNullOrWhiteSpace(ext) ? "file-60.svg" : $"file-icon-{ext}.svg";
        source = File.Exists(AssetIconPath(name)) ? RenderSvg(name, size) : RenderItemVectorIcon(ItemVectorIconKind.File, size);
        return RememberRaster(cacheKey, source);
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
        var cacheKey = $"generated-file|{ext}|{size}";
        if (TryGetCachedRaster(cacheKey, out var cached))
        {
            return cached;
        }

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
        return RememberRaster(cacheKey, BitmapFromDrawingImage(bitmap));
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
        var cacheKey = $"color|{hex}|{size}";
        if (TryGetCachedRaster(cacheKey, out var cached))
        {
            return cached;
        }

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
        return RememberRaster(cacheKey, BitmapFromDrawingImage(bitmap));
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

    private static ImageSource LoadCachedBitmap(string path, int decodePixels)
    {
        if (!ShouldCacheBitmap(decodePixels))
        {
            return LoadBitmap(path, decodePixels);
        }

        var cacheKey = RasterCacheKey("bitmap", path, decodePixels);
        if (TryGetCachedRaster(cacheKey, out var cached))
        {
            return cached;
        }

        return RememberRaster(cacheKey, LoadBitmap(path, decodePixels));
    }

    private static bool ShouldCacheBitmap(int decodePixels) => decodePixels <= RowIconDecodePixels;

    private static string RasterCacheKey(string prefix, string path, int size)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            var stamp = info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "missing";
            return $"{prefix}|{fullPath}|{size}|{stamp}";
        }
        catch
        {
            return $"{prefix}|{path}|{size}";
        }
    }

    private static bool TryGetCachedRaster(string cacheKey, out ImageSource source)
    {
        lock (RasterImageCacheGate)
        {
            return RasterImageCache.TryGetValue(cacheKey, out source!);
        }
    }

    private static ImageSource RememberRaster(string cacheKey, ImageSource source)
    {
        lock (RasterImageCacheGate)
        {
            if (RasterImageCache.Count >= MaxCachedRasterImages)
            {
                RasterImageCache.Clear();
            }

            RasterImageCache[cacheKey] = source;
        }

        return source;
    }

    private static BitmapImage LoadBitmap(string path, int? decodePixels = null)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixels is > 0)
        {
            bitmap.DecodePixelWidth = decodePixels.Value;
        }

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

        return ThinScrollBarStyleCache.GetOrAdd(hex, static key => (Style)XamlReader.Parse($$"""
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
                    <Border Background="{{key}}" CornerRadius="3" Margin="1"/>
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
"""));
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

    private static string WindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            var className = new StringBuilder(256);
            return GetClassName(hwnd, className, className.Capacity) > 0 ? className.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsFileExplorerWindowClass(string? className)
    {
        return string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "ExploreWClass", StringComparison.OrdinalIgnoreCase);
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

    private static bool CouldNeedNoActivatePalette(IntPtr hwnd)
    {
        return CouldNeedNoActivatePalette(hwnd, WindowTitle(hwnd));
    }

    private static bool CouldNeedNoActivatePalette(IntPtr hwnd, string windowTitle)
    {
        if (!windowTitle.Contains("Google Earth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var processName = TryGetProcessNameForWindow(hwnd);
        if (!string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
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
    private const int ShowWindowShow = 5;
    private const int ShowWindowRestore = 9;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyEnter = 0x0D;
    private const ushort VirtualKeyV = 0x56;
    private static readonly IntPtr HwndTopmost = new(-1);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
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

internal static class ClipControlTemplates
{
    private static readonly Lazy<ControlTemplate> PaddedButtonCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center"
                      VerticalAlignment="Center"
                      Margin="{TemplateBinding Padding}"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.85"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""));

    private static readonly Lazy<ControlTemplate> CenterButtonCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
  <Border x:Name="Root"
          Background="{TemplateBinding Background}"
          BorderBrush="{TemplateBinding BorderBrush}"
          BorderThickness="{TemplateBinding BorderThickness}"
          CornerRadius="6">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsPressed" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.85"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
"""));

    public static ControlTemplate PaddedButton => PaddedButtonCache.Value;

    public static ControlTemplate CenterButton => CenterButtonCache.Value;
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
            Template = ClipControlTemplates.PaddedButton,
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
        Loaded += (_, _) =>
        {
            _search.Focus();
            _status.Text = "Loading apps...";
            RenderApps();
            _ = Dispatcher.BeginInvoke(new Action(() => _ = LoadAppsAfterFirstPaintAsync()), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    public WatcherAppChoice? SelectedApp { get; private set; }

    private async Task LoadAppsAfterFirstPaintAsync()
    {
        LoadPersistedCache();
        if (TryGetCachedApps(_targetPath, out var cached))
        {
            _allApps = cached;
            _status.Text = $"{_allApps.Count} apps";
            RenderApps();
        }

        await LoadAppsAsync();
    }

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
            Template = ClipControlTemplates.PaddedButton,
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
            Template = ClipControlTemplates.PaddedButton,
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
    private const string DropdownIconTag = "SettingsDropdownIcon";
    private static readonly ConcurrentDictionary<string, ImageSource> DropdownChevronIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<ControlTemplate> TransparentButtonTemplateCache = new(() => (ControlTemplate)XamlReader.Parse("""
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
"""));
    private static readonly Lazy<ControlTemplate> SubtleSettingsButtonTemplateCache = new(() => (ControlTemplate)XamlReader.Parse("""
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
"""));
    private static readonly Lazy<ControlTemplate> InfoBadgeButtonTemplateCache = new(() => (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="{x:Type Button}">
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
"""));

    public static void WarmCaches()
    {
        try
        {
            _ = TransparentButtonTemplateCache.Value;
            _ = SubtleSettingsButtonTemplateCache.Value;
            _ = InfoBadgeButtonTemplateCache.Value;
            WarmDropdownIcon("#646464");
            WarmDropdownIcon("#989898");
            ShellLog.Info("settings caches warmed");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings cache warm failed");
        }
    }

    private static void WarmDropdownIcon(string hex)
    {
        DropdownChevronIconCache.GetOrAdd(hex, CreateDropdownChevronIcon);
    }

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
    private Action? _hostClose;
    private string _currentPage = "General";

    public SettingsWindow(ClipShellSettings settings, ClipUpdateStatus updateStatus, Action<ClipThemePreference> applyTheme, Action refreshClipboardManagerTextTheme, Action<AppIconPreference> applyAppIcon, Action<bool> applyRunAtStartup, Action<int?> applyHistoryLimit, Action<long?> applyMaxItemSize, Action<bool, bool> applyUpdateSettings, Action<Action<ClipUpdateStatus>> checkForUpdates, Func<ClipUpdateStatus, Task> installUpdate, Action openDataFolder, Action openDebugLog, Action<bool> clearHistory, Action<string> changeClipboardFolder, Action resetClipboardFolder, Action<ClipHotkeySettings> applyHotkeys, Action<ClipPrivacySettings> applyPrivacy, Action<PasteFormatPreference> applyDefaultPasteFormat, Action resetAllSettings, Func<SettingsPalette> paletteProvider)
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
                RequestClose();
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
                DragSettingsWindow();
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
        close.Click += (_, _) => RequestClose();
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

    public FrameworkElement DetachForHost(Action close)
    {
        _hostClose = close;
        if (Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("Settings content is not hostable.");
        }

        Content = null;
        return content;
    }

    private void RequestClose()
    {
        if (_hostClose is not null)
        {
            _hostClose();
            return;
        }

        Close();
    }

    private void DragSettingsWindow()
    {
        if (_hostClose is not null)
        {
            return;
        }

        DragMove();
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

    private static ControlTemplate TransparentButtonTemplate() => TransparentButtonTemplateCache.Value;

    private static ControlTemplate SubtleSettingsButtonTemplate() => SubtleSettingsButtonTemplateCache.Value;

    private static ControlTemplate InfoBadgeButtonTemplate() => InfoBadgeButtonTemplateCache.Value;

    private ImageSource DropdownIcon()
    {
        var color = _muted is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var key = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return DropdownChevronIconCache.GetOrAdd(key, CreateDropdownChevronIcon);
    }

    private static ImageSource CreateDropdownChevronIcon(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new WpfPen(brush, 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new System.Windows.Point(6.5, 9), isFilled: false, isClosed: false);
            context.LineTo(new System.Windows.Point(12, 14.5), isStroked: true, isSmoothJoin: true);
            context.LineTo(new System.Windows.Point(17.5, 9), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(WpfBrushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        drawing.Children.Add(new GeometryDrawing(null, pen, geometry));
        drawing.Freeze();

        var image = new System.Windows.Media.DrawingImage(drawing);
        image.Freeze();
        return image;
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

        var accent = MainWindow.GetWindowsAccentColor();
        var listBg = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(useDark ? "#272727" : "#EDEDED");
        var accentBrush = accent is null ? FrozenBrush(useDark ? "#8A9CCC" : "#3B5BDB") : FrozenColorBrush(accent.Value);
        var selectedBorderBrush = accent is null ? FrozenBrush(useDark ? "#6878A8" : "#5C7CFA") : FrozenColorBrush(accent.Value);
        var selectedBrush = accent is null ? FrozenBrush(useDark ? "#324068" : "#C9D3F5") : FrozenColorBrush(MainWindow.BlendColors(listBg, accent.Value, useDark ? 0.20 : 0.16));
        var accentSoftBrush = accent is null ? FrozenBrush(useDark ? "#232A45" : "#E1E7FB") : FrozenColorBrush(MainWindow.BlendColors(listBg, accent.Value, useDark ? 0.26 : 0.20));

        return new SettingsPalette(
            FrozenBrush(useDark ? "#1A1A1A" : "#F7F7F7"),
            FrozenBrush(useDark ? "#212121" : "#FFFFFF"),
            FrozenBrush(useDark ? "#272727" : "#EDEDED"),
            FrozenBrush(useDark ? "#323232" : "#DCDCDC"),
            FrozenBrush(useDark ? "#F1F1F1" : "#1A1A1A"),
            FrozenBrush(useDark ? "#989898" : "#646464"),
            FrozenBrush(useDark ? "#494949" : "#B8B8B8"),
            FrozenBrush(useDark ? "#5A5A5A" : "#989898"),
            accentBrush,
            accentSoftBrush,
            selectedBrush,
            selectedBorderBrush);
    }

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FrozenColorBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
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
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.Equals(_currentPage, "General", StringComparison.OrdinalIgnoreCase) &&
                    _content.Children.Contains(panel))
                {
                    panel.Children.Add(ResetDefaultsFooter());
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
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
        button.Template = InfoBadgeButtonTemplate();
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
            Content = new WpfImage { Source = DropdownIcon(), Width = 11, Height = 11, Tag = DropdownIconTag },
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
                case WpfImage image when string.Equals(image.Tag as string, DropdownIconTag, StringComparison.Ordinal):
                    image.Source = DropdownIcon();
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
        return MainWindow.RenderAppTileIcon(preference);
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
            Source = DropdownIcon(),
            Width = 11,
            Height = 11,
            Tag = DropdownIconTag,
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
    private static readonly object FileGate = new();
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly string LogRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip");
    public static readonly string Path = System.IO.Path.Combine(LogRoot, "shell.log");
    private static readonly string TempPath = System.IO.Path.Combine(LogRoot, "shell.log.tmp");
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private const long TrimmedLogBytes = 2L * 1024 * 1024;
    private static readonly Lazy<Thread> Writer = new(StartWriter);
    private static volatile bool _stopping;
    private static volatile bool _traceEnabled = TraceEnabledByEnvironment();

    public static void Configure(string[] args)
    {
        if (_traceEnabled)
        {
            return;
        }

        _traceEnabled = args.Any(arg =>
            string.Equals(arg, "--debug-perf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--debug-log", StringComparison.OrdinalIgnoreCase));
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Snapshot(string message) => Write("INFO", message, force: true);

    public static void Error(Exception exception, string message) => Write("ERROR", $"{message}: {exception}", force: true);

    public static void Flush() => FlushPending();

    public static void Shutdown()
    {
        if (!Writer.IsValueCreated)
        {
            return;
        }

        _stopping = true;
        Signal.Set();
        if (!Writer.Value.Join(TimeSpan.FromSeconds(2)))
        {
            FlushPending();
        }
    }

    private static void Write(string level, string message, bool force = false)
    {
        if (!force && !_traceEnabled)
        {
            return;
        }

        _ = Writer.Value;
        Pending.Enqueue($"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        Signal.Set();
    }

    private static bool TraceEnabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("CLIP_SHELL_TRACE");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static Thread StartWriter()
    {
        var thread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "Clip shell log writer",
        };
        thread.Start();
        return thread;
    }

    private static void WriteLoop()
    {
        while (!_stopping || !Pending.IsEmpty)
        {
            Signal.WaitOne(TimeSpan.FromSeconds(1));
            FlushPending();
        }
    }

    private static void FlushPending()
    {
        if (Pending.IsEmpty)
        {
            return;
        }

        var builder = new StringBuilder();
        while (Pending.TryDequeue(out var line))
        {
            builder.Append(line);
        }

        if (builder.Length == 0)
        {
            return;
        }

        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(LogRoot);
                TrimLogIfNeeded();
                File.AppendAllText(Path, builder.ToString());
            }
        }
        catch
        {
        }
    }

    private static void TrimLogIfNeeded()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        var info = new FileInfo(Path);
        if (info.Length <= MaxLogBytes)
        {
            return;
        }

        using (var input = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var output = File.Create(TempPath))
        {
            var marker = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O} [INFO] shell log trimmed to last {TrimmedLogBytes / 1024 / 1024} MB{Environment.NewLine}");
            output.Write(marker, 0, marker.Length);
            input.Seek(Math.Max(0, input.Length - TrimmedLogBytes), SeekOrigin.Begin);
            input.CopyTo(output);
        }

        File.Copy(TempPath, Path, overwrite: true);
        File.Delete(TempPath);
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
        button.Template = ClipControlTemplates.CenterButton;
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
        button.Template = ClipControlTemplates.CenterButton;
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
