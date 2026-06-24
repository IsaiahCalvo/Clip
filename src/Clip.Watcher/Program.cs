using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Clip.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Svg;

namespace Clip.Watcher;

internal static class Program
{
    internal const string RichPaletteShowEventName = @"Local\ClipShellShowPalette";
    internal const string WatcherPaletteShowEventName = @"Local\ClipWatcherShowPalette";
    internal const string RichPaletteSingleInstanceMutexName = @"Global\ClipShellSingleInstance";

    private static readonly Lazy<WatcherSettingsProvider> SettingsLazy = new(() => new WatcherSettingsProvider());
    private static readonly Lazy<ClipboardHistoryStore> StoreLazy = new(() => new ClipboardHistoryStore(contentRootPath: Settings.Current.EffectiveClipboardFolderPath(), enableLoadMaintenance: false, retainLoadedItems: false));
    private static readonly string LogRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip");
    private static readonly string ErrorLogPath = Path.Combine(LogRoot, "error.log");
    private static readonly string DebugLogPath = Path.Combine(LogRoot, "debug.log");
    private static readonly string DebugLogTempPath = Path.Combine(LogRoot, "debug.log.tmp");
    private const long MaxDebugLogBytes = 2L * 1024 * 1024;
    private const long TrimmedDebugLogBytes = 768L * 1024;
    private static readonly ConcurrentQueue<string> DebugLogQueue = new();
    private static readonly AutoResetEvent DebugLogSignal = new(false);
    private static readonly Lazy<Thread> DebugLogWriter = new(StartDebugLogWriter);
    private static readonly bool TraceLoggingEnabled = IsEnabled(Environment.GetEnvironmentVariable("CLIP_WATCHER_TRACE"));
    private static long? _launcherStartTicks;
    private static volatile bool _debugLogStopping;

    static Program()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownDebugLog();
    }

    private static WatcherSettingsProvider Settings => SettingsLazy.Value;

    private static ClipboardHistoryStore Store => StoreLazy.Value;

    [STAThread]
    private static int Main(string[] args)
    {
        var mainStartTicks = Stopwatch.GetTimestamp();
        LogLauncherTiming(args, mainStartTicks);

        if (args.Length > 0 && !args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            return RunCommand(args);
        }

        var showOnStart = args.Any(arg =>
            arg.Equals("--show", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--open", StringComparison.OrdinalIgnoreCase));
        using var singleInstance = new Mutex(true, @"Global\ClipWatcherSingleInstance", out var ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            if (showOnStart && TrySignalWatcherPalette())
            {
                LogDebug("Watcher palette signaled existing watcher");
                return 0;
            }

            if (showOnStart)
            {
                TryLaunchRichPalette();
            }

            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);

        try
        {
            var startupWatch = Stopwatch.StartNew();
            var settingsPhaseStart = startupWatch.ElapsedMilliseconds;
            var settings = Settings;
            var settingsMs = startupWatch.ElapsedMilliseconds - settingsPhaseStart;
            var storePhaseStart = startupWatch.ElapsedMilliseconds;
            var store = Store;
            var storeMs = startupWatch.ElapsedMilliseconds - storePhaseStart;
            LogDebug($"Watcher startup dependencies elapsedMs={startupWatch.ElapsedMilliseconds} settingsMs={settingsMs} storeMs={storeMs}");
            using var form = new ClipboardWatcherForm(store, settings, showOnStart);
            Application.Run(form);
            return 0;
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }

    private static void LogLauncherTiming(string[] args, long mainStartTicks)
    {
        var launcherTicks = LauncherTicks(args);
        _launcherStartTicks = launcherTicks;
        if (launcherTicks is null)
        {
            return;
        }

        var launcherToMainMs = (long)((mainStartTicks - launcherTicks.Value) * 1000.0 / Stopwatch.Frequency);
        LogDebug($"Watcher main entered launcherToMainMs={launcherToMainMs}");
    }

    internal static long? LauncherToNowMs()
    {
        var launcherTicks = _launcherStartTicks;
        if (launcherTicks is null)
        {
            return null;
        }

        return (long)((Stopwatch.GetTimestamp() - launcherTicks.Value) * 1000.0 / Stopwatch.Frequency);
    }

    private static long? LauncherTicks(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals("--launcher-ticks", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return long.TryParse(args[index + 1], out var value) ? value : null;
            }

            if (arg.StartsWith("--launcher-ticks=", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(arg["--launcher-ticks=".Length..], out var value) ? value : null;
            }
        }

        return null;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }

            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..];
            }
        }

        return null;
    }

    private static int RunCommand(string[] args)
    {
        try
        {
            var command = args[0].ToLowerInvariant();
            var id = args.Length > 1 ? args[1] : string.Empty;

            switch (command)
            {
                case "list":
                    ListItems(args);
                    return 0;
                case "copy":
                    SetClipboard(id, paste: false);
                    return 0;
                case "paste":
                    SetClipboard(id, paste: true);
                    return 0;
                case "pin":
                    return Store.SetPinned(id, true) ? 0 : 2;
                case "unpin":
                    return Store.SetPinned(id, false) ? 0 : 2;
                case "up":
                    return Store.MovePinned(id, -1) ? 0 : 2;
                case "down":
                    return Store.MovePinned(id, 1) ? 0 : 2;
                case "delete":
                    return Store.Delete(id) ? 0 : 2;
                case "rename":
                    return Store.Rename(id, string.Join(' ', args.Skip(2))) ? 0 : 2;
                case "edit":
                    Store.EditText(id, string.Join(' ', args.Skip(2)));
                    return 0;
                case "append":
                    AppendText(id);
                    return 0;
                case "save":
                    Console.WriteLine(Store.SaveAsFile(id, args.Length > 2 ? args[2] : null));
                    return 0;
                case "copy-path":
                    return CopyPath(id);
                case "open":
                    return OpenItem(id, args.Length > 2 ? args[2] : null);
                case "reveal":
                    return RevealItem(id);
                case "preview-thumb":
                    return RenderPreviewThumb(args);
                case "perf-add-text":
                    return AddPerfText(args);
                case "perf-add-file":
                    return AddPerfFile(args);
                default:
                    PrintHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void ListItems(string[] args)
    {
        if (ClipboardHistoryListCommand.IsJsonRequest(args))
        {
            Console.WriteLine(ClipboardHistoryListCommand.Serialize(ClipboardHistoryListCommand.Create(Store, args)));
            return;
        }

        foreach (var item in Store.QueryItemSummaries())
        {
            var pin = item.IsPinned ? "PIN" : "   ";
            Console.WriteLine($"{pin} {item.Id} [{item.Kind}] {item.Preview}");
        }
    }

    private static void SetClipboard(string id, bool paste)
    {
        if (!TrySetClipboardItem(Store, id, paste))
        {
            throw new InvalidOperationException("Clipboard item not found.");
        }
    }

    internal static bool TrySetClipboardItem(ClipboardHistoryStore store, string id, bool paste)
    {
        var item = store.GetItem(id);
        if (item is null)
        {
            return false;
        }

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            Clipboard.SetText(SafeTextPayload(item));
        }
        else if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null)
        {
            using var image = Image.FromFile(item.AssetPath);
            Clipboard.SetImage(image);
        }
        else if (item.Kind == ClipboardItemKind.Files)
        {
            var files = new StringCollection();
            files.AddRange(item.FilePaths.ToArray());
            Clipboard.SetFileDropList(files);
        }

        if (paste)
        {
            SendKeys.SendWait("^v");
        }

        return true;
    }

    private static void AppendText(string id)
    {
        var item = Store.GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");
        if (item.Kind != ClipboardItemKind.Text)
        {
            throw new InvalidOperationException("Only text can be appended.");
        }

        var existing = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        Clipboard.SetText(existing + SafeTextPayload(item));
    }

    private static int CopyPath(string id)
    {
        var item = Store.GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");
        if (item.FilePaths.Count == 0)
        {
            return 2;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, item.FilePaths));
        return 0;
    }

    private static int OpenItem(string id, string? appPath)
    {
        var item = Store.GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");
        var startInfo = ClipboardItemLaunchCommand.CreateOpenStartInfo(item, appPath);
        if (startInfo is null)
        {
            return 2;
        }

        Process.Start(startInfo);
        return 0;
    }

    private static int RevealItem(string id)
    {
        var item = Store.GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");
        var startInfo = ClipboardItemLaunchCommand.CreateRevealStartInfo(item);
        if (startInfo is null)
        {
            return 2;
        }

        Process.Start(startInfo);
        return 0;
    }

    // preview-thumb <srcPath> <outPng>
    // Renders the first page of a PDF / Word / Excel / PowerPoint / Visio document to <outPng>
    // (PNG) so the lean net9 Command Palette extension can show a real first-page thumbnail
    // without taking any heavy PDF/Office dependency. The palette calls this lazily on selection
    // and caches the result itself; the underlying renderers also cache by source mtime, so a
    // repeat call for an unchanged file is cheap. Exit codes: 0 rendered, 2 bad args / unsupported,
    // 3 render produced nothing (e.g. pdftoppm or the Office COM server is unavailable).
    internal static int RenderPreviewThumb(string[] args)
    {
        var srcPath = args.Length > 1 ? args[1] : string.Empty;
        var outPng = args.Length > 2 ? args[2] : string.Empty;
        if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(outPng) || !File.Exists(srcPath))
        {
            Console.Error.WriteLine("Usage: preview-thumb <srcPath> <outPng>");
            return 2;
        }

        var extension = Path.GetExtension(srcPath).ToLowerInvariant();
        var isPdf = extension == ".pdf";
        if (!isPdf && !StaticDocumentPreviewRenderer.IsSupported(srcPath))
        {
            Console.Error.WriteLine($"Unsupported document type for preview: {extension}");
            return 2;
        }

        Image? image = null;
        try
        {
            if (isPdf)
            {
                if (PdfPreviewRenderer.TryRenderFirstPage(srcPath, out var pdfImage))
                {
                    image = pdfImage;
                }
                else
                {
                    pdfImage.Dispose();
                }
            }
            else
            {
                // Office/Visio rendering drives COM servers that require an STA apartment; the
                // renderer already marshals onto a dedicated STA thread.
                image = StaticDocumentPreviewRenderer.TryRenderFirstPageOnStaThread(srcPath);
            }

            if (image is null)
            {
                return 3;
            }

            var outDirectory = Path.GetDirectoryName(outPng);
            if (!string.IsNullOrWhiteSpace(outDirectory))
            {
                Directory.CreateDirectory(outDirectory);
            }

            image.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }
        catch (Exception ex)
        {
            LogError(ex);
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        finally
        {
            image?.Dispose();
        }
    }

    private static int AddPerfText(string[] args)
    {
        var text = string.Join(' ', args.Skip(1));
        if (string.IsNullOrEmpty(text))
        {
            return 2;
        }

        var item = Store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Text,
            Text = text,
            Preview = ClipboardHistoryStore.PreviewText(text),
            SourceApplication = "ClipPerf"
        });
        Console.WriteLine(item.Id);
        return 0;
    }

    private static int AddPerfFile(string[] args)
    {
        var path = args.Length > 1 ? args[1] : string.Empty;
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return 2;
        }

        var item = Store.AddOrUpdate(new ClipboardHistoryItem
        {
            Kind = ClipboardItemKind.Files,
            FilePaths = [path],
            Preview = Path.GetFileName(path),
            SourceApplication = "ClipPerf"
        });
        Console.WriteLine(item.Id);
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Clip commands:");
        Console.WriteLine("  watch");
        Console.WriteLine("  watch [--show]");
        Console.WriteLine("  list [--json] [--limit <count>] [--query <text>]");
        Console.WriteLine("  copy <id>");
        Console.WriteLine("  paste <id>");
        Console.WriteLine("  pin|unpin|up|down|delete <id>");
        Console.WriteLine("  rename <id> <title>");
        Console.WriteLine("  edit <id> <new text>");
        Console.WriteLine("  append <id>");
        Console.WriteLine("  save <id> [path]");
        Console.WriteLine("  copy-path <id>");
        Console.WriteLine("  open <id> [app path]");
        Console.WriteLine("  reveal <id>");
        Console.WriteLine("  preview-thumb <srcPath> <outPng>");
    }

    internal static bool TryLaunchRichPalette(WatcherTrayAction action = WatcherTrayAction.OpenClip, bool keepWarm = false, bool startHidden = false)
    {
        var trayAction = WatcherTrayMenu.TrayActionArgument(action);
        if (!startHidden && action == WatcherTrayAction.OpenClip && TrySignalRichPaletteWithAction(trayAction))
        {
            LogDebug("Rich palette signaled existing shell");
            return true;
        }

        var exe = FindRichPaletteExecutable();
        if (exe is null)
        {
            LogDebug("Rich palette executable not found");
            return false;
        }

        var startInfo = CreateRichPaletteStartInfo(exe, action, keepWarm, startHidden);
        Process.Start(startInfo);
        LogDebug($"Rich palette launched path={exe} action={action} keepWarm={keepWarm} startHidden={startHidden}");
        return true;
    }

    private static bool TrySignalRichPaletteWithAction(string? trayAction)
    {
        if (!string.IsNullOrWhiteSpace(trayAction))
        {
            SaveTrayActionRequest(trayAction);
        }

        return TrySignalRichPalette();
    }

    private static void SaveTrayActionRequest(string action)
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            File.WriteAllText(Path.Combine(LogRoot, "tray-action.request"), action.Trim());
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    internal static ProcessStartInfo CreateRichPaletteStartInfo(string exe, WatcherTrayAction action, bool keepWarm = false, bool startHidden = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--palette-session");
        if (keepWarm)
        {
            startInfo.ArgumentList.Add("--keep-warm");
        }

        if (startHidden)
        {
            startInfo.ArgumentList.Add("--prewarm");
        }

        var trayAction = WatcherTrayMenu.TrayActionArgument(action);
        if (!startHidden && !string.IsNullOrWhiteSpace(trayAction))
        {
            startInfo.ArgumentList.Add($"--tray-action={trayAction}");
        }

        return startInfo;
    }

    internal static bool IsRichPaletteRunning(string mutexName = RichPaletteSingleInstanceMutexName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out var mutex))
            {
                return false;
            }

            mutex.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySignalRichPalette(string eventName = RichPaletteShowEventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var signal))
            {
                return false;
            }

            using (signal)
            {
                signal.Set();
            }

            return true;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
    }

    internal static bool TrySignalWatcherPalette(string eventName = WatcherPaletteShowEventName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(eventName, out var signal))
            {
                return false;
            }

            using (signal)
            {
                signal.Set();
            }

            return true;
        }
        catch (Exception ex)
        {
            LogError(ex);
            return false;
        }
    }

    private static string? FindRichPaletteExecutable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Clip.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory, "Clip.exe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return null;
    }

    // The Watcher imports Windows clipboard history by shelling out to the
    // Clip.WindowsHistory helper directly (same helper the Shell uses). The old
    // Clip.Command pass-through was only needed by the removed Command Palette extension.
    internal static string? FindWindowsHistoryExecutable()
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

    internal static int ParseImportCount(string output)
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

    internal static void LogError(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LogRoot);
            File.AppendAllText(ErrorLogPath, $"{DateTimeOffset.Now:u}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    internal static void LogDebug(string message)
    {
        try
        {
            _ = DebugLogWriter.Value;
            DebugLogQueue.Enqueue($"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
            DebugLogSignal.Set();
        }
        catch
        {
        }
    }

    internal static void LogTrace(string message)
    {
        if (TraceLoggingEnabled)
        {
            LogDebug(message);
        }
    }

    private static bool IsEnabled(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static Thread StartDebugLogWriter()
    {
        var thread = new Thread(DebugLogWriteLoop)
        {
            IsBackground = true,
            Name = "Clip watcher debug log writer",
        };
        thread.Start();
        return thread;
    }

    private static void DebugLogWriteLoop()
    {
        while (!_debugLogStopping || !DebugLogQueue.IsEmpty)
        {
            DebugLogSignal.WaitOne(TimeSpan.FromSeconds(1));
            FlushDebugLog();
        }
    }

    private static void ShutdownDebugLog()
    {
        if (!DebugLogWriter.IsValueCreated)
        {
            return;
        }

        _debugLogStopping = true;
        DebugLogSignal.Set();
        if (!DebugLogWriter.Value.Join(TimeSpan.FromSeconds(2)))
        {
            FlushDebugLog();
        }
    }

    private static void FlushDebugLog()
    {
        if (DebugLogQueue.IsEmpty)
        {
            return;
        }

        var builder = new StringBuilder();
        while (DebugLogQueue.TryDequeue(out var line))
        {
            builder.Append(line);
        }

        if (builder.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LogRoot);
            TrimDebugLogIfNeeded();
            File.AppendAllText(DebugLogPath, builder.ToString());
        }
        catch
        {
        }
    }

    private static void TrimDebugLogIfNeeded()
    {
        if (!File.Exists(DebugLogPath))
        {
            return;
        }

        var info = new FileInfo(DebugLogPath);
        if (info.Length <= MaxDebugLogBytes)
        {
            return;
        }

        using (var input = File.Open(DebugLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var output = File.Create(DebugLogTempPath))
        {
            var marker = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:u} debug log trimmed to last {TrimmedDebugLogBytes / 1024} KB{Environment.NewLine}");
            output.Write(marker, 0, marker.Length);
            input.Seek(Math.Max(0, input.Length - TrimmedDebugLogBytes), SeekOrigin.Begin);
            input.CopyTo(output);
        }

        File.Copy(DebugLogTempPath, DebugLogPath, overwrite: true);
        File.Delete(DebugLogTempPath);
    }

    private static string SafeTextPayload(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrEmpty(item.Text))
        {
            return item.Text;
        }

        if (!string.IsNullOrEmpty(item.Preview))
        {
            return item.Preview;
        }

        return " ";
    }
}
internal sealed class WatcherSettingsProvider
{
    private DateTime _lastWriteUtc;

    public WatcherSettings Current { get; private set; } = WatcherSettings.Load();

    public WatcherSettings ReloadIfChanged()
    {
        try
        {
            var path = WatcherSettings.SettingsPath;
            var lastWrite = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (lastWrite != _lastWriteUtc)
            {
                Current = WatcherSettings.Load();
                _lastWriteUtc = lastWrite;
            }
        }
        catch
        {
        }

        return Current;
    }
}

internal enum WatcherAppIconPreference
{
    Light,
    Dark,
}

internal static class WatcherTrayIcon
{
    public static string IconPath(WatcherAppIconPreference preference, string baseDirectory)
    {
        var fileName = preference == WatcherAppIconPreference.Dark ? "clip-tile-dark.ico" : "clip-tile-light.ico";
        var path = Path.Combine(baseDirectory, "assets", "app-icons", fileName);
        if (File.Exists(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "assets", "app-icons", fileName));
    }

    public static Icon? LoadOwnedIcon(WatcherAppIconPreference preference, string baseDirectory)
    {
        var path = IconPath(preference, baseDirectory);
        return File.Exists(path) ? new Icon(path) : null;
    }

    public static Icon LoadIcon(WatcherAppIconPreference preference, string baseDirectory)
    {
        return LoadOwnedIcon(preference, baseDirectory) ?? SystemIcons.Application;
    }
}

internal sealed class WatcherSettings
{
    private const string ClipboardFolderName = "Clipboard History";

    public string? ClipboardFolderPath { get; init; }
    public int? HistoryLimit { get; init; } = 500;
    public long? MaxItemSizeBytes { get; init; } = 50L * 1024 * 1024;
    public WatcherAppIconPreference AppIcon { get; init; } = WatcherAppIconPreference.Light;
    public string OpenHotkey { get; init; } = "Alt+V";
    public PasteFormatPreference DefaultPasteFormat { get; init; } = PasteFormatPreference.PlainText;
    public WatcherPrivacySettings Privacy { get; init; } = new();

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "settings.json");

    private static string DefaultClipboardFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        ClipboardFolderName);

    public string EffectiveClipboardFolderPath() => string.IsNullOrWhiteSpace(ClipboardFolderPath) ? DefaultClipboardFolderPath : ClipboardFolderPath;

    public int EffectiveHistoryLimit() => HistoryLimit is null ? int.MaxValue : Math.Max(0, HistoryLimit.Value);

    public static WatcherSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new WatcherSettings();
        }

        try
        {
            return LoadFromJson(File.ReadAllText(SettingsPath));
        }
        catch
        {
            return new WatcherSettings();
        }
    }

    internal static WatcherSettings LoadFromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new WatcherSettings
            {
                ClipboardFolderPath = StringProperty(root, "ClipboardFolderPath"),
                HistoryLimit = NullableIntProperty(root, "HistoryLimit", 500),
                MaxItemSizeBytes = NullableLongProperty(root, "MaxItemSizeBytes", 50L * 1024 * 1024),
                AppIcon = AppIconProperty(root),
                OpenHotkey = HotkeyProperty(root) ?? "Alt+V",
                DefaultPasteFormat = PasteFormatProperty(root),
                Privacy = WatcherPrivacySettings.FromJson(root),
            };
        }
        catch
        {
            return new WatcherSettings();
        }
    }

    private static WatcherAppIconPreference AppIconProperty(JsonElement root)
    {
        if (!root.TryGetProperty("AppIcon", out var value))
        {
            return WatcherAppIconPreference.Light;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric == (int)WatcherAppIconPreference.Dark ? WatcherAppIconPreference.Dark : WatcherAppIconPreference.Light;
        }

        return value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<WatcherAppIconPreference>(value.GetString(), ignoreCase: true, out var preference)
                ? preference
                : WatcherAppIconPreference.Light;
    }


    private static string? HotkeyProperty(JsonElement root)
    {
        if (!root.TryGetProperty("Hotkeys", out var hotkeys) ||
            hotkeys.ValueKind != JsonValueKind.Object ||
            !hotkeys.TryGetProperty("OpenClip", out var openClip) ||
            openClip.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return openClip.GetString();
    }

    private static string? StringProperty(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static PasteFormatPreference PasteFormatProperty(JsonElement root)
    {
        if (!root.TryGetProperty("DefaultPasteFormat", out var value))
        {
            return PasteFormatPreference.PlainText;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(PasteFormatPreference), numeric))
        {
            return (PasteFormatPreference)numeric;
        }

        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<PasteFormatPreference>(value.GetString(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return PasteFormatPreference.PlainText;
    }

    private static int? NullableIntProperty(JsonElement root, string name, int? fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.TryGetInt32(out var result) ? result : fallback;
    }

    private static long? NullableLongProperty(JsonElement root, string name, long? fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.TryGetInt64(out var result) ? result : fallback;
    }
}

internal sealed class WatcherPrivacySettings
{
    public List<WatcherExcludedApp> ExcludedApps { get; init; } = [];

    public bool RequiresSourcePath => ExcludedApps.Any(app => app.RequiresSourcePath);

    public bool IsExcluded(string? sourceName, string? sourcePath)
    {
        return ExcludedApps.Any(app => app.MatchesSource(sourceName, sourcePath));
    }

    public static WatcherPrivacySettings FromJson(JsonElement root)
    {
        var result = new WatcherPrivacySettings();
        if (!root.TryGetProperty("Privacy", out var privacy) ||
            privacy.ValueKind != JsonValueKind.Object ||
            !privacy.TryGetProperty("ExcludedApps", out var apps) ||
            apps.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var app in apps.EnumerateArray())
        {
            WatcherExcludedApp? entry = null;
            if (app.ValueKind == JsonValueKind.String)
            {
                entry = WatcherExcludedApp.Create(app.GetString(), null);
            }
            else if (app.ValueKind == JsonValueKind.Object)
            {
                var name = app.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                var path = app.TryGetProperty("ExecutablePath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                    ? pathElement.GetString()
                    : null;
                entry = WatcherExcludedApp.Create(name, path);
            }

            if (entry is not null && result.ExcludedApps.All(existing => !existing.MatchesEntry(entry)))
            {
                result.ExcludedApps.Add(entry);
            }
        }

        return result;
    }
}

internal sealed class WatcherExcludedApp
{
    public string Name { get; init; } = "";
    public string? ExecutablePath { get; init; }

    private string Key => NormalizePath(ExecutablePath) ?? NormalizeName(Name) ?? Name;

    public bool RequiresSourcePath => NormalizePath(ExecutablePath) is not null;

    public static WatcherExcludedApp? Create(string? name, string? executablePath)
    {
        var path = NormalizeEntry(executablePath);
        var displayName = NormalizeEntry(name) ?? Path.GetFileNameWithoutExtension(path);
        if (displayName is null)
        {
            return null;
        }

        return new WatcherExcludedApp { Name = displayName, ExecutablePath = path };
    }

    public bool MatchesEntry(WatcherExcludedApp other) => string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

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
        return normalized is null || !Path.IsPathRooted(normalized)
            ? null
            : Path.GetFullPath(normalized).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal readonly record struct WatcherHotkey(uint Modifiers, uint VirtualKey, string DisplayText)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    public static WatcherHotkey OpenHotkey(string? configured)
    {
        return TryParse(configured, out var hotkey) || TryParse("Alt+V", out hotkey)
            ? hotkey
            : new WatcherHotkey(ModAlt, (uint)Keys.V, "Alt+V");
    }

    private static bool TryParse(string? value, out WatcherHotkey hotkey)
    {
        hotkey = default;
        var parts = value?
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (parts is null || parts.Length == 0)
        {
            return false;
        }

        uint modifiers = 0;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index].ToLowerInvariant() switch
            {
                "alt" => ModAlt,
                "ctrl" or "control" => ModControl,
                "shift" => ModShift,
                "win" or "windows" or "meta" => ModWin,
                _ => 0,
            };
        }

        if (modifiers == 0 || !TryParseKey(parts[^1], out var key))
        {
            return false;
        }

        hotkey = new WatcherHotkey(modifiers, key, string.Join("+", parts));
        return true;
    }

    private static bool TryParseKey(string value, out uint key)
    {
        key = 0;
        if (value.Length == 1)
        {
            var ch = char.ToUpperInvariant(value[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = ch;
                return true;
            }
        }

        if (value.Length > 1 && value[0] is 'F' or 'f' && int.TryParse(value[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            key = (uint)((int)Keys.F1 + functionKey - 1);
            return true;
        }

        return Enum.TryParse<Keys>(value, ignoreCase: true, out var parsed) && (key = (uint)parsed) != 0;
    }
}


internal sealed class ClipboardWatcherForm : Form
{
    private const int WmClipboardUpdate = 0x031D;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 7001;
    private const int WatcherWindowsHistoryImportLimit = 120;
    private static readonly TimeSpan WindowsHistoryImportMinimumInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClipboardDuplicateBurstWindow = TimeSpan.FromSeconds(1);
    private readonly ClipboardHistoryStore _store;
    private readonly WatcherSettingsProvider _settingsProvider;
    private readonly PeriodicWorkThrottle _windowsHistoryImportThrottle = new(WindowsHistoryImportMinimumInterval);
    private readonly ClipboardCaptureBurstGate _clipboardCaptureBurstGate = new(ClipboardDuplicateBurstWindow);
    private readonly NotifyIcon _trayIcon = new();
    private readonly System.Windows.Forms.Timer _settingsReloadTimer = new() { Interval = 350 };
    private readonly bool _showOnStart;
    private Icon? _ownedTrayIcon;
    private FileSystemWatcher? _settingsWatcher;
    private EventWaitHandle? _showPaletteSignal;
    private CancellationTokenSource? _showPaletteSignalCts;
    private bool _clipboardListenerRegistered;
    private bool _hotkeyRegistered;
    private WatcherHotkey _registeredHotkey;
    private bool _settingsReloadQueued;
    private bool _deferWatcherHooks;
    private bool _historyImportInProgress;
    private string? _lastRealSourceName;
    private string? _lastRealSourcePath;
    private uint _lastClipboardSequenceNumber;

    public ClipboardWatcherForm(ClipboardHistoryStore store, WatcherSettingsProvider settingsProvider, bool showOnStart = false)
    {
        _store = store;
        _settingsProvider = settingsProvider;
        _showOnStart = showOnStart;
        _deferWatcherHooks = showOnStart;
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        _settingsReloadTimer.Tick += (_, _) =>
        {
            _settingsReloadTimer.Stop();
            _settingsReloadQueued = false;
            ApplySettingsChanges();
        };
        if (_showOnStart)
        {
            EnsureHandleForDeferredStartup();
            Program.LogDebug($"Clip watcher show starting handle={Handle}");
            ShowPaletteAfterStartupPrewarm();
            BeginInvokeIfAlive(CompleteWatcherStartup);
            BeginInvokeIfAlive(BeginDeferredStartupWork);
            return;
        }

        CompleteWatcherStartup();
        BeginDeferredStartupWork();
    }

    private void CompleteWatcherStartup()
    {
        _deferWatcherHooks = false;
        ConfigureTrayIcon();
        StartBackgroundListeners();
        _ = Handle;
        var watch = Stopwatch.StartNew();
        EnsureWatcherHooks(showWarning: true);
        _store.WarmHotIndexes();
        ApplyOpenMode();
        Program.LogDebug($"Clip watcher started handle={Handle} hotkey={_registeredHotkey.DisplayText} registered={_hotkeyRegistered} win32={Marshal.GetLastWin32Error()} elapsedMs={watch.ElapsedMilliseconds}");
    }

    private void StartBackgroundListeners()
    {
        StartSettingsWatcher();
        StartShowPaletteSignalListener();
    }

    private void ConfigureTrayIcon()
    {
        if (_trayIcon.Visible)
        {
            return;
        }

        _trayIcon.Text = "Clip";
        ApplyTrayIcon(_settingsProvider.Current.AppIcon);
        _trayIcon.ContextMenuStrip = CreateTrayMenu();
        _trayIcon.DoubleClick += (_, _) => RunTrayAction(WatcherTrayAction.OpenClip);
        _trayIcon.Visible = true;
    }

    private void ApplyTrayIcon(WatcherAppIconPreference preference)
    {
        var oldIcon = _ownedTrayIcon;
        try
        {
            var icon = WatcherTrayIcon.LoadOwnedIcon(preference, AppContext.BaseDirectory);
            if (icon is null)
            {
                _trayIcon.Icon = SystemIcons.Application;
                _ownedTrayIcon = null;
            }
            else
            {
                _trayIcon.Icon = icon;
                _ownedTrayIcon = icon;
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            _trayIcon.Icon = SystemIcons.Application;
            _ownedTrayIcon = null;
        }
        finally
        {
            oldIcon?.Dispose();
        }
    }

    private void ApplyOpenMode()
    {
        EnsureStandaloneShellWarm();
    }

    private void EnsureStandaloneShellWarm()
    {
        if (Program.IsRichPaletteRunning())
        {
            Program.LogDebug("Standalone shell already warm");
            return;
        }

        if (Program.TryLaunchRichPalette(WatcherTrayAction.OpenClip, keepWarm: true, startHidden: true))
        {
            Program.LogDebug("Standalone shell prewarm requested");
        }
    }

    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (var item in WatcherTrayMenu.DefaultItems)
        {
            if (item.Action == WatcherTrayAction.Exit)
            {
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add(item.Label, null, (_, _) => BeginInvokeIfAlive(() => RunTrayAction(item.Action)));
        }

        return menu;
    }

    private void RunTrayAction(WatcherTrayAction action)
    {
        try
        {
            switch (action)
            {
                case WatcherTrayAction.OpenClip:
                    ShowPalette();
                    break;
                case WatcherTrayAction.PasteLatest:
                    PasteLatestFromTray();
                    break;
                case WatcherTrayAction.CheckForUpdates:
                case WatcherTrayAction.SaveLogSnapshot:
                case WatcherTrayAction.OpenSettings:
                    Program.TryLaunchRichPalette(action, keepWarm: true);
                    break;
                case WatcherTrayAction.Exit:
                    Application.Exit();
                    break;
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            _trayIcon.ShowBalloonTip(3000, "Clip", "Action failed. Log saved.", ToolTipIcon.Warning);
        }
    }

    private void PasteLatestFromTray()
    {
        var item = _store.QueryRecentItemSummaries(1).FirstOrDefault();
        if (item is null)
        {
            _trayIcon.ShowBalloonTip(3000, "Clip", "No clipboard items yet.", ToolTipIcon.Info);
            return;
        }

        if (Program.TrySetClipboardItem(_store, item.Id, paste: true))
        {
            Program.LogDebug($"Tray paste latest id={item.Id}");
            return;
        }

        _trayIcon.ShowBalloonTip(3000, "Clip", "Latest item is no longer available.", ToolTipIcon.Warning);
    }

    private void EnsureHandleForDeferredStartup()
    {
        _ = Handle;
    }

    private void BeginDeferredStartupWork()
    {
        QueueWindowsHistoryImport("startup", refreshPalette: false, delayMs: 8000);
    }

    private void ShowPaletteAfterStartupPrewarm()
    {
        ShowPalette();
    }

    private bool _isDisposed;

    private void BeginInvokeIfAlive(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(action);
            }
        }
        catch
        {
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_deferWatcherHooks)
        {
            EnsureWatcherHooks(showWarning: false);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseWatcherHooks();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            if (ShouldSkipClipboardSequence())
            {
                return;
            }

            CaptureCurrentClipboard();
        }

        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            Program.LogDebug($"Open hotkey received key={_registeredHotkey.DisplayText} handle={Handle}");
            try
            {
                ShowPalette();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.L))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
        ReleaseWatcherHooks();
        _settingsReloadTimer.Stop();
        _showPaletteSignalCts?.Cancel();
        if (disposing)
        {
            _settingsReloadTimer.Dispose();
            _settingsWatcher?.Dispose();
            _showPaletteSignal?.Dispose();
            _showPaletteSignalCts?.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _ownedTrayIcon?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void StartSettingsWatcher()
    {
        try
        {
            var path = WatcherSettings.SettingsPath;
            var directory = Path.GetDirectoryName(path);
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            _settingsWatcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.CreationTime |
                    NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size,
            };
            _settingsWatcher.Changed += OnSettingsFileChanged;
            _settingsWatcher.Created += OnSettingsFileChanged;
            _settingsWatcher.Deleted += OnSettingsFileChanged;
            _settingsWatcher.Renamed += OnSettingsFileRenamed;
            _settingsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e) => QueueSettingsReload();

    private void OnSettingsFileRenamed(object sender, RenamedEventArgs e) => QueueSettingsReload();

    private void QueueSettingsReload()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvokeIfAlive(QueueSettingsReload);
            return;
        }

        if (!_settingsReloadQueued)
        {
            _settingsReloadQueued = true;
        }

        _settingsReloadTimer.Stop();
        _settingsReloadTimer.Start();
    }

    private void StartShowPaletteSignalListener()
    {
        _showPaletteSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.WatcherPaletteShowEventName);
        _showPaletteSignalCts = new CancellationTokenSource();
        var token = _showPaletteSignalCts.Token;
        var signal = _showPaletteSignal;

        _ = Task.Run(() =>
        {
            try
            {
                var handles = new WaitHandle[] { signal, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    var index = WaitHandle.WaitAny(handles);
                    if (index != 0 || token.IsCancellationRequested)
                    {
                        break;
                    }

                    BeginInvokeIfAlive(ShowPalette);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }, token);
    }

    private void ShowPalette()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            Program.LogDebug("Shell palette open requested");
            if (Program.TryLaunchRichPalette(keepWarm: true))
            {
                var launcherToShowMs = Program.LauncherToNowMs();
                var launcherTiming = launcherToShowMs is null ? string.Empty : $" launcherToShellMs={launcherToShowMs}";
                Program.LogDebug($"Shell palette requested elapsedMs={watch.ElapsedMilliseconds}{launcherTiming}");
                return;
            }

            Program.LogDebug($"Shell palette unavailable elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    private void QueueWindowsHistoryImport(string reason, bool refreshPalette, int delayMs)
    {
        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (_isDisposed || IsDisposed || !IsHandleCreated)
            {
                return;
            }

            BeginInvokeIfAlive(() => _ = ImportWindowsClipboardHistoryAsync(reason, refreshPalette));
        }, TaskScheduler.Default);
    }

    private async Task ImportWindowsClipboardHistoryAsync(string reason, bool refreshPalette)
    {
        if (_historyImportInProgress)
        {
            return;
        }

        if (!_windowsHistoryImportThrottle.TryBegin(DateTimeOffset.UtcNow))
        {
            Program.LogDebug($"Windows history import skipped reason={reason} throttle={WindowsHistoryImportMinimumInterval}");
            return;
        }

        _historyImportInProgress = true;
        var watch = Stopwatch.StartNew();
        try
        {
            var imported = await ImportWindowsClipboardHistoryInHelperAsync(WatcherWindowsHistoryImportLimit);
            Program.LogDebug($"Windows history import reason={reason} imported={imported} elapsedMs={watch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        finally
        {
            _historyImportInProgress = false;
        }
    }

    private static async Task<int> ImportWindowsClipboardHistoryInHelperAsync(int maxItems)
    {
        var command = Program.FindWindowsHistoryExecutable();
        if (command is null)
        {
            Program.LogDebug("Windows history import skipped helper=missing");
            return 0;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(command) ?? AppContext.BaseDirectory,
            },
            EnableRaisingEvents = true,
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
            Program.LogDebug($"Windows history import helper failed exit={process.ExitCode} error={error.Trim()}");
            return 0;
        }

        return Program.ParseImportCount(output);
    }

    private void EnsureWatcherHooks(bool showWarning)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (!_clipboardListenerRegistered)
        {
            _clipboardListenerRegistered = AddClipboardFormatListener(Handle);
            _lastClipboardSequenceNumber = GetClipboardSequenceNumber();
            Program.LogDebug($"Clipboard listener registered={_clipboardListenerRegistered} handle={Handle} win32={Marshal.GetLastWin32Error()}");
        }

        if (!_hotkeyRegistered)
        {
            var settings = _settingsProvider.ReloadIfChanged();
            _registeredHotkey = WatcherHotkey.OpenHotkey(settings.OpenHotkey);
            _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, _registeredHotkey.Modifiers, _registeredHotkey.VirtualKey);
            var win32 = Marshal.GetLastWin32Error();
            Program.LogDebug($"Open hotkey registered={_hotkeyRegistered} key={_registeredHotkey.DisplayText} handle={Handle} win32={win32}");
            if (!_hotkeyRegistered && showWarning)
            {
                MessageBox.Show(
                    $"Clip could not register {_registeredHotkey.DisplayText}. Another app is already using it.",
                    "Clip",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    private void ReleaseWatcherHooks()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (_hotkeyRegistered)
        {
            var released = UnregisterHotKey(Handle, HotkeyId);
            Program.LogDebug($"Open hotkey unregistered={released} key={_registeredHotkey.DisplayText} handle={Handle} win32={Marshal.GetLastWin32Error()}");
            _hotkeyRegistered = false;
        }

        if (_clipboardListenerRegistered)
        {
            var removed = RemoveClipboardFormatListener(Handle);
            Program.LogDebug($"Clipboard listener removed={removed} handle={Handle} win32={Marshal.GetLastWin32Error()}");
            _clipboardListenerRegistered = false;
        }
    }

    private void CaptureCurrentClipboard()
    {
        try
        {
            var captureWatch = Stopwatch.StartNew();
            var settings = RefreshSettings();
            var source = SourceSnapshot(includePath: settings.Privacy.RequiresSourcePath);
            var sourceName = source.Name;
            var sourcePath = source.Path;
            if (settings.Privacy.IsExcluded(sourceName, sourcePath))
            {
                Program.LogDebug($"Clipboard skipped excluded source={sourceName} path={sourcePath}");
                return;
            }

            var data = Clipboard.GetDataObject();
            if (data is null)
            {
                return;
            }

            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (data.GetData(DataFormats.FileDrop) as string[])?
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToList() ?? [];
                if (files.Count > 0)
                {
                    var captured = AddCapturedItem(new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Files,
                        FilePaths = files,
                        Preview = string.Join(", ", files.Select(Path.GetFileName)),
                        ContentHash = HashText(string.Join("|", files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))),
                        SourceApplication = sourceName,
                        SourceApplicationPath = sourcePath,
                    }, settings);
                    LogCapturedItem(captured, captureWatch);
                }
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                var text = data.GetData(DataFormats.UnicodeText) as string ??
                    data.GetData(DataFormats.Text) as string ??
                    string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var captureRichText = settings.DefaultPasteFormat == PasteFormatPreference.OriginalFormatting;
                    var htmlText = captureRichText && data.GetDataPresent(DataFormats.Html)
                        ? data.GetData(DataFormats.Html) as string
                        : null;
                    var rtfText = captureRichText && data.GetDataPresent(DataFormats.Rtf)
                        ? data.GetData(DataFormats.Rtf) as string
                        : null;
                    var captured = AddCapturedItem(new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Text,
                        Text = text,
                        Preview = ClipboardHistoryStore.PreviewText(text),
                        ContentHash = HashText(text),
                        HtmlText = htmlText,
                        RtfText = rtfText,
                        SourceApplication = sourceName,
                        SourceApplicationPath = sourcePath,
                    }, settings);
                    LogCapturedItem(captured, captureWatch);
                }
            }
            else if (data.GetDataPresent(DataFormats.Bitmap))
            {
                var assetPath = _store.NewAssetFilePath(".png");
                if (data.GetData(DataFormats.Bitmap) is not Image image)
                {
                    return;
                }

                image.Save(assetPath, System.Drawing.Imaging.ImageFormat.Png);
                var width = image.Width;
                var height = image.Height;
                var captured = AddCapturedItem(new ClipboardHistoryItem
                {
                    Kind = ClipboardItemKind.Image,
                    AssetPath = assetPath,
                    Preview = $"Image {width} x {height}",
                    ContentHash = File.Exists(assetPath) ? HashFile(assetPath) : null,
                    ImageWidth = width,
                    ImageHeight = height,
                    SourceApplication = sourceName,
                    SourceApplicationPath = sourcePath,
                }, settings);
                LogCapturedItem(captured, captureWatch);
            }
        }
        catch
        {
            // Some apps lock or clear clipboard formats briefly. Ignore and catch the next update.
        }
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
            Program.LogDebug($"Clipboard skipped duplicate sequence={sequence}");
            return true;
        }

        _lastClipboardSequenceNumber = sequence;
        return false;
    }

    private WatcherSettings RefreshSettings()
    {
        var settings = _settingsProvider.ReloadIfChanged();
        var path = settings.EffectiveClipboardFolderPath();
        if (!string.Equals(_store.ContentRootPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _store.SetContentRootPath(path);
            Program.LogDebug($"Clipboard folder changed path={path}");
        }

        return settings;
    }

    private void ApplySettingsChanges()
    {
        var settings = RefreshSettings();
        ApplyTrayIcon(settings.AppIcon);
        ApplyOpenMode();

        var desiredHotkey = WatcherHotkey.OpenHotkey(settings.OpenHotkey);
        if (!_hotkeyRegistered)
        {
            EnsureWatcherHooks(showWarning: false);
            return;
        }

        if (desiredHotkey == _registeredHotkey)
        {
            return;
        }

        var released = UnregisterHotKey(Handle, HotkeyId);
        Program.LogDebug($"Open hotkey reloading old={_registeredHotkey.DisplayText} new={desiredHotkey.DisplayText} released={released} win32={Marshal.GetLastWin32Error()}");
        _hotkeyRegistered = false;
        _registeredHotkey = desiredHotkey;
        EnsureWatcherHooks(showWarning: false);
    }

    private (ClipboardHistoryItem Item, long StoreMs)? AddCapturedItem(ClipboardHistoryItem item, WatcherSettings settings)
    {
        if (ShouldSkipDuplicateClipboardBurst(item))
        {
            return null;
        }

        if (!AllowsItem(item, settings.MaxItemSizeBytes))
        {
            DeleteCaptureAsset(item);
            Program.LogDebug($"Clipboard skipped oversized kind={item.Kind} bytes={EstimateBytes(item)} limit={settings.MaxItemSizeBytes?.ToString() ?? "Unlimited"} source={item.SourceApplication} preview={item.Preview}");
            return null;
        }

        var storeWatch = Stopwatch.StartNew();
        var saved = _store.AddOrUpdate(item, settings.EffectiveHistoryLimit());
        return (saved, storeWatch.ElapsedMilliseconds);
    }

    private bool ShouldSkipDuplicateClipboardBurst(ClipboardHistoryItem item)
    {
        var fingerprint = ClipboardFingerprint(item);
        if (!_clipboardCaptureBurstGate.ShouldSkip(fingerprint, DateTimeOffset.UtcNow))
        {
            return false;
        }

        DeleteCaptureAsset(item);
        Program.LogDebug($"Clipboard skipped duplicate burst kind={item.Kind} source={item.SourceApplication} preview={item.Preview}");
        return true;
    }

    private static string? ClipboardFingerprint(ClipboardHistoryItem item)
    {
        return string.IsNullOrWhiteSpace(item.ContentHash)
            ? null
            : $"{item.Kind}:{item.ContentHash}";
    }

    private static void LogCapturedItem((ClipboardHistoryItem Item, long StoreMs)? captured, Stopwatch captureWatch)
    {
        if (captured is null)
        {
            return;
        }

        var item = captured.Value.Item;
        Program.LogDebug($"Clipboard captured id={item.Id} kind={item.Kind} source={item.SourceApplication} elapsedMs={captureWatch.ElapsedMilliseconds} storeMs={captured.Value.StoreMs} preview={item.Preview}");
    }

    private static bool AllowsItem(ClipboardHistoryItem item, long? maxBytes)
    {
        return maxBytes is null || EstimateBytes(item) <= Math.Max(0, maxBytes.Value);
    }

    private static long EstimateBytes(ClipboardHistoryItem item)
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
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? 0 : new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static void DeleteCaptureAsset(ClipboardHistoryItem item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.AssetPath) && File.Exists(item.AssetPath))
            {
                File.Delete(item.AssetPath);
            }
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static (string? Name, string? Path) ForegroundAppSnapshot(bool includePath)
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
            if (processId == 0)
            {
                return (null, null);
            }

            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            string? path = null;
            if (includePath)
            {
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    path = null;
                }
            }

            return (name, path);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private (string? Name, string? Path) SourceSnapshot(bool includePath)
    {
        UpdateSourceSnapshot(includePath);
        return (_lastRealSourceName, includePath ? _lastRealSourcePath : null);
    }

    private void UpdateSourceSnapshot(bool includePath)
    {
        var (name, path) = ForegroundAppSnapshot(includePath);
        if (IsSelfSource(path, name))
        {
            return;
        }

        _lastRealSourcePath = includePath ? path : null;
        _lastRealSourceName = !string.IsNullOrWhiteSpace(path)
            ? Path.GetFileNameWithoutExtension(path)
            : name;
    }

    private static bool IsSelfSource(string? path, string? name)
    {
        return path?.Contains("Clip.Watcher", StringComparison.OrdinalIgnoreCase) == true ||
            path?.EndsWith($"{Path.DirectorySeparatorChar}Clip.exe", StringComparison.OrdinalIgnoreCase) == true ||
            path?.EndsWith($"{Path.AltDirectorySeparatorChar}Clip.exe", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Equals("Clip.Watcher", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Equals("Clip", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes);
    }
}

internal static class StartMenuIconLookup
{
    private static Dictionary<string, string>? _linksByName;

    public static Image? TryGetIcon(string appName)
    {
        try
        {
            var links = _linksByName ??= BuildIndex();
            if (!links.TryGetValue(Normalize(appName), out var linkPath))
            {
                return null;
            }

            return ShellIconReader.TryGetIcon(linkPath, large: false);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return null;
        }
    }

    private static Dictionary<string, string> BuildIndex()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var link in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                result.TryAdd(Normalize(Path.GetFileNameWithoutExtension(link)), link);
            }
        }

        Program.LogDebug($"OpenWith start-menu icon index count={result.Count}");
        return result;
    }

    private static string Normalize(string value)
    {
        var name = value.Trim().ToLowerInvariant();
        foreach (var suffix in new[] { " app", " file manager", " x64", " wow", " (preview)" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
            }
        }

        return name.Trim();
    }
}

internal static class PackageLogoLookup
{
    private static readonly Dictionary<string, Image?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Image? TryGetIcon(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        if (Cache.TryGetValue(appUserModelId, out var cached))
        {
            return cached is null ? null : new Bitmap(cached);
        }

        try
        {
            var packageFamily = appUserModelId.Split('!')[0];
            var installLocation = FindInstallLocation(packageFamily);
            if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            {
                Cache[appUserModelId] = null;
                return null;
            }

            var logo = Directory.EnumerateFiles(installLocation, "*.png", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("Logo", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault();
            if (logo is null)
            {
                Cache[appUserModelId] = null;
                return null;
            }

            using var source = Image.FromFile(logo);
            var image = new Bitmap(source);
            Cache[appUserModelId] = image;
            Program.LogDebug($"OpenWith package logo path={logo}");
            return new Bitmap(image);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            Cache[appUserModelId] = null;
            return null;
        }
    }

    private static string? FindInstallLocation(string packageFamily)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"(Get-AppxPackage | Where-Object {{$_.PackageFamilyName -eq '{packageFamily}'}} | Select-Object -First 1 -ExpandProperty InstallLocation)\"",
        });

        if (process is null || !process.WaitForExit(2500) || process.ExitCode != 0)
        {
            return null;
        }

        return process.StandardOutput.ReadToEnd().Trim();
    }
}

internal static class ShellIconReader
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiPidl = 0x000000008;
    private const uint SiigbfIconOnly = 0x00000004;
    private const uint SiigbfBiggersizeOk = 0x00000001;

    public static Image? TryGetIcon(string path, bool large)
    {
        try
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                return TryGetShellNamespaceIcon(path, large);
            }

            var info = new ShFileInfo();
            var flags = ShgfiIcon | (large ? ShgfiLargeIcon : ShgfiSmallIcon);
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            using var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            return icon.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    private static Image? TryGetShellNamespaceIcon(string parsingName, bool large)
    {
        var factoryIcon = TryGetShellItemImageFactoryIcon(parsingName, large);
        if (factoryIcon is not null)
        {
            Program.LogDebug($"OpenWith icon shell factory ok path={parsingName}");
            return factoryIcon;
        }

        var hr = SHParseDisplayName(parsingName, IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero)
        {
            Program.LogDebug($"OpenWith icon shell parse failed hr={hr} path={parsingName}");
            return null;
        }

        try
        {
            var info = new ShFileInfo();
            var flags = ShgfiIcon | ShgfiPidl | (large ? ShgfiLargeIcon : ShgfiSmallIcon);
            var result = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                Program.LogDebug($"OpenWith icon shell get failed path={parsingName}");
                return null;
            }

            using var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
            DestroyIcon(info.hIcon);
            return icon.ToBitmap();
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static Image? TryGetShellItemImageFactoryIcon(string parsingName, bool large)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        var hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory is null)
        {
            Program.LogDebug($"OpenWith icon shell factory create failed hr={hr} path={parsingName}");
            return null;
        }

        try
        {
            var size = large ? new NativeSize(64, 64) : new NativeSize(32, 32);
            hr = factory.GetImage(size, SiigbfIconOnly | SiigbfBiggersizeOk, out var hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
            {
                Program.LogDebug($"OpenWith icon shell factory image failed hr={hr} path={parsingName}");
                return null;
            }

            try
            {
                using var bitmap = Image.FromHbitmap(hbitmap);
                return new Bitmap(bitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            Cx = width;
            Cy = height;
        }

        public readonly int Cx;
        public readonly int Cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}


internal static class StaticDocumentPreviewRenderer
{
    public static bool IsSupported(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".doc" or ".xlsx" or ".xlsm" or ".xls" or ".pptx" or ".ppt" or ".vsdx" or ".vsd";
    }

    public static Image? TryRenderFirstPageOnStaThread(string path)
    {
        Image? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (TryRenderFirstPage(path, out var image))
                {
                    result = image;
                }
                else
                {
                    image.Dispose();
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error is not null)
        {
            Program.LogError(error);
        }

        return result;
    }

    public static bool TryRenderFirstPage(string path, out Image image)
    {
        image = new Bitmap(1, 1);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip", "document-previews");
            Directory.CreateDirectory(cacheRoot);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length)));

            if (extension is ".docx" or ".doc")
            {
                return TryRenderOfficePdf(path, Path.Combine(cacheRoot, fingerprint + ".word.pdf"), "Word.Application", ExportWordToPdf, out image);
            }

            if (extension is ".xlsx" or ".xlsm" or ".xls")
            {
                return TryRenderOfficePdf(path, Path.Combine(cacheRoot, fingerprint + ".excel.pdf"), "Excel.Application", ExportExcelToPdf, out image);
            }

            if (extension is ".pptx" or ".ppt")
            {
                var pngPath = Path.Combine(cacheRoot, fingerprint + ".powerpoint.png");
                return TryRenderImage(path, pngPath, "PowerPoint.Application", ExportPowerPointToPng, out image);
            }

            if (extension is ".vsdx" or ".vsd")
            {
                var pngPath = Path.Combine(cacheRoot, fingerprint + ".visio.png");
                return TryRenderImage(path, pngPath, "Visio.Application", ExportVisioToPng, out image);
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }

        image.Dispose();
        image = new Bitmap(1, 1);
        return false;
    }

    private static bool TryRenderOfficePdf(string sourcePath, string pdfPath, string progId, Action<dynamic, string, string> export, out Image image)
    {
        image = new Bitmap(1, 1);
        try
        {
            if (!File.Exists(pdfPath))
            {
                var app = CreateComApplication(progId);
                if (app is null)
                {
                    Program.LogDebug($"Static preview skipped progId={progId} path={sourcePath}");
                    return false;
                }

                try
                {
                    export(app, sourcePath, pdfPath);
                }
                finally
                {
                    QuitAndRelease(app);
                }
            }

            if (!File.Exists(pdfPath))
            {
                return false;
            }

            image.Dispose();
            return PdfPreviewRenderer.TryRenderFirstPage(pdfPath, out image);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            image.Dispose();
            image = new Bitmap(1, 1);
            return false;
        }
    }

    private static bool TryRenderImage(string sourcePath, string imagePath, string progId, Action<dynamic, string, string> export, out Image image)
    {
        image = new Bitmap(1, 1);
        try
        {
            if (!File.Exists(imagePath))
            {
                var app = CreateComApplication(progId);
                if (app is null)
                {
                    Program.LogDebug($"Static preview skipped progId={progId} path={sourcePath}");
                    return false;
                }

                try
                {
                    export(app, sourcePath, imagePath);
                }
                finally
                {
                    QuitAndRelease(app);
                }
            }

            if (!File.Exists(imagePath))
            {
                return false;
            }

            using var source = Image.FromFile(imagePath);
            image.Dispose();
            image = new Bitmap(source);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            image.Dispose();
            image = new Bitmap(1, 1);
            return false;
        }
    }

    private static dynamic? CreateComApplication(string progId)
    {
        var type = Type.GetTypeFromProgID(progId);
        return type is null ? null : Activator.CreateInstance(type);
    }

    private static void ExportWordToPdf(dynamic app, string sourcePath, string pdfPath)
    {
        app.Visible = false;
        app.DisplayAlerts = 0;
        dynamic? document = null;
        try
        {
            document = app.Documents.Open(FileName: sourcePath, ReadOnly: true, Visible: false);
            document.ExportAsFixedFormat(pdfPath, 17);
            Program.LogDebug($"Static preview Word exported path={sourcePath}");
        }
        finally
        {
            CloseAndRelease(document, false);
        }
    }

    private static void ExportExcelToPdf(dynamic app, string sourcePath, string pdfPath)
    {
        app.Visible = false;
        app.DisplayAlerts = false;
        dynamic? workbook = null;
        try
        {
            workbook = app.Workbooks.Open(Filename: sourcePath, ReadOnly: true);
            workbook.ExportAsFixedFormat(0, pdfPath);
            Program.LogDebug($"Static preview Excel exported path={sourcePath}");
        }
        finally
        {
            CloseAndRelease(workbook, false);
        }
    }

    private static void ExportPowerPointToPng(dynamic app, string sourcePath, string imagePath)
    {
        dynamic? presentation = null;
        try
        {
            presentation = app.Presentations.Open(sourcePath, true, false, false);
            dynamic slide = presentation.Slides[1];
            slide.Export(imagePath, "PNG", 1400, 1000);
            Program.LogDebug($"Static preview PowerPoint exported path={sourcePath}");
        }
        finally
        {
            CloseAndRelease(presentation, null);
        }
    }

    private static void ExportVisioToPng(dynamic app, string sourcePath, string imagePath)
    {
        app.Visible = false;
        dynamic? document = null;
        try
        {
            document = app.Documents.OpenEx(sourcePath, 66);
            dynamic page = document.Pages[1];
            page.Export(imagePath);
            Program.LogDebug($"Static preview Visio exported path={sourcePath}");
        }
        finally
        {
            CloseAndRelease(document, null);
        }
    }

    private static void CloseAndRelease(dynamic? comObject, object? argument)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (argument is null)
            {
                comObject.Close();
            }
            else
            {
                comObject.Close(argument);
            }
        }
        catch
        {
        }

        ReleaseComObject(comObject);
    }

    private static void QuitAndRelease(dynamic app)
    {
        try
        {
            app.Quit();
        }
        catch
        {
        }

        ReleaseComObject(app);
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }
}

internal static class PdfPreviewRenderer
{
    public static bool TryRenderFirstPage(string path, out Image image, int dpi = 120)
    {
        image = new Bitmap(1, 1);
        try
        {
            var tool = FindTool("pdftoppm.exe");
            if (tool is null)
            {
                Program.LogDebug("PDF preview skipped: pdftoppm.exe not found");
                image.Dispose();
                return false;
            }

            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip", "pdf-previews");
            Directory.CreateDirectory(cacheRoot);
            dpi = Math.Clamp(dpi, 72, 400);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length + "|" + dpi)));
            var outputPrefix = Path.Combine(cacheRoot, fingerprint);
            var outputFile = outputPrefix + ".png";
            if (!File.Exists(outputFile))
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = tool,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                }.WithArguments(args =>
                {
                    args.Add("-f");
                    args.Add("1");
                    args.Add("-singlefile");
                    args.Add("-png");
                    args.Add("-r");
                    args.Add(dpi.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    args.Add(path);
                    args.Add(outputPrefix);
                }));

                if (process is null || !process.WaitForExit(6000) || process.ExitCode != 0 || !File.Exists(outputFile))
                {
                    var error = process?.StandardError.ReadToEnd();
                    Program.LogDebug($"PDF preview failed exit={process?.ExitCode} error={error}");
                    image.Dispose();
                    return false;
                }
            }

            using var source = Image.FromFile(outputFile);
            image.Dispose();
            image = new Bitmap(source);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            image.Dispose();
            image = new Bitmap(1, 1);
            return false;
        }
    }

    private static string? FindTool(string fileName)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Directory.Exists(localAppData)
            ? Directory.EnumerateFiles(localAppData, fileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo info, Action<Collection<string>> configure)
    {
        configure(info.ArgumentList);
        return info;
    }
}

internal sealed record ExcelPreviewCell(string Value, Color? FillColor = null, bool Bold = false);

internal sealed record ExcelCellStyle(int FillId, int FontId);

internal static class ExcelPreviewReader
{
    private const int MaxRows = 35;
    private const int MaxColumns = 19;

    public static bool TryRead(string path, out ExcelPreviewCell[,] cells)
    {
        cells = new ExcelPreviewCell[MaxRows, MaxColumns];
        for (var row = 0; row < MaxRows; row++)
        {
            for (var column = 0; column < MaxColumns; column++)
            {
                cells[row, column] = new ExcelPreviewCell(string.Empty);
            }
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var sharedStrings = ReadSharedStrings(archive);
            var styles = ReadStyles(archive);
            var sheetPath = FirstWorksheetPath(archive);
            if (sheetPath is null)
            {
                return false;
            }

            var sheetEntry = archive.GetEntry(sheetPath);
            if (sheetEntry is null)
            {
                return false;
            }

            using var stream = sheetEntry.Open();
            var document = XDocument.Load(stream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            foreach (var cell in document.Descendants(ns + "c"))
            {
                var reference = cell.Attribute("r")?.Value;
                if (!TryParseCellReference(reference, out var row, out var column) || row > MaxRows || column > MaxColumns)
                {
                    continue;
                }

                var styleIndex = int.TryParse(cell.Attribute("s")?.Value, out var parsedStyle) ? parsedStyle : -1;
                var style = styles.StyleAt(styleIndex);
                cells[row - 1, column - 1] = new ExcelPreviewCell(
                    ReadCellValue(cell, ns, sharedStrings),
                    styles.FillAt(style.FillId),
                    styles.BoldAt(style.FontId));
            }

            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }

    private static ExcelStyleBook ReadStyles(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/styles.xml");
        if (entry is null)
        {
            return ExcelStyleBook.Empty;
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var fills = document.Descendants(ns + "fills").Elements(ns + "fill")
            .Select(fill =>
            {
                var rgb = fill.Descendants(ns + "fgColor").FirstOrDefault()?.Attribute("rgb")?.Value;
                return ParseColor(rgb);
            })
            .ToList();
        var boldFonts = document.Descendants(ns + "fonts").Elements(ns + "font")
            .Select(font => font.Element(ns + "b") is not null)
            .ToList();
        var cellFormats = document.Descendants(ns + "cellXfs").Elements(ns + "xf")
            .Select(xf => new ExcelCellStyle(
                int.TryParse(xf.Attribute("fillId")?.Value, out var fillId) ? fillId : 0,
                int.TryParse(xf.Attribute("fontId")?.Value, out var fontId) ? fontId : 0))
            .ToList();
        return new ExcelStyleBook(fills, boldFonts, cellFormats);
    }

    private static Color? ParseColor(string? rgb)
    {
        if (string.IsNullOrWhiteSpace(rgb))
        {
            return null;
        }

        if (rgb.Length == 8)
        {
            rgb = rgb[2..];
        }

        if (rgb.Length != 6)
        {
            return null;
        }

        try
        {
            return Color.FromArgb(
                Convert.ToInt32(rgb[..2], 16),
                Convert.ToInt32(rgb.Substring(2, 2), 16),
                Convert.ToInt32(rgb.Substring(4, 2), 16));
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value)))
            .ToList();
    }

    private static string? FirstWorksheetPath(ZipArchive archive)
    {
        if (archive.GetEntry("xl/worksheets/sheet1.xml") is not null)
        {
            return "xl/worksheets/sheet1.xml";
        }

        return archive.Entries
            .Select(entry => entry.FullName)
            .FirstOrDefault(name => name.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadCellValue(XElement cell, XNamespace ns, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        var value = cell.Element(ns + "v")?.Value ?? string.Empty;
        if (type == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex];
        }

        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(ns + "t").Select(text => text.Value));
        }

        return value;
    }

    private static bool TryParseCellReference(string? reference, out int row, out int column)
    {
        row = 0;
        column = 0;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var index = 0;
        while (index < reference.Length && char.IsLetter(reference[index]))
        {
            column = column * 26 + (char.ToUpperInvariant(reference[index]) - 'A' + 1);
            index++;
        }

        return column > 0 && int.TryParse(reference[index..], out row) && row > 0;
    }
}

internal sealed class ExcelStyleBook(List<Color?> fills, List<bool> boldFonts, List<ExcelCellStyle> cellFormats)
{
    public static ExcelStyleBook Empty { get; } = new([], [], []);

    public ExcelCellStyle StyleAt(int index)
    {
        return index >= 0 && index < cellFormats.Count ? cellFormats[index] : new ExcelCellStyle(0, 0);
    }

    public Color? FillAt(int index)
    {
        return index >= 0 && index < fills.Count ? fills[index] : null;
    }

    public bool BoldAt(int index)
    {
        return index >= 0 && index < boldFonts.Count && boldFonts[index];
    }
}

