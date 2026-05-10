using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Clip.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using Svg;

namespace Clip.Watcher;

internal static class Program
{
    private static readonly ClipboardHistoryStore Store = new();

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);

        if (args.Length > 0 && !args[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            return RunCommand(args);
        }

        using var form = new ClipboardWatcherForm(Store);
        Application.Run(form);
        return 0;
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
                    ListItems();
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
                case "edit":
                    Store.EditText(id, string.Join(' ', args.Skip(2)));
                    return 0;
                case "append":
                    AppendText(id);
                    return 0;
                case "save":
                    Console.WriteLine(Store.SaveAsFile(id, args.Length > 2 ? args[2] : null));
                    return 0;
                case "open":
                    OpenItem(id, args.Length > 2 ? args[2] : null);
                    return 0;
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

    private static void ListItems()
    {
        foreach (var item in Store.GetItems().OrderByDescending(i => i.IsPinned).ThenBy(i => i.PinOrder).ThenByDescending(i => i.LastUsedAt))
        {
            var pin = item.IsPinned ? "PIN" : "   ";
            Console.WriteLine($"{pin} {item.Id} [{item.Kind}] {item.Preview}");
        }
    }

    private static void SetClipboard(string id, bool paste)
    {
        var item = Store.GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");

        if (item.Kind == ClipboardItemKind.Text)
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

    private static void OpenItem(string id, string? appPath)
    {
        var item = Store.GetItem(id) ?? throw new InvalidOperationException("Clipboard item not found.");
        if (item.Kind != ClipboardItemKind.Image || item.AssetPath is null)
        {
            throw new InvalidOperationException("Only image items can be opened right now.");
        }

        var startInfo = appPath is null
            ? new ProcessStartInfo(item.AssetPath) { UseShellExecute = true }
            : new ProcessStartInfo(appPath, $"\"{item.AssetPath}\"") { UseShellExecute = true };

        Process.Start(startInfo);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Clip commands:");
        Console.WriteLine("  watch");
        Console.WriteLine("  list");
        Console.WriteLine("  copy <id>");
        Console.WriteLine("  paste <id>");
        Console.WriteLine("  pin|unpin|up|down|delete <id>");
        Console.WriteLine("  edit <id> <new text>");
        Console.WriteLine("  append <id>");
        Console.WriteLine("  save <id> [path]");
        Console.WriteLine("  open <id> [app path]");
    }

    internal static void LogError(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clip",
                "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    internal static void LogDebug(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clip",
                "debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch
        {
        }
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

internal static class ClipTheme
{
    public static readonly bool IsDark = IsWindowsDarkMode();
    public static readonly Color AppBackground = IsDark ? Color.FromArgb(11, 34, 42) : Color.FromArgb(243, 241, 236);
    public static readonly Color Surface = IsDark ? Color.FromArgb(31, 27, 23) : Color.FromArgb(255, 255, 255);
    public static readonly Color ControlBackground = IsDark ? Color.FromArgb(23, 21, 18) : Color.FromArgb(244, 243, 239);
    public static readonly Color PreviewBackground = IsDark ? Color.FromArgb(25, 23, 20) : Color.FromArgb(251, 250, 247);
    public static readonly Color Border = IsDark ? Color.FromArgb(52, 47, 40) : Color.FromArgb(216, 213, 204);
    public static readonly Color Divider = IsDark ? Color.FromArgb(58, 52, 44) : Color.FromArgb(232, 228, 218);
    public static readonly Color Text = IsDark ? Color.FromArgb(244, 242, 237) : Color.FromArgb(26, 24, 22);
    public static readonly Color MutedText = IsDark ? Color.FromArgb(155, 148, 136) : Color.FromArgb(122, 117, 108);
    public static readonly Color Accent = IsDark ? Color.FromArgb(53, 169, 186) : Color.FromArgb(43, 154, 173);
    public static readonly Color Selection = IsDark ? Color.FromArgb(18, 63, 70) : Color.FromArgb(216, 241, 239);
    public static readonly Color SelectionBorder = IsDark ? Color.FromArgb(25, 141, 158) : Color.FromArgb(124, 203, 208);
    public static readonly Color Footer = IsDark ? Color.FromArgb(26, 23, 19) : Color.FromArgb(244, 243, 239);
    public static readonly Color Chip = IsDark ? Color.FromArgb(38, 34, 29) : Color.FromArgb(236, 234, 227);
    public static readonly Color OpenButton = IsDark ? Color.FromArgb(44, 156, 171) : Color.FromArgb(33, 132, 148);

    public static readonly Font UiFont = new("Segoe UI Variable Text", 9.5f);
    public static readonly Font InfoFont = new("Segoe UI Variable Text", 9f);
    public static readonly Font TitleFont = new("Segoe UI Variable Display", 10.5f, FontStyle.Bold);
    public static readonly Font SmallCapsFont = new("Segoe UI Variable Text", 8.25f, FontStyle.Bold);
    public static readonly Font MonoFont = new("Cascadia Mono", 9.5f);

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class ClipMenuColorTable : ProfessionalColorTable
{
    public override Color MenuItemSelected => ClipTheme.Selection;
    public override Color MenuItemBorder => ClipTheme.SelectionBorder;
    public override Color ToolStripDropDownBackground => ClipTheme.Surface;
    public override Color ImageMarginGradientBegin => ClipTheme.Surface;
    public override Color ImageMarginGradientMiddle => ClipTheme.Surface;
    public override Color ImageMarginGradientEnd => ClipTheme.Surface;
    public override Color SeparatorDark => ClipTheme.Divider;
    public override Color SeparatorLight => ClipTheme.Divider;
}

internal sealed class ClipboardWatcherForm : Form
{
    private const int WmClipboardUpdate = 0x031D;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 7001;
    private const uint ModAlt = 0x0001;
    private const uint VkV = 0x56;
    private readonly ClipboardHistoryStore _store;
    private readonly NotifyIcon _trayIcon = new();
    private ClipboardPaletteForm? _palette;
    private bool _clipboardListenerRegistered;
    private bool _hotkeyRegistered;
    private bool _paletteLoadQueued;
    private string? _lastRealSourceName;
    private string? _lastRealSourcePath;

    public ClipboardWatcherForm(ClipboardHistoryStore store)
    {
        _store = store;
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        _trayIcon.Text = "Clip";
        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => ShowPalette();
        EnsureWatcherHooks(showWarning: true);
        Program.LogDebug($"Clip watcher started handle={Handle} hotkeyAltV={_hotkeyRegistered} win32={Marshal.GetLastWin32Error()} dark={ClipTheme.IsDark}");
        BeginDeferredStartupWork();
    }

    private void BeginDeferredStartupWork()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            BeginInvokeIfAlive(CaptureCurrentClipboard);

            await Task.Delay(500);
            BeginInvokeIfAlive(WarmPalette);
        });
    }

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
        EnsureWatcherHooks(showWarning: false);
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
            CaptureCurrentClipboard();
            RefreshPaletteIfOpen();
        }

        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            Program.LogDebug($"Alt+V hotkey received handle={Handle} visible={_palette?.Visible == true}");
            try
            {
                ShowPalette();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                _palette?.Dispose();
                _palette = null;
            }
        }

        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.L))
        {
            _palette?.WriteDebugSnapshot();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        ReleaseWatcherHooks();
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowPalette()
    {
        try
        {
            Program.LogDebug("Palette open requested");
            if (_palette is null || _palette.IsDisposed)
            {
                _palette = new ClipboardPaletteForm(_store);
            }

            var wasVisible = _palette.Visible;
            _palette.ShowPaletteWindow();
            if (wasVisible)
            {
                Program.LogDebug("Palette already visible; focused without reload");
            }
            else if (!_palette.IsWarm)
            {
                QueuePaletteLoad(_palette);
            }
            else
            {
                _palette.RefreshSelectedPreviewSoon();
                Program.LogDebug("Palette opened from warm cache");
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            try
            {
                _palette?.Dispose();
            }
            catch
            {
            }

            _palette = null;
        }
    }

    private void WarmPalette()
    {
        try
        {
            if (_palette is null || _palette.IsDisposed)
            {
                _palette = new ClipboardPaletteForm(_store);
            }

            _palette.WarmHidden();
            Program.LogDebug("Palette warmup completed");
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    private void QueuePaletteLoad(ClipboardPaletteForm palette)
    {
        if (_paletteLoadQueued)
        {
            Program.LogDebug("Palette load already queued");
            return;
        }

        _paletteLoadQueued = true;
        palette.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!palette.IsDisposed)
                {
                    palette.LoadItems();
                    Program.LogDebug("Palette load completed after show");
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            finally
            {
                _paletteLoadQueued = false;
            }
        }));
    }

    private void RefreshPaletteIfOpen()
    {
        try
        {
            if (_palette is { IsDisposed: false })
            {
                if (_palette.Visible)
                {
                    QueuePaletteLoad(_palette);
                }
                else
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_palette is { IsDisposed: false, Visible: false })
                        {
                            _palette.LoadItems(refreshPreview: false);
                            Program.LogDebug("Hidden palette refreshed after clipboard update");
                        }
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
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
            Program.LogDebug($"Clipboard listener registered={_clipboardListenerRegistered} handle={Handle} win32={Marshal.GetLastWin32Error()}");
        }

        if (!_hotkeyRegistered)
        {
            _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, ModAlt, VkV);
            var win32 = Marshal.GetLastWin32Error();
            Program.LogDebug($"Alt+V hotkey registered={_hotkeyRegistered} handle={Handle} win32={win32}");
            if (!_hotkeyRegistered && showWarning)
            {
                MessageBox.Show(
                    "Clip could not register Alt+V. Another app is already using it.",
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
            Program.LogDebug($"Alt+V hotkey unregistered={released} handle={Handle} win32={Marshal.GetLastWin32Error()}");
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
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count > 0)
                {
                    _store.AddOrUpdate(new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Files,
                        FilePaths = files,
                        Preview = string.Join(", ", files.Select(Path.GetFileName)),
                        ContentHash = HashText(string.Join("|", files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))),
                        SourceApplication = SourceName(),
                        SourceApplicationPath = SourcePath(),
                    });
                }
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (ClipboardPathText.TryParseExistingFilePaths(text, out var paths))
                {
                    _store.AddOrUpdate(new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Files,
                        FilePaths = paths,
                        Preview = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files",
                        ContentHash = HashText(string.Join("|", paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))),
                        SourceApplication = SourceName(),
                        SourceApplicationPath = SourcePath(),
                    });
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    _store.AddOrUpdate(new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Text,
                        Text = text,
                        Preview = ClipboardHistoryStore.PreviewText(text),
                        ContentHash = HashText(text),
                        SourceApplication = SourceName(),
                        SourceApplicationPath = SourcePath(),
                    });
                }
            }
            else if (Clipboard.ContainsImage())
            {
                var assetPath = _store.NewAssetFilePath(".png");
                var image = Clipboard.GetImage();
                image?.Save(assetPath, System.Drawing.Imaging.ImageFormat.Png);
                var width = image?.Width;
                var height = image?.Height;
                _store.AddOrUpdate(new ClipboardHistoryItem
                {
                    Kind = ClipboardItemKind.Image,
                    AssetPath = assetPath,
                    Preview = width is not null && height is not null ? $"Image {width} x {height}" : "Image",
                    ContentHash = File.Exists(assetPath) ? HashFile(assetPath) : null,
                    ImageWidth = width,
                    ImageHeight = height,
                    SourceApplication = SourceName(),
                    SourceApplicationPath = SourcePath(),
                });
            }
        }
        catch
        {
            // Some apps lock or clear clipboard formats briefly. Ignore and catch the next update.
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

    private static string? ForegroundAppName()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
            return processId == 0 ? null : Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? ForegroundAppPath()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
            return processId == 0 ? null : Process.GetProcessById((int)processId).MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private string? SourceName()
    {
        UpdateSourceSnapshot();
        return _lastRealSourceName;
    }

    private string? SourcePath()
    {
        UpdateSourceSnapshot();
        return _lastRealSourcePath;
    }

    private void UpdateSourceSnapshot()
    {
        var path = ForegroundAppPath();
        var name = ForegroundAppName();
        if (IsSelfSource(path, name))
        {
            return;
        }

        _lastRealSourcePath = path;
        _lastRealSourceName = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? Path.GetFileNameWithoutExtension(path)
            : name;
    }

    private static bool IsSelfSource(string? path, string? name)
    {
        return path?.Contains("Clip.Watcher", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Equals("Clip.Watcher", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes);
    }
}

internal sealed class ClipboardPaletteForm : Form
{
    private readonly ClipboardHistoryStore _store;
    private readonly SplitContainer _split = new();
    private readonly TextBox _search = new();
    private readonly ListView _list = new();
    private readonly ImageList _smallImages = new();
    private readonly Dictionary<string, Image> _thumbnailImages = [];
    private readonly TextBox _preview = new();
    private NativeScrollOverlay? _listScrollOverlay;
    private NativeScrollOverlay? _previewScrollOverlay;
    private readonly Panel _previewHeader = new();
    private readonly PictureBox _previewHeaderIcon = new();
    private readonly Label _previewHeaderTitle = new();
    private readonly Label _previewHeaderSubtitle = new();
    private readonly Button _previewOpenButton = new();
    private readonly Panel _footerBar = new();
    private readonly FlowLayoutPanel _footerLeft = new();
    private readonly FlowLayoutPanel _footerRight = new();
    private readonly Button _settingsButton = new();
    private readonly Panel _previewPanel = new();
    private readonly Panel _infoPanel = new();
    private readonly Panel _infoRowsHost = new();
    private readonly Panel _infoRowsScroll = new();
    private readonly Panel _infoScrollGutter = new();
    private readonly ThinVScrollBar _infoScrollBar = new();
    private readonly Label _infoTitle = new();
    private readonly TableLayoutPanel _infoRows = new();
    private readonly ImagePreviewBox _imagePreview = new();
    private readonly ShellPreviewPanel _shellPreview = new();
    private readonly ExcelPreviewGrid _excelPreview = new();
    private readonly WebView2 _htmlPreview = new();
    private readonly ToastBanner _toast = new();
    private readonly System.Windows.Forms.Timer _toastTimer = new();
    private readonly PaletteShortcutMessageFilter _shortcutFilter;
    private readonly List<ClipboardHistoryItem> _items = [];
    private readonly List<ClipboardHistoryItem> _sourceItems = [];
    private readonly Dictionary<string, InfoRowControls> _infoRowControls = [];
    private readonly List<string> _infoRowOrder = [];
    private readonly Panel _searchHost = new();
    private readonly FlowLayoutPanel _filterHost = new();
    private readonly Dictionary<ClipboardFilter, List<Button>> _filterButtons = [];
    private readonly ComboBox _fileTypeFilter = new();
    private readonly ComboBox _dateFilter = new();
    private readonly ContextMenuStrip _allFilterMenu = new();
    private readonly ContextMenuStrip _fileFilterMenu = new();
    private ClipboardFilter _filter = ClipboardFilter.All;
    private string _selectedFileType = AllFilterValue;
    private string _selectedDateFilter = AllFilterValue;
    private bool _updatingFilterControls;
    private string? _sourceQuery;
    private bool _keepOpenForModal;
    private bool _suppressSelectionPreview;
    private string? _lastRenderedItemId;
    private int _previewRenderVersion;
    private int _infoWheelRemainder;
    private DateTime _suppressDeactivateUntil = DateTime.MinValue;
    private const string AllFilterValue = "All";

    public bool IsWarm { get; private set; }

    public ClipboardPaletteForm(ClipboardHistoryStore store)
    {
        _store = store;
        _shortcutFilter = new PaletteShortcutMessageFilter(this, WriteDebugSnapshot);
        Application.AddMessageFilter(_shortcutFilter);
        Text = "Clip";
        Width = 900;
        Height = 600;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = ClipTheme.AppBackground;
        Font = ClipTheme.UiFont;

        _smallImages.ImageSize = new Size(54, 54);
        _smallImages.ColorDepth = ColorDepth.Depth32Bit;
        Program.LogDebug($"Clip UI theme dark={ClipTheme.IsDark} appBg={ClipTheme.AppBackground} surface={ClipTheme.Surface} text={ClipTheme.Text}");

        _split.Dock = DockStyle.Fill;
        _split.BackColor = ClipTheme.Border;
        _split.Panel1MinSize = 120;
        _split.Panel2MinSize = 120;
        _split.SplitterWidth = 1;

        Shown += (_, _) => ApplyPreviewRatio();
        Resize += (_, _) => ApplyPreviewRatio();

        var split = _split;

        _search.Dock = DockStyle.Top;
        _search.PlaceholderText = "Search clipboard history";
        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = ClipTheme.ControlBackground;
        _search.ForeColor = ClipTheme.Text;
        _search.Font = ClipTheme.UiFont;
        _search.Margin = new Padding(10, 8, 10, 8);
        _search.GotFocus += (_, _) => _searchHost.Invalidate();
        _search.LostFocus += (_, _) => _searchHost.Invalidate();
        _search.TextChanged += (_, _) => LoadItems();

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Scrollable = true;
        _list.HeaderStyle = ColumnHeaderStyle.None;
        _list.BackColor = ClipTheme.Surface;
        _list.ForeColor = ClipTheme.Text;
        _list.Font = ClipTheme.UiFont;
        _list.OwnerDraw = true;
        _list.SmallImageList = _smallImages;
        _list.Columns.Add("Clipboard", 300);
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += DrawClipboardSubItem;
        _list.DrawItem += (_, _) => { };
        _list.SelectedIndexChanged += (_, _) => ShowSelectedPreview();
        _list.DoubleClick += (_, _) => PasteSelected();
        _list.KeyDown += OnListKeyDown;
        _list.MouseDown += OnListMouseDown;
        _list.MouseWheel += (_, _) => _listScrollOverlay?.SyncFromNative();
        _list.MouseEnter += (_, _) => _list.Focus();

        _searchHost.Dock = DockStyle.Top;
        _searchHost.Height = 42;
        _searchHost.Padding = new Padding(10, 9, 10, 7);
        _searchHost.BackColor = ClipTheme.Surface;
        _searchHost.Paint += DrawSearchHost;
        _searchHost.Controls.Add(_search);

        _filterHost.Dock = DockStyle.Top;
        _filterHost.Height = 40;
        _filterHost.Padding = new Padding(10, 2, 10, 6);
        _filterHost.BackColor = ClipTheme.Surface;
        _filterHost.Controls.Add(CreateSplitFilterButton("All", ClipboardFilter.All, _allFilterMenu));
        _filterHost.Controls.Add(CreateFilterButton("Text", ClipboardFilter.Text));
        _filterHost.Controls.Add(CreateFilterButton("Images", ClipboardFilter.Images));
        _filterHost.Controls.Add(CreateSplitFilterButton("Files", ClipboardFilter.Files, _fileFilterMenu));
        ConfigureFilterDropdown(_fileTypeFilter, width: 122);
        ConfigureFilterDropdown(_dateFilter, width: 112);
        _fileTypeFilter.Visible = false;
        _dateFilter.Visible = false;
        _fileTypeFilter.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingFilterControls)
            {
                return;
            }

            _selectedFileType = _fileTypeFilter.SelectedItem?.ToString() ?? AllFilterValue;
            Program.LogDebug($"File type filter selected value={_selectedFileType}");
            LoadItems();
        };
        _dateFilter.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingFilterControls)
            {
                return;
            }

            _selectedDateFilter = _dateFilter.SelectedItem?.ToString() ?? AllFilterValue;
            Program.LogDebug($"Date filter selected value={_selectedDateFilter}");
            LoadItems();
        };
        _filterHost.Controls.Add(_fileTypeFilter);
        _filterHost.Controls.Add(_dateFilter);
        _allFilterMenu.BackColor = ClipTheme.ControlBackground;
        _allFilterMenu.ForeColor = ClipTheme.Text;
        _fileFilterMenu.BackColor = ClipTheme.ControlBackground;
        _fileFilterMenu.ForeColor = ClipTheme.Text;

        ConfigurePreviewHeader();
        ConfigureFooterBar();

        var left = new Panel { Dock = DockStyle.Fill, BackColor = ClipTheme.Surface, Padding = new Padding(0, 0, 0, 8) };
        left.Controls.Add(_list);
        left.Controls.Add(_filterHost);
        left.Controls.Add(_searchHost);
        left.Resize += (_, _) => UpdateClipboardColumnWidth();
        _listScrollOverlay = new NativeScrollOverlay(_list, vertical: true, horizontal: true);

        _preview.Dock = DockStyle.Fill;
        _preview.Multiline = true;
        _preview.ReadOnly = true;
        _preview.WordWrap = true;
        _preview.ScrollBars = ScrollBars.Vertical;
        _preview.Font = ClipTheme.MonoFont;
        _preview.BorderStyle = BorderStyle.None;
        _preview.BackColor = ClipTheme.PreviewBackground;
        _preview.ForeColor = ClipTheme.Text;
        _preview.MouseWheel += (_, _) => _previewScrollOverlay?.SyncFromNative();

        _imagePreview.Dock = DockStyle.Fill;
        _imagePreview.BackColor = ClipTheme.PreviewBackground;
        _imagePreview.Visible = false;

        _shellPreview.Dock = DockStyle.Fill;
        _shellPreview.BackColor = ClipTheme.PreviewBackground;
        _shellPreview.Visible = false;
        _shellPreview.UserInteracted += (_, _) =>
        {
            _suppressDeactivateUntil = DateTime.UtcNow.AddSeconds(2);
        };

        _htmlPreview.Dock = DockStyle.Fill;
        _htmlPreview.Visible = false;
        _htmlPreview.DefaultBackgroundColor = ClipTheme.PreviewBackground;

        _excelPreview.Dock = DockStyle.Fill;
        _excelPreview.Visible = false;
        _excelPreview.ReadOnly = true;
        _excelPreview.AllowUserToAddRows = false;
        _excelPreview.AllowUserToDeleteRows = false;
        _excelPreview.AllowUserToResizeRows = false;
        _excelPreview.RowHeadersWidth = 52;
        _excelPreview.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _excelPreview.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _excelPreview.BackgroundColor = ClipTheme.PreviewBackground;
        _excelPreview.BorderStyle = BorderStyle.None;
        _excelPreview.MouseDown += (_, _) => MarkPreviewInteraction();
        _excelPreview.CellMouseDown += (_, _) => MarkPreviewInteraction();
        _excelPreview.Scroll += (_, _) => MarkPreviewInteraction();

        _infoPanel.Dock = DockStyle.Bottom;
        _infoPanel.Height = 205;
        _infoPanel.Padding = new Padding(12, 8, 12, 8);
        _infoPanel.AutoScroll = false;
        _infoPanel.BackColor = ClipTheme.Surface;

        _infoTitle.Text = "INFORMATION";
        _infoTitle.Dock = DockStyle.Top;
        _infoTitle.Height = 28;
        _infoTitle.TextAlign = ContentAlignment.MiddleLeft;
        _infoTitle.Font = ClipTheme.SmallCapsFont;
        _infoTitle.ForeColor = ClipTheme.Text;
        _infoTitle.BackColor = ClipTheme.Surface;

        _infoRowsHost.Dock = DockStyle.Fill;
        _infoRowsHost.BackColor = ClipTheme.Surface;

        _infoRowsScroll.Dock = DockStyle.Fill;
        _infoRowsScroll.AutoScroll = false;
        _infoRowsScroll.Padding = new Padding(0);
        _infoRowsScroll.BackColor = ClipTheme.Surface;
        _infoRowsScroll.Resize += (_, _) => UpdateInfoScrollBar();
        _infoRowsScroll.MouseWheel += OnInfoRowsMouseWheel;
        _infoRowsScroll.MouseEnter += (_, _) => _infoRowsScroll.Focus();

        _infoScrollGutter.Dock = DockStyle.Right;
        _infoScrollGutter.Width = 8;
        _infoScrollGutter.BackColor = ClipTheme.Surface;

        _infoScrollBar.Dock = DockStyle.Fill;
        _infoScrollBar.Visible = false;
        _infoScrollBar.ValueChanged += (_, _) =>
        {
            Program.LogDebug($"InfoRows thin scrollbar value={_infoScrollBar.Value}");
            ScrollInfoRowsTo(_infoScrollBar.Value);
        };

        _infoRows.Dock = DockStyle.None;
        _infoRows.Location = Point.Empty;
        _infoRows.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _infoRows.AutoSize = true;
        _infoRows.BackColor = ClipTheme.Surface;
        _infoRows.ColumnCount = 2;
        _infoRows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        _infoRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _infoRows.MouseWheel += OnInfoRowsMouseWheel;

        _infoRowsScroll.Controls.Add(_infoRows);
        _infoScrollGutter.Controls.Add(_infoScrollBar);
        _infoRowsHost.Controls.Add(_infoRowsScroll);
        _infoRowsHost.Controls.Add(_infoScrollGutter);
        _infoPanel.Controls.Add(_infoRowsHost);
        _infoPanel.Controls.Add(_infoTitle);

        _previewPanel.Dock = DockStyle.Fill;
        _previewPanel.BackColor = ClipTheme.PreviewBackground;
        _previewPanel.Padding = new Padding(12);
        _previewPanel.Controls.Add(_shellPreview);
        _previewPanel.Controls.Add(_htmlPreview);
        _previewPanel.Controls.Add(_excelPreview);
        _previewPanel.Controls.Add(_imagePreview);
        _previewPanel.Controls.Add(_preview);
        _previewScrollOverlay = new NativeScrollOverlay(_preview, vertical: true, horizontal: true);

        var rightLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        rightLayout.BackColor = ClipTheme.PreviewBackground;
        rightLayout.Margin = Padding.Empty;
        rightLayout.Padding = Padding.Empty;
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rightLayout.Controls.Add(_previewHeader, 0, 0);
        rightLayout.Controls.Add(_previewPanel, 0, 1);
        rightLayout.Controls.Add(_infoPanel, 0, 2);

        split.Panel1.Controls.Add(left);
        split.Panel1.BackColor = ClipTheme.Surface;
        split.Panel2.Controls.Add(rightLayout);
        split.Panel2.BackColor = ClipTheme.PreviewBackground;
        Controls.Add(split);
        Controls.Add(_footerBar);

        _toast.BackColor = Color.Transparent;
        _toast.Visible = false;
        Controls.Add(_toast);
        _toast.BringToFront();
        _toastTimer.Interval = 2500;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.Visible = false;
        };
        Resize += (_, _) => PositionToast();
        Shown += (_, _) =>
        {
            NativeDarkMode.ApplyToTree(this);
            Program.LogDebug("Clip dark mode applied to control tree");
            _listScrollOverlay?.SyncFromNative();
            _previewScrollOverlay?.SyncFromNative();
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };

        Deactivate += (_, _) =>
        {
            if (!_keepOpenForModal)
            {
                BeginInvoke(new Action(() =>
                {
                    if (_keepOpenForModal)
                    {
                        return;
                    }

                    if (DateTime.UtcNow < _suppressDeactivateUntil || IsCursorInsideThisWindow())
                    {
                        Program.LogDebug($"Palette deactivate suppressed shellPreview={_shellPreview.Visible} cursorInside={IsCursorInsideThisWindow()}");
                        return;
                    }

                    if (!IsForegroundInsideThisWindow())
                    {
                        Program.LogDebug($"Palette dismissed on deactivate shellPreview={_shellPreview.Visible}");
                        Hide();
                    }
                }));
            }
        };

        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(2500);
                var warmupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "warmup.pdf");
                var apps = AppDiscovery.GetApps(warmupPath);
                OpenWithPickerForm.WarmIconCache(apps.Take(60));
                Program.LogDebug("OpenWith app cache warmed");
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        });
    }

    public void WarmHidden()
    {
        if (IsDisposed)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        CreateControl();
        ForceCreateHandles(this);
        NativeDarkMode.ApplyToTree(this);
        LoadItems(refreshPreview: true);
        BeginWarmHtmlPreview();
        IsWarm = true;
        Program.LogDebug($"Palette hidden warm elapsedMs={watch.ElapsedMilliseconds}");
    }

    public void RefreshSelectedPreviewSoon()
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(new Action(ShowSelectedPreview));
    }

    private static void ForceCreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls)
        {
            ForceCreateHandles(child);
        }
    }

    private void BeginWarmHtmlPreview()
    {
        BeginInvoke(new Action(async () =>
        {
            try
            {
                var watch = Stopwatch.StartNew();
                await _htmlPreview.EnsureCoreWebView2Async();
                if (_htmlPreview.CoreWebView2 is not null)
                {
                    _htmlPreview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _htmlPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                }

                Program.LogDebug($"Preview html webview warm elapsedMs={watch.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.L))
        {
            WriteDebugSnapshot();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(_shortcutFilter);
        }

        base.Dispose(disposing);
    }

    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    public void ShowPaletteWindow()
    {
        if (IsDisposed)
        {
            return;
        }

        MoveToMouseScreen();

        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        _suppressDeactivateUntil = DateTime.UtcNow.AddMilliseconds(750);
        TopMost = true;
        BringToFront();
        Activate();
        SetForegroundWindow(Handle);
        FocusSearch();
        TopMost = false;
        Program.LogDebug($"Palette shown handle={Handle} visible={Visible} focused={ContainsFocus}");
    }

    private void MoveToMouseScreen()
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        var x = screen.Left + Math.Max(0, (screen.Width - Width) / 2);
        var y = screen.Top + Math.Max(0, (screen.Height - Height) / 2);
        Location = new Point(x, y);
        Program.LogDebug($"Palette positioned screen={screen.Left},{screen.Top},{screen.Width}x{screen.Height} mouse={Cursor.Position.X},{Cursor.Position.Y} location={Location.X},{Location.Y}");
    }

    private bool IsForegroundInsideThisWindow()
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && (foreground == Handle || IsChild(Handle, foreground));
    }

    private void MarkPreviewInteraction()
    {
        _suppressDeactivateUntil = DateTime.UtcNow.AddSeconds(2);
    }

    private bool IsCursorInsideThisWindow()
    {
        return Visible && Bounds.Contains(Cursor.Position);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    private void ApplyPreviewRatio()
    {
        if (WindowState == FormWindowState.Minimized || _split.Width <= 0)
        {
            return;
        }

        try
        {
            var leftWidth = (int)(_split.Width * 0.40);
            var min = _split.Panel1MinSize;
            var max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
            if (max <= min)
            {
                return;
            }

            _split.SplitterDistance = Math.Clamp(leftWidth, min, max);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    private void ConfigurePreviewHeader()
    {
        _previewHeader.Dock = DockStyle.Fill;
        _previewHeader.Height = 62;
        _previewHeader.Padding = new Padding(14, 10, 14, 8);
        _previewHeader.BackColor = ClipTheme.PreviewBackground;

        _previewHeaderIcon.Width = 32;
        _previewHeaderIcon.Dock = DockStyle.Left;
        _previewHeaderIcon.SizeMode = PictureBoxSizeMode.Zoom;
        _previewHeaderIcon.Margin = new Padding(0, 0, 10, 0);

        var titleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = ClipTheme.PreviewBackground,
            Margin = Padding.Empty,
            Padding = new Padding(10, 0, 0, 0),
        };
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        _previewHeaderTitle.Dock = DockStyle.Fill;
        _previewHeaderTitle.TextAlign = ContentAlignment.BottomLeft;
        _previewHeaderTitle.Font = ClipTheme.TitleFont;
        _previewHeaderTitle.ForeColor = ClipTheme.Text;
        _previewHeaderTitle.BackColor = ClipTheme.PreviewBackground;

        _previewHeaderSubtitle.Dock = DockStyle.Fill;
        _previewHeaderSubtitle.TextAlign = ContentAlignment.TopLeft;
        _previewHeaderSubtitle.Font = ClipTheme.InfoFont;
        _previewHeaderSubtitle.ForeColor = ClipTheme.MutedText;
        _previewHeaderSubtitle.BackColor = ClipTheme.PreviewBackground;

        titleStack.Controls.Add(_previewHeaderTitle, 0, 0);
        titleStack.Controls.Add(_previewHeaderSubtitle, 0, 1);

        _previewOpenButton.Text = "Open";
        _previewOpenButton.Width = 76;
        _previewOpenButton.Height = 32;
        _previewOpenButton.Dock = DockStyle.Right;
        _previewOpenButton.FlatStyle = FlatStyle.Flat;
        _previewOpenButton.Font = ClipTheme.InfoFont;
        _previewOpenButton.BackColor = ClipTheme.OpenButton;
        _previewOpenButton.ForeColor = Color.White;
        _previewOpenButton.FlatAppearance.BorderSize = 0;
        _previewOpenButton.Visible = false;
        _previewOpenButton.Click += (_, _) => OpenSelectedDefault();

        _previewHeader.Controls.Add(titleStack);
        _previewHeader.Controls.Add(_previewOpenButton);
        _previewHeader.Controls.Add(_previewHeaderIcon);
    }

    private void ConfigureFooterBar()
    {
        _footerBar.Dock = DockStyle.Bottom;
        _footerBar.Height = 38;
        _footerBar.Padding = new Padding(10, 6, 10, 6);
        _footerBar.BackColor = ClipTheme.Footer;

        _footerLeft.Dock = DockStyle.Left;
        _footerLeft.AutoSize = true;
        _footerLeft.WrapContents = false;
        _footerLeft.BackColor = ClipTheme.Footer;
        _footerLeft.Controls.Add(CommandChip("Enter", "Paste"));
        _footerLeft.Controls.Add(CommandChip("Ctrl+C", "Copy"));
        _footerLeft.Controls.Add(CommandChip("Ctrl+Shift+L", "Log"));

        _footerRight.Dock = DockStyle.Right;
        _footerRight.AutoSize = true;
        _footerRight.WrapContents = false;
        _footerRight.FlowDirection = FlowDirection.RightToLeft;
        _footerRight.BackColor = ClipTheme.Footer;

        _settingsButton.Text = "Settings";
        _settingsButton.Width = 84;
        _settingsButton.Height = 26;
        _settingsButton.FlatStyle = FlatStyle.Flat;
        _settingsButton.Font = ClipTheme.InfoFont;
        _settingsButton.BackColor = ClipTheme.ControlBackground;
        _settingsButton.ForeColor = ClipTheme.Text;
        _settingsButton.FlatAppearance.BorderColor = ClipTheme.Border;
        _settingsButton.Click += (_, _) => OpenSettings();
        _footerRight.Controls.Add(_settingsButton);
        _footerRight.Controls.Add(CommandChip("Right click", "Actions"));

        _footerBar.Controls.Add(_footerLeft);
        _footerBar.Controls.Add(_footerRight);
    }

    private static Control CommandChip(string key, string text)
    {
        var label = new Label
        {
            AutoSize = true,
            Text = $"{key}  {text}",
            Font = ClipTheme.InfoFont,
            ForeColor = ClipTheme.MutedText,
            BackColor = ClipTheme.Chip,
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(0, 0, 7, 0),
        };
        return label;
    }

    private void DrawSearchHost(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        var rect = new Rectangle(9, 7, _searchHost.ClientSize.Width - 18, _searchHost.ClientSize.Height - 14);
        using var fill = new SolidBrush(ClipTheme.ControlBackground);
        using var border = new Pen(_search.Focused ? ClipTheme.Accent : ClipTheme.Border, _search.Focused ? 2 : 1);
        FillRoundedRectangle(e.Graphics, fill, rect, 7);
        DrawRoundedRectangle(e.Graphics, border, rect, 7);
    }

    private Button CreateFilterButton(string text, ClipboardFilter filter)
    {
        var button = new Button
        {
            Text = text,
            Tag = filter,
            Width = filter == ClipboardFilter.Images ? 74 : 58,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            Font = ClipTheme.InfoFont,
            Margin = new Padding(0, 0, 8, 0),
        };
        button.FlatAppearance.BorderSize = 1;
        button.Click += (_, _) =>
        {
            SelectFilter(filter);
        };
        TrackFilterButton(filter, button);
        StyleFilterButton(button);
        return button;
    }

    private Panel CreateSplitFilterButton(string text, ClipboardFilter filter, ContextMenuStrip menu)
    {
        var width = filter == ClipboardFilter.Files ? 72 : 58;
        var panel = new Panel
        {
            Width = width,
            Height = 28,
            Margin = new Padding(0, 0, 8, 0),
            BackColor = ClipTheme.Surface,
        };

        var main = new Button
        {
            Text = text,
            Tag = filter,
            Width = width - 22,
            Height = 28,
            Left = 0,
            Top = 0,
            FlatStyle = FlatStyle.Flat,
            Font = ClipTheme.InfoFont,
            Margin = Padding.Empty,
        };
        main.FlatAppearance.BorderSize = 1;
        main.Click += (_, _) => SelectFilter(filter);

        var arrow = new Button
        {
            Text = "v",
            Tag = filter,
            Width = 23,
            Height = 28,
            Left = width - 23,
            Top = 0,
            FlatStyle = FlatStyle.Flat,
            Font = ClipTheme.InfoFont,
            Margin = Padding.Empty,
        };
        arrow.FlatAppearance.BorderSize = 1;
        arrow.Click += (_, _) =>
        {
            SelectFilter(filter);
            ShowFilterMenu(panel, menu);
        };

        panel.Controls.Add(main);
        panel.Controls.Add(arrow);
        TrackFilterButton(filter, main);
        TrackFilterButton(filter, arrow);
        StyleFilterButton(main);
        StyleFilterButton(arrow);
        return panel;
    }

    private void SelectFilter(ClipboardFilter filter)
    {
        _filter = filter;
        if (filter != ClipboardFilter.Files)
        {
            _selectedFileType = AllFilterValue;
        }

        if (filter != ClipboardFilter.All)
        {
            _selectedDateFilter = AllFilterValue;
        }

        UpdateFilterButtons();
        LoadItems(useCachedSource: true);
    }

    private void TrackFilterButton(ClipboardFilter filter, Button button)
    {
        if (!_filterButtons.TryGetValue(filter, out var buttons))
        {
            buttons = [];
            _filterButtons[filter] = buttons;
        }

        buttons.Add(button);
    }

    private static void ShowFilterMenu(Control owner, ContextMenuStrip menu)
    {
        if (menu.Items.Count == 0)
        {
            return;
        }

        menu.Show(owner, new Point(0, owner.Height + 2));
    }

    private static void ConfigureFilterDropdown(ComboBox comboBox, int width)
    {
        comboBox.Width = width;
        comboBox.Height = 28;
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.Font = ClipTheme.InfoFont;
        comboBox.BackColor = ClipTheme.ControlBackground;
        comboBox.ForeColor = ClipTheme.Text;
        comboBox.Margin = new Padding(0, 0, 8, 0);
    }

    private void UpdateFilterButtons()
    {
        foreach (var button in _filterButtons.Values.SelectMany(buttons => buttons))
        {
            StyleFilterButton(button);
        }

        _fileTypeFilter.Visible = _filter == ClipboardFilter.Files;
        _dateFilter.Visible = false;
    }

    private void StyleFilterButton(Button button)
    {
        var active = button.Tag is ClipboardFilter filter && filter == _filter;
        button.BackColor = active ? ClipTheme.Selection : ClipTheme.ControlBackground;
        button.ForeColor = active ? ClipTheme.Text : ClipTheme.MutedText;
        button.FlatAppearance.BorderColor = active ? ClipTheme.SelectionBorder : ClipTheme.Border;
    }

    private IEnumerable<ClipboardHistoryItem> FilterItems(IEnumerable<ClipboardHistoryItem> items)
    {
        var filtered = _filter switch
        {
            ClipboardFilter.Text => items.Where(item => item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link),
            ClipboardFilter.Images => items.Where(item => item.Kind == ClipboardItemKind.Image),
            ClipboardFilter.Files => items.Where(item => item.Kind == ClipboardItemKind.Files),
            _ => items,
        };

        if (_filter == ClipboardFilter.Files && _selectedFileType != AllFilterValue)
        {
            filtered = filtered.Where(item => FileTypeFilterLabel(item) == _selectedFileType);
        }

        if (_selectedDateFilter != AllFilterValue)
        {
            filtered = filtered.Where(item => DateFilterLabel(item) == _selectedDateFilter);
        }

        return filtered;
    }

    private void UpdateFilterDropdowns(IEnumerable<ClipboardHistoryItem> sourceItems)
    {
        _updatingFilterControls = true;
        try
        {
            var fileTypes = sourceItems
                .Where(item => item.Kind == ClipboardItemKind.Files)
                .Select(FileTypeFilterLabel)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (_selectedFileType != AllFilterValue && !fileTypes.Contains(_selectedFileType, StringComparer.OrdinalIgnoreCase))
            {
                _selectedFileType = AllFilterValue;
            }

            var dateOptions = DateFilterOptions(sourceItems);
            if (_selectedDateFilter != AllFilterValue && !dateOptions.Contains(_selectedDateFilter, StringComparer.OrdinalIgnoreCase))
            {
                _selectedDateFilter = AllFilterValue;
            }

            ResetComboItems(_fileTypeFilter, fileTypes, _selectedFileType);
            ResetComboItems(_dateFilter, dateOptions, _selectedDateFilter);
            RebuildFilterMenu(_fileFilterMenu, FixedFileTypeOptions(fileTypes), _selectedFileType, value =>
            {
                _filter = ClipboardFilter.Files;
                _selectedFileType = value;
                _selectedDateFilter = AllFilterValue;
                Program.LogDebug($"File type filter selected value={_selectedFileType}");
                UpdateFilterButtons();
                LoadItems(useCachedSource: true);
            });
            RebuildFilterMenu(_allFilterMenu, DateFilterOptions(sourceItems), _selectedDateFilter, value =>
            {
                _filter = ClipboardFilter.All;
                _selectedDateFilter = value;
                _selectedFileType = AllFilterValue;
                Program.LogDebug($"Date filter selected value={_selectedDateFilter}");
                UpdateFilterButtons();
                LoadItems(useCachedSource: true);
            });
            _fileTypeFilter.Visible = false;
            _dateFilter.Visible = false;
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private static void RebuildFilterMenu(ContextMenuStrip menu, IEnumerable<string> values, string selected, Action<string> onSelect)
    {
        menu.Items.Clear();
        var all = new ToolStripMenuItem(AllFilterValue)
        {
            Checked = selected == AllFilterValue,
        };
        all.Click += (_, _) => onSelect(AllFilterValue);
        menu.Items.Add(all);

        foreach (var value in values)
        {
            var item = new ToolStripMenuItem(value)
            {
                Checked = string.Equals(value, selected, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => onSelect(value);
            menu.Items.Add(item);
        }
    }

    private static List<string> FixedFileTypeOptions(IEnumerable<string> available)
    {
        var set = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var preferred = new[] { "Folders", "PDF", "Excel", "Visio", "HTML", "Word", "PowerPoint", "JPEG", "PNG", "Text", "Log", "Script" };
        foreach (var label in preferred)
        {
            var matches = set
                .Where(value => value.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var match in matches)
            {
                ordered.Add(match);
                set.Remove(match);
            }
        }

        ordered.AddRange(set.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    private static void ResetComboItems(ComboBox comboBox, IEnumerable<string> values, string selected)
    {
        comboBox.BeginUpdate();
        try
        {
            comboBox.Items.Clear();
            comboBox.Items.Add(AllFilterValue);
            foreach (var value in values)
            {
                comboBox.Items.Add(value);
            }

            comboBox.SelectedItem = comboBox.Items.Contains(selected) ? selected : AllFilterValue;
        }
        finally
        {
            comboBox.EndUpdate();
        }
    }

    private static List<string> DateFilterOptions(IEnumerable<ClipboardHistoryItem> sourceItems)
    {
        return sourceItems
            .Select(DateFilterLabel)
            .Where(label => label != AllFilterValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(DateFilterSortOrder)
            .ToList();
    }

    private static int DateFilterSortOrder(string label)
    {
        return label switch
        {
            "Today" => 0,
            "Yesterday" => 1,
            "This week" => 2,
            "This month" => 3,
            "This year" => 4,
            "Older" => 5,
            _ => 99,
        };
    }

    private static string DateFilterLabel(ClipboardHistoryItem item)
    {
        var copied = item.LastCopiedAt.LocalDateTime.Date;
        var today = DateTime.Today;
        if (copied == today)
        {
            return "Today";
        }

        if (copied == today.AddDays(-1))
        {
            return "Yesterday";
        }

        if (copied >= today.AddDays(-7))
        {
            return "This week";
        }

        if (copied.Year == today.Year && copied.Month == today.Month)
        {
            return "This month";
        }

        return copied.Year == today.Year ? "This year" : "Older";
    }

    private static string FileTypeFilterLabel(ClipboardHistoryItem item)
    {
        var path = item.FilePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Files";
        }

        if (Directory.Exists(path))
        {
            return "Folders";
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" or ".xlsm" or ".xls" => "Excel",
            ".docx" or ".doc" => "Word",
            ".pptx" or ".ppt" => "PowerPoint",
            ".vsdx" or ".vsd" => "Visio",
            ".pdf" => "PDF",
            ".jpg" or ".jpeg" => "JPEG",
            ".png" => "PNG",
            ".html" or ".htm" => "HTML",
            ".txt" => "Text",
            ".log" => "Log",
            ".bat" or ".cmd" or ".ps1" => "Script",
            "" => "Files",
            _ => extension.ToUpperInvariant(),
        };
    }

    private void DrawClipboardSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        if (e.Item.Tag is ClipboardGroupHeader)
        {
            using var headerBackground = new SolidBrush(ClipTheme.Surface);
            e.Graphics.FillRectangle(headerBackground, e.Bounds);
            var headerTextRect = new Rectangle(e.Bounds.Left + 12, e.Bounds.Top + 4, e.Bounds.Width - 24, Math.Max(18, e.Bounds.Height - 8));
            TextRenderer.DrawText(
                e.Graphics,
                e.Item.Text,
                ClipTheme.InfoFont,
                headerTextRect,
                ClipTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            using var pen = new Pen(ClipTheme.Divider);
            e.Graphics.DrawLine(pen, e.Bounds.Left + 12, e.Bounds.Bottom - 2, e.Bounds.Right - 12, e.Bounds.Bottom - 2);
            return;
        }

        var selected = e.Item.Selected;
        var bounds = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 2, e.Bounds.Width - 12, e.Bounds.Height - 4);
        using var background = new SolidBrush(selected ? ClipTheme.Selection : ClipTheme.Surface);
        FillRoundedRectangle(e.Graphics, background, bounds, 7);

        if (selected)
        {
            using var border = new Pen(ClipTheme.SelectionBorder);
            DrawRoundedRectangle(e.Graphics, border, bounds, 7);
        }

        if (!string.IsNullOrWhiteSpace(e.Item.ImageKey) && _thumbnailImages.TryGetValue(e.Item.ImageKey, out var icon))
        {
            if (icon is not null)
            {
                var iconRect = new Rectangle(bounds.Left + 7, bounds.Top + Math.Max(2, (bounds.Height - 46) / 2), 46, 46);
                e.Graphics.DrawImage(icon, iconRect);
            }
        }

        var textRect = new Rectangle(bounds.Left + 64, bounds.Top, Math.Max(10, bounds.Width - 70), bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.Item.Text,
            ClipTheme.UiFont,
            textRect,
            ClipTheme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = RoundedRectanglePath(bounds, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = RoundedRectanglePath(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void LoadItems(bool refreshPreview = true, bool useCachedSource = false)
    {
        try
        {
            var selectedId = SelectedItem()?.Id;
            var queriedItems = SourceItems(useCachedSource);
            UpdateFilterDropdowns(queriedItems);
            _items.Clear();
            _items.AddRange(FilterItems(queriedItems));

            _suppressSelectionPreview = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Groups.Clear();

            foreach (var (header, groupItems) in GroupedItemsForDisplay())
            {
                if (groupItems.Count == 0)
                {
                    continue;
                }

                _list.Items.Add(new ListViewItem(header) { Tag = new ClipboardGroupHeader(header) });
                foreach (var item in groupItems)
                {
                    var row = new ListViewItem(TitleFor(item))
                    {
                        Tag = item,
                        ImageKey = ImageKeyFor(item),
                    };
                    _list.Items.Add(row);
                }
            }

            var selectedRow = _list.Items
                .Cast<ListViewItem>()
                .FirstOrDefault(row => row.Tag is ClipboardHistoryItem item && item.Id == selectedId) ??
                _list.Items.Cast<ListViewItem>().FirstOrDefault(row => row.Tag is ClipboardHistoryItem);
            if (selectedRow is not null)
            {
                selectedRow.Selected = true;
            }

            UpdateClipboardColumnWidth();
            _listScrollOverlay?.SyncFromNative();
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        finally
        {
            _list.EndUpdate();
            _suppressSelectionPreview = false;
        }

        if (refreshPreview)
        {
            BeginInvoke(new Action(ShowSelectedPreview));
        }
    }

    private IReadOnlyList<ClipboardHistoryItem> SourceItems(bool useCachedSource)
    {
        var query = _search.Text;
        if (useCachedSource && string.Equals(_sourceQuery, query, StringComparison.Ordinal) && _sourceItems.Count > 0)
        {
            Program.LogDebug($"Clipboard filter reused cached source count={_sourceItems.Count} filter={_filter} file={_selectedFileType} date={_selectedDateFilter}");
            return _sourceItems;
        }

        var watch = Stopwatch.StartNew();
        var queriedItems = _store.QueryItems(query);
        _sourceItems.Clear();
        _sourceItems.AddRange(queriedItems);
        _sourceQuery = query;
        Program.LogDebug($"Clipboard source loaded count={_sourceItems.Count} elapsedMs={watch.ElapsedMilliseconds} query={query}");
        return _sourceItems;
    }

    private void UpdateClipboardColumnWidth()
    {
        if (_list.Columns.Count == 0)
        {
            return;
        }

        var width = Math.Max(80, _list.ClientSize.Width - 8);
        using var graphics = _list.CreateGraphics();
        foreach (ListViewItem item in _list.Items)
        {
            var textWidth = TextRenderer.MeasureText(graphics, item.Text, ClipTheme.UiFont).Width + 70;
            width = Math.Max(width, textWidth);
        }

        _list.Columns[0].Width = width;
    }

    private ClipboardHistoryItem? SelectedItem()
    {
        return _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as ClipboardHistoryItem;
    }

    private void ShowSelectedPreview()
    {
        if (_suppressSelectionPreview)
        {
            return;
        }

        var item = SelectedItem();
        if (item is null && _list.Items.Count > 0)
        {
            Program.LogDebug("InfoRows skipped transient empty selection");
            return;
        }

        if (item?.Id == _lastRenderedItemId)
        {
            return;
        }

        _lastRenderedItemId = item?.Id;
        _previewRenderVersion++;
        _shellPreview.ClearPreview();
        _shellPreview.Visible = false;
        _excelPreview.Visible = false;
        _htmlPreview.Visible = false;
        _imagePreview.Visible = false;
        _preview.Visible = true;

        if (item is null)
        {
            _preview.Text = string.Empty;
            UpdatePreviewHeader(null);
            FillInformation(null);
            return;
        }

        UpdatePreviewHeader(item);
        FillInformation(item);

        if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            _preview.Visible = false;
            _imagePreview.Visible = true;
            using var source = Image.FromFile(item.AssetPath);
            _imagePreview.SetImage(new Bitmap(source));
            return;
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            ShowFilePreview(item);
            return;
        }

        _preview.Text = item.Kind switch
        {
            ClipboardItemKind.Text or ClipboardItemKind.Link => TextPayload(item),
            _ => item.Preview,
        };
        _preview.WordWrap = true;
        _preview.ScrollBars = ScrollBars.Vertical;
        _previewScrollOverlay?.SyncFromNative();
    }

    private void UpdatePreviewHeader(ClipboardHistoryItem? item)
    {
        if (item is null)
        {
            _previewHeaderTitle.Text = "Clipboard";
            _previewHeaderSubtitle.Text = "Select an item to preview it";
            _previewOpenButton.Visible = false;
            _previewHeaderIcon.Image = null;
            return;
        }

        _previewHeaderTitle.Text = PreviewHeaderTitle(item);
        _previewHeaderSubtitle.Text = $"Copied from {SourceDisplayName(item)} - {item.LastCopiedAt.LocalDateTime:g}";
        _previewOpenButton.Visible = CanOpenDefault(item);
        _previewHeaderIcon.Image?.Dispose();
        _previewHeaderIcon.Image = PreviewHeaderIcon(item);
    }

    private Image? PreviewHeaderIcon(ClipboardHistoryItem item)
    {
        try
        {
            var key = ImageKeyFor(item);
            if (!string.IsNullOrWhiteSpace(key) && _thumbnailImages.TryGetValue(key, out var image))
            {
                return new Bitmap(image);
            }

            if (!string.IsNullOrWhiteSpace(item.SourceApplicationPath) && File.Exists(item.SourceApplicationPath))
            {
                return Icon.ExtractAssociatedIcon(item.SourceApplicationPath)?.ToBitmap();
            }
        }
        catch
        {
        }

        return SystemIcons.Application.ToBitmap();
    }

    private static string PreviewHeaderTitle(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Link => "Link",
            ClipboardItemKind.Text => "Text",
            ClipboardItemKind.Image when item.ImageWidth is not null && item.ImageHeight is not null => $"Image {item.ImageWidth} x {item.ImageHeight}",
            ClipboardItemKind.Image => "Image",
            ClipboardItemKind.Files => FileTitle(item),
            _ => item.Preview,
        };
    }

    private static bool CanOpenDefault(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Link)
        {
            return !string.IsNullOrWhiteSpace(TextPayload(item));
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            return !string.IsNullOrWhiteSpace(item.AssetPath) && File.Exists(item.AssetPath);
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            var path = item.FilePaths.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));
        }

        return false;
    }

    private void OpenSelectedDefault()
    {
        var item = SelectedItem();
        if (item is null)
        {
            return;
        }

        try
        {
            if (item.Kind == ClipboardItemKind.Link)
            {
                var target = TextPayload(item).Trim();
                if (!target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    target = target.Contains('@') && !target.Contains(' ', StringComparison.Ordinal) ? $"mailto:{target}" : $"https://{target}";
                }

                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }

            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null)
            {
                Process.Start(new ProcessStartInfo(item.AssetPath) { UseShellExecute = true });
                return;
            }

            if (item.Kind == ClipboardItemKind.Files)
            {
                var path = item.FilePaths.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            ShowToast("Could not open item");
        }
    }

    private void ShowFilePreview(ClipboardHistoryItem item)
    {
        var path = item.FilePaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            _preview.Text = item.Preview;
            return;
        }

        Program.LogDebug($"Preview placeholder icon path={path}");
        ShowFileIconPreview(path);
        _imagePreview.Refresh();

        if (item.FilePaths.Count > 1 || Directory.Exists(path))
        {
            Program.LogDebug($"Preview fallback icon path={path} reason={(Directory.Exists(path) ? "folder" : "multiple-files")}");
            return;
        }

        if (IsImagePreviewFile(path) && File.Exists(path))
        {
            Program.LogDebug($"Preview native image path={path}");
            _preview.Visible = false;
            _shellPreview.Visible = false;
            _excelPreview.Visible = false;
            _imagePreview.Visible = true;
            using var source = Image.FromFile(path);
            _imagePreview.SetImage(new Bitmap(source));
            return;
        }

        if (IsPdfPreviewFile(path))
        {
            LoadRenderedPreviewAsync(path, "pdf", () => PdfPreviewRenderer.TryRenderFirstPage(path, out var image) ? image : null);
            return;
        }

        if (StaticDocumentPreviewRenderer.IsSupported(path))
        {
            LoadRenderedPreviewAsync(path, "static document", () => StaticDocumentPreviewRenderer.TryRenderFirstPageOnStaThread(path));
            return;
        }

        if (IsHtmlPreviewFile(path))
        {
            ShowHtmlFilePreview(path);
            return;
        }

        if (IsTextPreviewFile(path))
        {
            Program.LogDebug($"Preview text file path={path}");
            ShowTextFilePreview(path);
            return;
        }

        if (File.Exists(path))
        {
            _preview.Visible = false;
            _imagePreview.Visible = false;
            _excelPreview.Visible = false;
            _htmlPreview.Visible = false;
            _shellPreview.Visible = true;
            _shellPreview.BringToFront();
            if (_shellPreview.TryPreview(path))
            {
                Program.LogDebug($"Preview shell handler path={path}");
                _shellPreview.RefreshPreviewBounds();
                return;
            }

            _shellPreview.Visible = false;
        }

        Program.LogDebug($"Preview fallback icon path={path} reason=no-handler");
        ShowFileIconPreview(path);
    }

    private void LoadRenderedPreviewAsync(string path, string kind, Func<Image?> render)
    {
        var version = _previewRenderVersion;
        Program.LogDebug($"Preview async {kind} queued path={path}");
        _ = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            Image? image = null;
            try
            {
                image = render();
                Program.LogDebug($"Preview async {kind} rendered path={path} elapsedMs={watch.ElapsedMilliseconds} success={image is not null}");
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }

            if (image is null)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || version != _previewRenderVersion)
                    {
                        image.Dispose();
                        Program.LogDebug($"Preview async {kind} discarded stale path={path}");
                        return;
                    }

                    _preview.Visible = false;
                    _shellPreview.Visible = false;
                    _excelPreview.Visible = false;
                    _htmlPreview.Visible = false;
                    _imagePreview.Visible = true;
                    _imagePreview.SetImage(image);
                    _imagePreview.BringToFront();
                    Program.LogDebug($"Preview async {kind} applied path={path}");
                }));
            }
            catch
            {
                image.Dispose();
            }
        });
    }

    private void ShowFileIconPreview(string path)
    {
        _preview.Visible = false;
        _shellPreview.Visible = false;
        _excelPreview.Visible = false;
        _htmlPreview.Visible = false;
        _imagePreview.Visible = true;
        _imagePreview.SetImage(FilePreviewIcon(path));
        _imagePreview.BringToFront();
    }

    private void ShowTextFilePreview(string path)
    {
        _imagePreview.Visible = false;
        _shellPreview.Visible = false;
        _excelPreview.Visible = false;
        _htmlPreview.Visible = false;
        _preview.Visible = true;
        _preview.WordWrap = true;
        _preview.ScrollBars = ScrollBars.Vertical;
        _preview.Text = ReadTextPreview(path);
        _preview.BringToFront();
        _previewScrollOverlay?.SyncFromNative();
    }

    private void ShowHtmlFilePreview(string path)
    {
        Program.LogDebug($"Preview html webview path={path} bundled={IsBundledHtml(path)}");
        _preview.Visible = false;
        _shellPreview.Visible = false;
        _excelPreview.Visible = false;
        _imagePreview.Visible = false;
        _htmlPreview.Visible = true;
        _htmlPreview.BringToFront();
        _ = LoadHtmlPreviewAsync(path);
    }

    private async Task LoadHtmlPreviewAsync(string path)
    {
        try
        {
            if (_htmlPreview.CoreWebView2 is null)
            {
                await _htmlPreview.EnsureCoreWebView2Async();
                if (_htmlPreview.CoreWebView2 is not null)
                {
                    _htmlPreview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _htmlPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    Program.LogDebug("Preview html webview initialized");
                }
            }

            _htmlPreview.Source = new Uri(path);
            Program.LogDebug($"Preview html webview navigate path={path}");
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            if (_htmlPreview.Visible)
            {
                Program.LogDebug($"Preview html webview fallback-to-text path={path}");
                ShowTextFilePreview(path);
            }
        }
    }

    private void ShowExcelPreview(ExcelPreviewCell[,] cells)
    {
        _preview.Visible = false;
        _shellPreview.Visible = false;
        _imagePreview.Visible = false;
        _htmlPreview.Visible = false;
        _excelPreview.Visible = true;
        _excelPreview.BringToFront();
        _excelPreview.Columns.Clear();
        _excelPreview.Rows.Clear();

        for (var column = 0; column < cells.GetLength(1); column++)
        {
            _excelPreview.Columns.Add(ColumnName(column + 1), ColumnName(column + 1));
            _excelPreview.Columns[column].Width = 82;
            _excelPreview.Columns[column].SortMode = DataGridViewColumnSortMode.NotSortable;
        }

        for (var row = 0; row < cells.GetLength(0); row++)
        {
            var values = Enumerable.Range(0, cells.GetLength(1)).Select(column => cells[row, column].Value).Cast<object>().ToArray();
            var rowIndex = _excelPreview.Rows.Add(values);
            _excelPreview.Rows[rowIndex].HeaderCell.Value = (row + 1).ToString();
            _excelPreview.Rows[rowIndex].Height = 24;
            for (var column = 0; column < cells.GetLength(1); column++)
            {
                var style = cells[row, column];
                if (style.FillColor is not null)
                {
                    _excelPreview.Rows[rowIndex].Cells[column].Style.BackColor = style.FillColor.Value;
                }

                if (style.Bold)
                {
                    _excelPreview.Rows[rowIndex].Cells[column].Style.Font = new Font(_excelPreview.Font, FontStyle.Bold);
                }
            }
        }

        if (_excelPreview.Rows.Count > 0 && _excelPreview.Columns.Count > 0)
        {
            _excelPreview.FirstDisplayedScrollingRowIndex = 0;
            _excelPreview.FirstDisplayedScrollingColumnIndex = 0;
            _excelPreview.CurrentCell = _excelPreview.Rows[0].Cells[0];
            _excelPreview.ClearSelection();
            Program.LogDebug("Excel preview reset scroll origin row=0 column=0");
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.L)
        {
            WriteDebugSnapshot();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            PasteSelected();
            e.Handled = true;
        }
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _list.HitTest(e.Location);
        if (hit.Item is not null)
        {
            hit.Item.Selected = true;
        }

        var item = SelectedItem();
        if (item is null)
        {
            return;
        }

        BuildContextMenu(item).Show(_list, e.Location);
    }

    private ContextMenuStrip BuildContextMenu(ClipboardHistoryItem item)
    {
        var menu = new ContextMenuStrip();
        StyleMenu(menu);
        menu.Items.Add("Paste", null, (_, _) => PasteSelected());
        menu.Items.Add("Copy", null, (_, _) => CopySelected());
        menu.Items.Add(item.IsPinned ? "Unpin" : "Pin", null, (_, _) => TogglePin(item));
        var canMoveUp = CanMovePin(item, -1);
        var canMoveDown = CanMovePin(item, 1);
        menu.Items.Add(new ToolStripMenuItem("Move Pin Up", null, (_, _) => MovePin(item, -1)) { Enabled = canMoveUp });
        menu.Items.Add(new ToolStripMenuItem("Move Pin Down", null, (_, _) => MovePin(item, 1)) { Enabled = canMoveDown });

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            menu.Items.Add("Edit Text", null, (_, _) => EditText(item));
            menu.Items.Add("Append to Clipboard", null, (_, _) => AppendText(item));
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            menu.Items.Add("Open Image", null, (_, _) => OpenImage(item));
            menu.Items.Add("Open With...", null, (_, _) => OpenWith(item));
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            menu.Items.Add("Open With...", null, (_, _) => OpenWith(item));
            menu.Items.Add("Copy path", null, (_, _) => CopyPath(item));
        }

        menu.Items.Add("Save as File", null, (_, _) => SaveItem(item));
        menu.Items.Add("Delete", null, (_, _) => { _store.Delete(item.Id); LoadItems(); });
        return menu;
    }

    private static void StyleMenu(ContextMenuStrip menu)
    {
        menu.BackColor = ClipTheme.Surface;
        menu.ForeColor = ClipTheme.Text;
        menu.Font = ClipTheme.UiFont;
        menu.Renderer = new ToolStripProfessionalRenderer(new ClipMenuColorTable());
    }

    private void CopySelected()
    {
        var item = SelectedItem();
        if (item is null)
        {
            return;
        }

        SetClipboard(item);
    }

    private void PasteSelected()
    {
        var item = SelectedItem();
        if (item is null)
        {
            return;
        }

        SetClipboard(item);
        Hide();
        SendKeys.SendWait("^v");
    }

    private static void SetClipboard(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            Clipboard.SetText(TextPayload(item));
        }
        else if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
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
    }

    private void TogglePin(ClipboardHistoryItem item)
    {
        _store.SetPinned(item.Id, !item.IsPinned);
        item.IsPinned = !item.IsPinned;
        item.PinOrder = item.IsPinned ? item.PinOrder : 0;
        if (!item.IsPinned)
        {
            item.LastCopiedAt = DateTimeOffset.Now;
            item.LastUsedAt = DateTimeOffset.Now;
        }

        MoveExistingListItem(item);
    }

    private void MovePin(ClipboardHistoryItem item, int direction)
    {
        Program.LogDebug($"MovePin requested id={item.Id} direction={direction} isPinned={item.IsPinned} pinOrder={item.PinOrder}");
        if (!item.IsPinned || !_store.MovePinned(item.Id, direction))
        {
            Program.LogDebug("MovePin ignored or store rejected.");
            return;
        }

        var pins = _store.QueryItems()
            .Where(i => i.IsPinned)
            .ToDictionary(i => i.Id, i => i.PinOrder);

        foreach (var existing in _items.Where(i => i.IsPinned))
        {
            if (pins.TryGetValue(existing.Id, out var order))
            {
                existing.PinOrder = order;
            }
        }

        MoveExistingListItem(item);
        Program.LogDebug($"MovePin completed id={item.Id}");
    }

    private bool CanMovePin(ClipboardHistoryItem item, int direction)
    {
        if (!item.IsPinned)
        {
            return false;
        }

        var pins = _store.QueryItems()
            .Where(i => i.IsPinned)
            .OrderBy(i => i.PinOrder)
            .ToList();
        var index = pins.FindIndex(i => i.Id == item.Id);
        var target = index + Math.Sign(direction);
        return index >= 0 && target >= 0 && target < pins.Count;
    }

    private void MoveExistingListItem(ClipboardHistoryItem item)
    {
        _suppressSelectionPreview = true;
        _list.BeginUpdate();
        try
        {
            var queriedItems = SourceItems(useCachedSource: true);
            UpdateFilterDropdowns(queriedItems);
            _items.Clear();
            _items.AddRange(FilterItems(queriedItems));

            _list.Items.Clear();
            _list.Groups.Clear();

            ListViewItem? selectedRow = null;
            foreach (var (header, groupItems) in GroupedItemsForDisplay())
            {
                if (groupItems.Count == 0)
                {
                    continue;
                }

                _list.Items.Add(new ListViewItem(header) { Tag = new ClipboardGroupHeader(header) });
                foreach (var listItem in groupItems)
                {
                    var row = new ListViewItem(TitleFor(listItem))
                    {
                        Tag = listItem,
                        ImageKey = ImageKeyFor(listItem),
                    };
                    _list.Items.Add(row);
                    if (listItem.Id == item.Id)
                    {
                        selectedRow = row;
                    }
                }
            }

            if (selectedRow is not null)
            {
                selectedRow.Selected = true;
                selectedRow.Focused = true;
                selectedRow.EnsureVisible();
            }
        }
        finally
        {
            _list.EndUpdate();
            _suppressSelectionPreview = false;
        }
    }

    public void WriteDebugSnapshot()
    {
        var selected = SelectedItem();
        Program.LogDebug("=== Snapshot ===");
        Program.LogDebug($"selected={(selected is null ? "none" : $"{selected.Id} {selected.Kind} pinned={selected.IsPinned} order={selected.PinOrder} preview={selected.Preview}")}");
        foreach (ListViewGroup group in _list.Groups)
        {
            Program.LogDebug($"group header={group.Header} items={group.Items.Count}");
            foreach (ListViewItem row in group.Items)
            {
                if (row.Tag is ClipboardHistoryItem item)
                {
                    Program.LogDebug($"  row id={item.Id} kind={item.Kind} pinned={item.IsPinned} order={item.PinOrder} text={row.Text}");
                }
            }
        }
        Program.LogDebug($"info rows={string.Join(",", _infoRowOrder)}");
        Program.LogDebug($"info scroll visible={_infoScrollBar.Visible} value={_infoScrollBar.Value} max={Math.Max(0, _infoRows.Height - _infoRowsScroll.ClientSize.Height)} rowsHeight={_infoRows.Height} viewport={_infoRowsScroll.ClientSize.Height}");
        Program.LogDebug("=== End Snapshot ===");
        ShowToast("Clip log saved");
    }

    private void ShowToast(string message)
    {
        _toast.Text = message;
        _toast.SizeToContent();
        _toast.Visible = true;
        _toast.BringToFront();
        PositionToast();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void PositionToast()
    {
        if (!_toast.Visible)
        {
            return;
        }

        _toast.Left = Math.Max(8, (ClientSize.Width - _toast.Width) / 2);
        _toast.Top = Math.Max(8, ClientSize.Height - _toast.Height - 18);
    }

    private void EditText(ClipboardHistoryItem item)
    {
        _keepOpenForModal = true;
        try
        {
            using var editor = new TextEditorForm(item.Text ?? string.Empty);
            if (editor.ShowDialog(this) == DialogResult.OK)
            {
                _store.EditText(item.Id, editor.Value);
                LoadItems();
            }
        }
        finally
        {
            _keepOpenForModal = false;
            Show();
            Activate();
        }
    }

    private void OpenSettings()
    {
        _keepOpenForModal = true;
        try
        {
            using var settings = new SettingsForm();
            settings.ShowDialog(this);
        }
        finally
        {
            _keepOpenForModal = false;
            Show();
            Activate();
        }
    }

    private static void AppendText(ClipboardHistoryItem item)
    {
        var existing = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        var payload = TextPayload(item);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            Clipboard.SetText(existing + payload);
        }
    }

    private static void OpenImage(ClipboardHistoryItem item)
    {
        if (item.AssetPath is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(item.AssetPath) { UseShellExecute = true });
    }

    private static void CopyPath(ClipboardHistoryItem item)
    {
        if (item.FilePaths.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, item.FilePaths));
    }

    private void OpenWith(ClipboardHistoryItem item)
    {
        var targetPath = item.Kind == ClipboardItemKind.Image
            ? item.AssetPath
            : item.FilePaths.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(targetPath) || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
        {
            return;
        }

        _keepOpenForModal = true;
        try
        {
            using var picker = new OpenWithPickerForm(targetPath);
            if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedApp is not null)
            {
                AppLauncher.OpenWith(targetPath, picker.SelectedApp);
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            MessageBox.Show(this, ex.Message, "Clip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _keepOpenForModal = false;
            Show();
            Activate();
        }
    }

    private void SaveItem(ClipboardHistoryItem item)
    {
        using var dialog = new SaveFileDialog
        {
            FileName = item.Kind == ClipboardItemKind.Image ? "clipboard.png" : "clipboard.txt",
            Filter = item.Kind == ClipboardItemKind.Image ? "PNG Image|*.png|All files|*.*" : "Text File|*.txt|All files|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _store.SaveAsFile(item.Id, dialog.FileName);
        }
    }

    private static bool IsTextPreviewFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".html" or ".htm" or ".css" or ".scss" or ".sass" or ".less" or ".js" or ".jsx" or ".ts" or ".tsx" or ".cs" or ".csproj" or ".sln" or ".vb" or ".fs" or ".ps1" or ".psm1" or ".bat" or ".cmd" or ".ini" or ".env" or ".yml" or ".yaml" or ".toml" or ".sql" or ".py" or ".rb" or ".go" or ".rs" or ".java" or ".c" or ".cpp" or ".h" or ".hpp" or ".php" or ".sh" or ".swift";
    }

    private static bool IsHtmlPreviewFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".html" or ".htm";
    }

    private static bool IsBundledHtml(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[16_000];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, read);
            return head.Contains("__bundler", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("text/babel", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("Unpacking", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }

    private static bool IsImagePreviewFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp";
    }

    private static bool IsExcelPreviewFile(string path)
    {
        return Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPdfPreviewFile(string path)
    {
        return Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    }

    private static Image FilePreviewIcon(string path)
    {
        if (Directory.Exists(path))
        {
            return new Bitmap(DrawFolderIcon(150), new Size(160, 160));
        }

        return new Bitmap(RenderFileTypeIcon(path, 150), new Size(160, 160));
    }

    private static string ColumnName(int columnNumber)
    {
        var dividend = columnNumber;
        var columnName = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
    }

    private static string ReadTextPreview(string path)
    {
        const int maxChars = 120_000;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxChars + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, Math.Min(read, maxChars));
            return read > maxChars ? text + $"{Environment.NewLine}{Environment.NewLine}... preview truncated ..." : text;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return FileMetadataPreview(path);
        }
    }

    private static string FileMetadataPreview(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return $"Folder: {directory.Name}{Environment.NewLine}Path: {directory.FullName}{Environment.NewLine}Modified: {directory.LastWriteTime}";
        }

        var file = new FileInfo(path);
        return $"File: {file.Name}{Environment.NewLine}Type: {file.Extension}{Environment.NewLine}Size: {FormatBytes(file.Length)}{Environment.NewLine}Modified: {file.LastWriteTime}{Environment.NewLine}Path: {file.FullName}";
    }

    private Dictionary<string, ListViewGroup> BuildGroups()
    {
        return new Dictionary<string, ListViewGroup>
        {
            ["Pinned"] = new("Pinned items"),
            ["Today"] = new("Today"),
            ["Yesterday"] = new("Yesterday"),
            ["ThisWeek"] = new("This week"),
            ["ThisMonth"] = new("This month"),
            ["ThisYear"] = new("This year"),
            ["Older"] = new("Older"),
        };
    }

    private List<(string Header, List<ClipboardHistoryItem> Items)> GroupedItemsForDisplay()
    {
        var groups = BuildGroups();
        return
        [
            (groups["Pinned"].Header, _items.Where(item => item.IsPinned).ToList()),
            (groups["Today"].Header, _items.Where(item => !item.IsPinned && GroupFor(item, groups) == groups["Today"]).ToList()),
            (groups["Yesterday"].Header, _items.Where(item => !item.IsPinned && GroupFor(item, groups) == groups["Yesterday"]).ToList()),
            (groups["ThisWeek"].Header, _items.Where(item => !item.IsPinned && GroupFor(item, groups) == groups["ThisWeek"]).ToList()),
            (groups["ThisMonth"].Header, _items.Where(item => !item.IsPinned && GroupFor(item, groups) == groups["ThisMonth"]).ToList()),
            (groups["ThisYear"].Header, _items.Where(item => !item.IsPinned && GroupFor(item, groups) == groups["ThisYear"]).ToList()),
            (groups["Older"].Header, _items.Where(item => !item.IsPinned && GroupFor(item, groups) == groups["Older"]).ToList()),
        ];
    }

    private static ListViewGroup GroupFor(ClipboardHistoryItem item, Dictionary<string, ListViewGroup> groups)
    {
        if (item.IsPinned)
        {
            return groups["Pinned"];
        }

        var copied = item.LastCopiedAt.LocalDateTime.Date;
        var today = DateTime.Today;
        if (copied == today)
        {
            return groups["Today"];
        }

        if (copied == today.AddDays(-1))
        {
            return groups["Yesterday"];
        }

        if (copied >= today.AddDays(-7))
        {
            return groups["ThisWeek"];
        }

        if (copied.Year == today.Year && copied.Month == today.Month)
        {
            return groups["ThisMonth"];
        }

        return copied.Year == today.Year ? groups["ThisYear"] : groups["Older"];
    }

    private string ImageKeyFor(ClipboardHistoryItem item)
    {
        var key = item.Id;
        if (_thumbnailImages.ContainsKey(key))
        {
            return key;
        }

        _thumbnailImages[key] = ThumbnailFor(item);
        _smallImages.Images.Add(key, new Bitmap(1, 1));
        return key;
    }

    private void ClearThumbnailImages()
    {
        foreach (var image in _thumbnailImages.Values)
        {
            image.Dispose();
        }

        _thumbnailImages.Clear();
    }

    private static Image ThumbnailFor(ClipboardHistoryItem item)
    {
        try
        {
            if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
            {
                using var image = Image.FromFile(item.AssetPath);
                return image.GetThumbnailImage(34, 34, null, IntPtr.Zero);
            }

            return item.Kind switch
            {
                ClipboardItemKind.Text => DrawTextIcon(),
                ClipboardItemKind.Link => DrawLinkIcon(),
                ClipboardItemKind.Files => DrawFileThumbnail(item),
                _ => DrawTextIcon(),
            };
        }
        catch
        {
        }

        return DrawTextIcon();
    }

    private static Image DrawTextIcon()
    {
        return RenderSvgIcon("text_underline_icon_high_fidelity.svg", 19, scaleX: 1.55f);
    }

    private static Image DrawLinkIcon()
    {
        return RenderSvgIcon("hyperlink-icon.svg", 14);
    }

    private static Image DrawFileIcon()
    {
        return DrawFileIcon(20);
    }

    private static Image DrawFileIcon(int iconSize)
    {
        return RenderSvgIcon("file-60.svg", iconSize);
    }

    private static Image DrawFileThumbnail(ClipboardHistoryItem item)
    {
        if (item.FilePaths.Count != 1)
        {
            return DrawFileIcon();
        }

        var path = item.FilePaths[0];
        return Directory.Exists(path) ? DrawFolderIcon(50) : RenderFileTypeIcon(path, 56);
    }

    private static Image DrawFolderIcon()
    {
        return DrawFolderIcon(20);
    }

    private static Image DrawFolderIcon(int iconSize)
    {
        return RenderSvgIcon("folder-svgrepo-com.svg", iconSize);
    }

    private static Image RenderSvgIcon(string fileName, int iconSize, float scaleX = 1.0f)
    {
        var renderWidth = Math.Max(1, (int)Math.Round(iconSize * scaleX));
        var canvas = Math.Max(44, Math.Max(iconSize, renderWidth) + 12);
        var target = new Bitmap(canvas, canvas);
        using var graphics = Graphics.FromImage(target);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var path = AssetIconPath(fileName);

        var document = SvgDocument.FromSvg<SvgDocument>(ThemeSvgMarkup(File.ReadAllText(path)));
        document.Width = renderWidth;
        document.Height = iconSize;
        using var rendered = document.Draw(renderWidth, iconSize);
        var x = (canvas - renderWidth) / 2;
        var y = (canvas - iconSize) / 2;
        graphics.DrawImage(rendered, x, y, renderWidth, iconSize);
        return target;
    }

    private static Image RenderFileTypeIcon(string path, int iconSize)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            return DrawFileIcon();
        }

        if (ShouldUseWindowsFileIcon(extension))
        {
            var windowsIcon = ShellIconReader.TryGetIcon(path, large: iconSize >= 48);
            if (windowsIcon is not null)
            {
                return windowsIcon;
            }
        }

        var fileName = $"file-icon-{extension}.svg";
        var iconPath = AssetIconPath(fileName);
        if (File.Exists(iconPath))
        {
            return RenderSvgIcon(fileName, iconSize);
        }

        return DrawGeneratedFileIcon(extension, iconSize);
    }

    private static bool ShouldUseWindowsFileIcon(string extension)
    {
        return extension is "doc" or "docx" or "xls" or "xlsx" or "xlsm" or "ppt" or "pptx" or "vsd" or "vsdx" or "pdf";
    }

    private static string AssetIconPath(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "icons", fileName);
        if (File.Exists(path))
        {
            return path;
        }

        path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "icons", fileName);
        return Path.GetFullPath(path);
    }

    private static Image DrawGeneratedFileIcon(string extension, int iconSize)
    {
        var text = CleanFileIconLabel(extension);
        var fontSize = FileIconFontSize(text);
        var svg = $$"""
<?xml version="1.0" encoding="utf-8"?>
<svg height="800px" width="800px" version="1.1" id="file_dynamic" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" xml:space="preserve">
<style type="text/css">
  .st0{fill:{{ColorTranslator.ToHtml(ClipTheme.Text)}};}
  .label{fill:{{ColorTranslator.ToHtml(ClipTheme.Text)}};font-family:Arial, Helvetica, sans-serif;font-weight:900;}
</style>
<g>
  <path class="st0" d="M378.413,0H208.297h-13.182L185.8,9.314L57.02,138.102l-9.314,9.314v13.176v265.514
		c0,47.36,38.528,85.895,85.896,85.895h244.811c47.353,0,85.881-38.535,85.881-85.895V85.896C464.294,38.528,425.766,0,378.413,0z
		 M432.497,426.105c0,29.877-24.214,54.091-54.084,54.091H133.602c-29.884,0-54.098-24.214-54.098-54.091V160.591h83.716
		c24.885,0,45.077-20.178,45.077-45.07V31.804h170.116c29.87,0,54.084,24.214,54.084,54.092V426.105z"/>
  <text class="label" x="256" y="330" text-anchor="middle" dominant-baseline="middle" font-size="{{fontSize}}">{{System.Security.SecurityElement.Escape(text)}}</text>
</g>
</svg>
""";
        var document = SvgDocument.FromSvg<SvgDocument>(svg);
        document.Width = iconSize;
        document.Height = iconSize;
        var canvas = Math.Max(44, iconSize + 12);
        var target = new Bitmap(canvas, canvas);
        using var graphics = Graphics.FromImage(target);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        using var rendered = document.Draw(iconSize, iconSize);
        var offset = (canvas - iconSize) / 2;
        graphics.DrawImage(rendered, offset, offset, iconSize, iconSize);
        return target;
    }

    private static string CleanFileIconLabel(string raw)
    {
        var label = raw.Trim().TrimStart('.').ToUpperInvariant();
        label = new string(label.Where(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '+' or '#' or '-').ToArray());
        return string.IsNullOrWhiteSpace(label) ? "FILE" : label;
    }

    private static int FileIconFontSize(string label)
    {
        return label.Length switch
        {
            <= 2 => 112,
            3 => 96,
            4 => 78,
            5 => 60,
            _ => Math.Max(34, 300 / label.Length),
        };
    }

    private static string ThemeSvgMarkup(string svg)
    {
        var color = ColorTranslator.ToHtml(ClipTheme.Text);
        var themed = Regex.Replace(
            svg,
            @"(?i)(fill|stroke)\s*=\s*[""'](?:#(?:000000|000|1f1f1f|222222|2b2b2b|333333|444444|555555|666666)|black|rgb\(\s*0\s*,\s*0\s*,\s*0\s*\))[""']",
            match => $"{match.Groups[1].Value}=\"{color}\"");
        themed = Regex.Replace(
            themed,
            @"(?i)(fill|stroke)\s*:\s*(?:#(?:000000|000|1f1f1f|222222|2b2b2b|333333|444444|555555|666666)|black|rgb\(\s*0\s*,\s*0\s*,\s*0\s*\))\s*;",
            match => $"{match.Groups[1].Value}:{color};");
        return themed;
    }

    private static string TitleFor(ClipboardHistoryItem item)
    {
        return item.Kind switch
        {
            ClipboardItemKind.Image => item.Preview,
            ClipboardItemKind.Link => item.Text ?? item.Preview,
            ClipboardItemKind.Files => FileTitle(item),
            _ => item.Preview,
        };
    }

    private static string FileTitle(ClipboardHistoryItem item)
    {
        if (item.FilePaths.Count == 1)
        {
            return Path.GetFileName(item.FilePaths[0]);
        }

        return item.FilePaths.Count > 1 ? $"{item.FilePaths.Count} files" : item.Preview;
    }

    private void FillInformation(ClipboardHistoryItem? item)
    {
        try
        {
            _infoWheelRemainder = 0;
            if (item is null)
            {
                HideInfoRows();
                return;
            }

            var rows = BuildInfoRows(item);
            RenderInfoRows(rows);
            UpdateInfoValue("source", SourceDisplayName(item), item.SourceApplicationPath);
            UpdateInfoValue("content_type", item.Kind.ToString());
            UpdateInfoValue("copied", item.LastCopiedAt.LocalDateTime.ToString("g"));
            UpdateInfoValue("times_copied", item.CopyCount.ToString());

            if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
            {
                UpdateInfoValue("characters", (item.CharacterCount ?? item.Text?.Length ?? 0).ToString());
                UpdateInfoValue("words", (item.WordCount ?? 0).ToString());
            }

            if (item.Kind == ClipboardItemKind.Image)
            {
                if (item.ImageWidth is not null && item.ImageHeight is not null)
                {
                    UpdateInfoValue("dimensions", $"{item.ImageWidth} x {item.ImageHeight}");
                }

                if (item.AssetSizeBytes is not null)
                {
                    UpdateInfoValue("image_size", FormatBytes(item.AssetSizeBytes.Value));
                }
            }

            if (item.Kind == ClipboardItemKind.Files)
            {
                UpdateInfoValue("files", item.FilePaths.Count.ToString());
                if (item.FilePaths.Count == 1)
                {
                    var path = item.FilePaths[0];
                    UpdateInfoValue("file_name", Path.GetFileName(path));
                    UpdateInfoValue("file_type", Directory.Exists(path) ? "Folder" : Path.GetExtension(path));
                    if (File.Exists(path))
                    {
                        UpdateInfoValue("file_size", FormatBytes(new FileInfo(path).Length));
                    }
                    else
                    {
                        UpdateInfoValue("file_size", string.Empty);
                    }

                    UpdateInfoValue("file_path", path);
                }
                else if (item.AssetSizeBytes is not null)
                {
                    UpdateInfoValue("total_size", FormatBytes(item.AssetSizeBytes.Value));
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        finally
        {
        }
    }

    private List<InfoRowSpec> BuildInfoRows(ClipboardHistoryItem item)
    {
        var rows = new List<InfoRowSpec>
        {
            new("source", "Source", Wrap: false, HasIcon: true),
            new("content_type", "Content type"),
            new("copied", "Copied"),
            new("times_copied", "Times copied"),
        };

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            rows.Add(new("characters", "Characters"));
            rows.Add(new("words", "Words"));
        }
        else if (item.Kind == ClipboardItemKind.Image)
        {
            rows.Add(new("dimensions", "Dimensions"));
            rows.Add(new("image_size", "Image size"));
        }
        else if (item.Kind == ClipboardItemKind.Files)
        {
            rows.Add(new("files", "Files"));
            if (item.FilePaths.Count == 1)
            {
                rows.Add(new("file_name", "File name", HorizontalScroll: true));
                rows.Add(new("file_type", "File type"));
                rows.Add(new("file_size", "File size"));
                rows.Add(new("file_path", "File path", HorizontalScroll: true));
            }
            else
            {
                rows.Add(new("total_size", "Total size"));
            }
        }

        return rows;
    }

    private void RenderInfoRows(List<InfoRowSpec> specs)
    {
        var nextIds = specs.Select(spec => spec.Id).ToList();
        if (_infoRowOrder.SequenceEqual(nextIds))
        {
            Program.LogDebug($"InfoRows reused ids={string.Join(",", nextIds)}");
            return;
        }

        EnsureInfoRowsCreated();
        var active = nextIds.ToHashSet(StringComparer.Ordinal);
        var old = _infoRowOrder.ToList();
        var reused = nextIds.Where(old.Contains).ToList();
        var added = nextIds.Where(id => !old.Contains(id)).ToList();
        var removed = old.Where(id => !active.Contains(id)).ToList();
        Program.LogDebug($"InfoRows diff reused={string.Join(",", reused)} added={string.Join(",", added)} removed={string.Join(",", removed)} old={string.Join(",", _infoRowOrder)} new={string.Join(",", nextIds)}");

        _infoRows.SuspendLayout();
        try
        {
            _infoRowOrder.Clear();
            foreach (var spec in AllInfoRows())
            {
                SetInfoRowVisible(spec, active.Contains(spec.Id));
            }

            foreach (var id in nextIds)
            {
                _infoRowOrder.Add(id);
            }
        }
        finally
        {
            _infoRows.ResumeLayout();
            UpdateInfoScrollBar();
        }
    }

    private void HideInfoRows()
    {
        EnsureInfoRowsCreated();
        _infoRows.SuspendLayout();
        try
        {
            foreach (var spec in AllInfoRows())
            {
                SetInfoRowVisible(spec, false);
            }

            _infoRowOrder.Clear();
        }
        finally
        {
            _infoRows.ResumeLayout();
            UpdateInfoScrollBar();
        }
    }

    private void EnsureInfoRowsCreated()
    {
        if (_infoRowControls.Count > 0)
        {
            return;
        }

        _infoRows.SuspendLayout();
        try
        {
            foreach (var spec in AllInfoRows())
            {
                AttachInfoRow(spec, CreateInfoRow(spec));
                SetInfoRowVisible(spec, false);
            }

            _infoRowOrder.Clear();
        }
        finally
        {
            _infoRows.ResumeLayout();
            UpdateInfoScrollBar();
        }
    }

    private InfoRowControls CreateInfoRow(InfoRowSpec spec)
    {
        var labelControl = new InfoValueTextBox
        {
            Text = $"{spec.Label}:",
            Dock = DockStyle.Fill,
            TextAlign = HorizontalAlignment.Left,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Multiline = false,
            Font = ClipTheme.InfoFont,
            ForeColor = ClipTheme.MutedText,
            BackColor = ClipTheme.Surface,
            Padding = spec.Wrap ? new Padding(0, 3, 0, 0) : Padding.Empty,
            Margin = new Padding(0, 3, 0, 0),
        };
        labelControl.WheelRedirect = OnInfoRowsMouseWheel;
        labelControl.MouseWheel += OnInfoRowsMouseWheel;

        var valuePanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = ClipTheme.Surface,
        };
        valuePanel.Paint += DrawInfoDivider;
        valuePanel.MouseWheel += OnInfoRowsMouseWheel;

        var iconBox = new PictureBox
        {
            Width = 22,
            Height = 20,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(4, 2, 0, 2),
            Visible = false,
        };

        Control valueControl = new InfoValueTextBox
        {
            Text = string.Empty,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Multiline = spec.Wrap,
            WordWrap = spec.Wrap,
            BackColor = ClipTheme.Surface,
            ForeColor = ClipTheme.Text,
            Font = ClipTheme.InfoFont,
            Width = Math.Max(240, _infoRows.Width - 155),
            Height = spec.Wrap ? 42 : 20,
            Margin = new Padding(0, 3, 0, 0),
            TextAlign = HorizontalAlignment.Right,
            Dock = DockStyle.Fill,
        };

        if (spec.HasIcon)
        {
            valueControl.Dock = DockStyle.None;
            iconBox.MouseWheel += OnInfoRowsMouseWheel;
            valuePanel.Resize += (_, _) => LayoutIconInfoValue(valuePanel, valueControl, iconBox);
            valuePanel.Controls.Add(iconBox);
        }

        if (valueControl is InfoValueTextBox infoValueTextBox)
        {
            infoValueTextBox.WheelRedirect = OnInfoRowsMouseWheel;
        }
        else
        {
            valueControl.MouseWheel += OnInfoRowsMouseWheel;
        }
        valuePanel.Controls.Add(valueControl);
        if (spec.HasIcon)
        {
            LayoutIconInfoValue(valuePanel, valueControl, iconBox);
        }

        return new InfoRowControls(labelControl, valuePanel, valueControl, iconBox);
    }

    private static void LayoutIconInfoValue(Panel panel, Control valueControl, PictureBox iconBox)
    {
        if (!iconBox.Visible)
        {
            valueControl.Bounds = new Rectangle(0, 3, Math.Max(20, panel.ClientSize.Width), 20);
            return;
        }

        var available = Math.Max(40, panel.ClientSize.Width - iconBox.Width - 6);
        var measured = TextRenderer.MeasureText(valueControl.Text, valueControl.Font);
        var valueWidth = Math.Clamp(measured.Width + 8, 40, available);
        var valueLeft = Math.Max(0, panel.ClientSize.Width - valueWidth);
        var iconLeft = Math.Max(0, valueLeft - iconBox.Width - 4);
        iconBox.Bounds = new Rectangle(iconLeft, 2, iconBox.Width, iconBox.Height);
        valueControl.Bounds = new Rectangle(valueLeft, 3, valueWidth, 20);
    }

    private void AttachInfoRow(InfoRowSpec spec, InfoRowControls controls)
    {
        var row = _infoRows.RowCount++;
        _infoRows.RowStyles.Add(spec.Wrap ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 24));

        var expectedLabel = $"{spec.Label}:";
        if (!string.Equals(controls.Label.Text, expectedLabel, StringComparison.Ordinal))
        {
            controls.Label.Text = expectedLabel;
        }

        controls.Label.TextAlign = HorizontalAlignment.Left;
        controls.Label.Padding = spec.Wrap ? new Padding(0, 3, 0, 0) : Padding.Empty;

        _infoRows.Controls.Add(controls.Label, 0, row);
        _infoRows.Controls.Add(controls.ValuePanel, 1, row);
        _infoRowControls[spec.Id] = controls;
        _infoRowOrder.Add(spec.Id);
    }

    private void SetInfoRowVisible(InfoRowSpec spec, bool visible)
    {
        if (!_infoRowControls.TryGetValue(spec.Id, out var controls))
        {
            return;
        }

        controls.Label.Visible = visible;
        controls.ValuePanel.Visible = visible;
        var index = AllInfoRows().FindIndex(row => row.Id == spec.Id);
        if (index < 0 || index >= _infoRows.RowStyles.Count)
        {
            return;
        }

        _infoRows.RowStyles[index].SizeType = visible && spec.Wrap ? SizeType.AutoSize : SizeType.Absolute;
        _infoRows.RowStyles[index].Height = visible ? (spec.Wrap ? 42 : 24) : 0;
    }

    private void UpdateInfoScrollBar()
    {
        if (_infoRowsScroll.ClientSize.Height <= 0)
        {
            return;
        }

        _infoRows.Width = _infoRowsScroll.ClientSize.Width;
        _infoRows.PerformLayout();
        var contentBottom = InfoRowsContentBottom();
        var overflow = Math.Max(0, contentBottom - _infoRowsScroll.ClientSize.Height);
        var hadScroll = _infoScrollBar.Visible;
        _infoScrollBar.Visible = overflow > 0;
        _infoScrollBar.Enabled = overflow > 0;
        _infoScrollBar.SmallChange = 24;
        _infoScrollBar.LargeChange = Math.Max(24, _infoRowsScroll.ClientSize.Height);
        _infoScrollBar.Maximum = overflow;

        if (_infoScrollBar.Value > overflow)
        {
            _infoScrollBar.Value = overflow;
        }

        ScrollInfoRowsTo(_infoScrollBar.Value);
        if (hadScroll != _infoScrollBar.Visible)
        {
            Program.LogDebug($"InfoRows scrollbar visible={_infoScrollBar.Visible} reservedWidth={_infoScrollGutter.Width} overflow={overflow} contentBottom={contentBottom} rowsHeight={_infoRows.Height}");
        }
    }

    private void ScrollInfoRowsTo(int value)
    {
        var nextTop = -Math.Max(0, value);
        if (_infoRows.Top == nextTop && _infoRows.Left == 0)
        {
            return;
        }

        _infoRows.Location = new Point(0, nextTop);
        var actualTop = _infoRows.Top;
        Program.LogDebug($"InfoRows scrolled value={value} expectedTop={nextTop} actualTop={actualTop} dock={_infoRows.Dock} viewport={_infoRowsScroll.ClientSize.Height} contentBottom={InfoRowsContentBottom()} rowsHeight={_infoRows.Height}");
        if (actualTop != nextTop)
        {
            Program.LogDebug($"InfoRows position mismatch expectedTop={nextTop} actualTop={actualTop} anchor={_infoRows.Anchor}");
        }
    }

    private void OnInfoRowsMouseWheel(object? sender, MouseEventArgs e)
    {
        if (!_infoScrollBar.Visible)
        {
            return;
        }

        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }

        _infoWheelRemainder += e.Delta;
        var notches = _infoWheelRemainder / 120;
        if (notches == 0)
        {
            Program.LogDebug($"InfoRows wheel buffered delta={e.Delta} remainder={_infoWheelRemainder} value={_infoScrollBar.Value}");
            return;
        }

        _infoWheelRemainder -= notches * 120;
        var delta = -notches * _infoScrollBar.SmallChange;
        var max = Math.Max(0, InfoRowsContentBottom() - _infoRowsScroll.ClientSize.Height);
        _infoScrollBar.Value = Math.Clamp(_infoScrollBar.Value + delta, 0, max);
        ScrollInfoRowsTo(_infoScrollBar.Value);
        Program.LogDebug($"InfoRows wheel delta={e.Delta} notches={notches} remainder={_infoWheelRemainder} value={_infoScrollBar.Value} max={max}");
    }

    private int InfoRowsContentBottom()
    {
        return _infoRows.Controls
            .Cast<Control>()
            .Where(control => control.Visible)
            .Select(control => control.Bottom)
            .DefaultIfEmpty(_infoRows.Height)
            .Max();
    }

    private static List<InfoRowSpec> AllInfoRows()
    {
        return
        [
            new("source", "Source", Wrap: false, HasIcon: true),
            new("content_type", "Content type"),
            new("copied", "Copied"),
            new("times_copied", "Times copied"),
            new("characters", "Characters"),
            new("words", "Words"),
            new("dimensions", "Dimensions"),
            new("image_size", "Image size"),
            new("files", "Files"),
            new("file_name", "File name", HorizontalScroll: true),
            new("file_type", "File type"),
            new("file_size", "File size"),
            new("total_size", "Total size"),
            new("file_path", "File path", HorizontalScroll: true),
        ];
    }

    private static void DrawInfoDivider(object? sender, PaintEventArgs e)
    {
        if (sender is not Control { Visible: true } control)
        {
            return;
        }

        using var pen = new Pen(ClipTheme.Divider);
        e.Graphics.DrawLine(pen, 0, control.Height - 1, control.Width, control.Height - 1);
    }

    private void UpdateInfoValue(string id, string value, string? iconPath = null)
    {
        if (!_infoRowControls.TryGetValue(id, out var controls))
        {
            return;
        }

        if (!string.Equals(controls.LastValue, value, StringComparison.Ordinal))
        {
            controls.Value.Text = value;
            controls.LastValue = value;
            if (controls.Value is TextBox textBox && textBox.Multiline)
            {
                ResizeWrappedInfoRow(id, textBox, value);
            }
            else if (controls.Value is TextBox singleLineTextBox)
            {
                singleLineTextBox.SelectionStart = id is "file_name" or "file_path" ? 0 : singleLineTextBox.TextLength;
                singleLineTextBox.SelectionLength = 0;
                singleLineTextBox.ScrollToCaret();
                Program.LogDebug($"InfoRows horizontal caret reset id={id} selection={singleLineTextBox.SelectionStart} textLength={singleLineTextBox.TextLength}");
            }

            if (controls.Icon is { } existingIcon)
            {
                LayoutIconInfoValue(controls.ValuePanel, controls.Value, existingIcon);
            }
        }

        if (controls.Icon is { } iconBox)
        {
            var normalizedIconPath = !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath)
                ? iconPath
                : null;
            if (string.Equals(controls.LastIconPath, normalizedIconPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            iconBox.Image?.Dispose();
            iconBox.Image = null;
            iconBox.Visible = false;
            controls.LastIconPath = normalizedIconPath;
            try
            {
                if (normalizedIconPath is not null)
                {
                    iconBox.Image = Icon.ExtractAssociatedIcon(normalizedIconPath)?.ToBitmap();
                    iconBox.Visible = iconBox.Image is not null;
                }

                LayoutIconInfoValue(controls.ValuePanel, controls.Value, iconBox);
            }
            catch
            {
            }
        }
    }

    private void ResizeWrappedInfoRow(string id, TextBox textBox, string value)
    {
        var specIndex = AllInfoRows().FindIndex(row => row.Id == id);
        if (specIndex < 0 || specIndex >= _infoRows.RowStyles.Count)
        {
            return;
        }

        var availableWidth = Math.Max(120, textBox.ClientSize.Width);
        var measured = TextRenderer.MeasureText(
            value,
            textBox.Font,
            new Size(availableWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        var height = Math.Clamp(measured.Height + 12, 42, 420);
        if (textBox.Height != height)
        {
            textBox.Height = height;
            _infoRows.RowStyles[specIndex].Height = height + 6;
            Program.LogDebug($"InfoRows wrapped row resized id={id} height={height} measured={measured.Height} width={availableWidth}");
            UpdateInfoScrollBar();
        }
    }

    private static string SourceDisplayName(ClipboardHistoryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.SourceApplicationPath) && File.Exists(item.SourceApplicationPath))
        {
            var name = Path.GetFileNameWithoutExtension(item.SourceApplicationPath);
            if (name.Equals("olk", StringComparison.OrdinalIgnoreCase))
            {
                return "Outlook";
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return item.SourceApplication?.Equals("olk", StringComparison.OrdinalIgnoreCase) == true ? "Outlook" : item.SourceApplication ?? "Unknown";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private static string TextPayload(ClipboardHistoryItem item)
    {
        var text = item.Text;
        if (string.IsNullOrEmpty(text))
        {
            text = item.Preview;
        }

        if (string.IsNullOrEmpty(text))
        {
            return " ";
        }

        return text;
    }
}

internal sealed class ThinVScrollBar : Control
{
    private bool _dragging;
    private int _dragOffset;
    private int _value;
    private int _maximum;
    private int _largeChange = 80;
    private int _smallChange = 24;

    public event EventHandler? ValueChanged;

    public ThinVScrollBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Width = 8;
        BackColor = ClipTheme.Surface;
        Cursor = Cursors.Hand;
    }

    public bool HandleWheelInternally { get; set; } = true;

    public event MouseEventHandler? WheelMoved;

    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, 0, Maximum);
            if (_value == next)
            {
                return;
            }

            _value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0, value);
            if (_value > _maximum)
            {
                _value = _maximum;
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }

            Invalidate();
        }
    }

    public int LargeChange
    {
        get => _largeChange;
        set
        {
            _largeChange = Math.Max(1, value);
            Invalidate();
        }
    }

    public int SmallChange
    {
        get => _smallChange;
        set => _smallChange = Math.Max(1, value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var track = new SolidBrush(ClipTheme.Surface);
        e.Graphics.FillRectangle(track, ClientRectangle);

        if (Maximum <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var thumb = ThumbRect();
        using var fill = new SolidBrush(ClipTheme.MutedText);
        using var path = RoundedPath(thumb, thumb.Width / 2);
        e.Graphics.FillPath(fill, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var thumb = ThumbRect();
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragOffset = e.Y - thumb.Top;
            Capture = true;
            return;
        }

        Value += e.Y < thumb.Top ? -LargeChange : LargeChange;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        var thumb = ThumbRect();
        var travel = Math.Max(1, ClientSize.Height - thumb.Height);
        var top = Math.Clamp(e.Y - _dragOffset, 0, travel);
        Value = (int)Math.Round(top / (double)travel * Maximum);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (HandleWheelInternally)
        {
            Value -= Math.Sign(e.Delta) * SmallChange;
        }

        WheelMoved?.Invoke(this, e);
        base.OnMouseWheel(e);
    }

    private Rectangle ThumbRect()
    {
        var height = Math.Clamp((int)Math.Round(ClientSize.Height * (LargeChange / (double)(Maximum + LargeChange))), 24, Math.Max(24, ClientSize.Height));
        var travel = Math.Max(1, ClientSize.Height - height);
        var top = Maximum <= 0 ? 0 : (int)Math.Round(Value / (double)Maximum * travel);
        return new Rectangle(Math.Max(0, (ClientSize.Width - 4) / 2), top, 4, height);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ThinHScrollBar : Control
{
    private bool _dragging;
    private int _dragOffset;
    private int _value;
    private int _maximum;
    private int _largeChange = 80;
    private int _smallChange = 24;

    public event EventHandler? ValueChanged;

    public ThinHScrollBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 8;
        BackColor = ClipTheme.Surface;
        Cursor = Cursors.Hand;
    }

    public bool HandleWheelInternally { get; set; } = true;

    public event MouseEventHandler? WheelMoved;

    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, 0, Maximum);
            if (_value == next)
            {
                return;
            }

            _value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0, value);
            if (_value > _maximum)
            {
                _value = _maximum;
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }

            Invalidate();
        }
    }

    public int LargeChange
    {
        get => _largeChange;
        set
        {
            _largeChange = Math.Max(1, value);
            Invalidate();
        }
    }

    public int SmallChange
    {
        get => _smallChange;
        set => _smallChange = Math.Max(1, value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var track = new SolidBrush(ClipTheme.Surface);
        e.Graphics.FillRectangle(track, ClientRectangle);

        if (Maximum <= 0 || ClientSize.Width <= 0)
        {
            return;
        }

        var thumb = ThumbRect();
        using var fill = new SolidBrush(ClipTheme.MutedText);
        using var path = RoundedPath(thumb, thumb.Height / 2);
        e.Graphics.FillPath(fill, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var thumb = ThumbRect();
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragOffset = e.X - thumb.Left;
            Capture = true;
            return;
        }

        Value += e.X < thumb.Left ? -LargeChange : LargeChange;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        var thumb = ThumbRect();
        var travel = Math.Max(1, ClientSize.Width - thumb.Width);
        var left = Math.Clamp(e.X - _dragOffset, 0, travel);
        Value = (int)Math.Round(left / (double)travel * Maximum);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (HandleWheelInternally)
        {
            Value -= Math.Sign(e.Delta) * SmallChange;
        }

        WheelMoved?.Invoke(this, e);
        base.OnMouseWheel(e);
    }

    private Rectangle ThumbRect()
    {
        var width = Math.Clamp((int)Math.Round(ClientSize.Width * (LargeChange / (double)(Maximum + LargeChange))), 24, Math.Max(24, ClientSize.Width));
        var travel = Math.Max(1, ClientSize.Width - width);
        var left = Maximum <= 0 ? 0 : (int)Math.Round(Value / (double)Maximum * travel);
        return new Rectangle(left, Math.Max(0, (ClientSize.Height - 4) / 2), width, 4);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class NativeScrollOverlay
{
    private const int SbHorz = 0;
    private const int SbVert = 1;
    private const int SbBoth = 3;
    private const int SifRange = 0x1;
    private const int SifPage = 0x2;
    private const int SifPos = 0x4;
    private const int SifAll = SifRange | SifPage | SifPos | 0x10;
    private const int WmVScroll = 0x0115;
    private const int WmHScroll = 0x0114;
    private const int WmMouseWheel = 0x020A;
    private const int SbThumbPosition = 4;

    private readonly Control _target;
    private readonly ThinVScrollBar? _vertical;
    private readonly ThinHScrollBar? _horizontal;
    private readonly Panel? _corner;
    private readonly int _nativeVerticalWidth = SystemInformation.VerticalScrollBarWidth;
    private readonly int _nativeHorizontalHeight = SystemInformation.HorizontalScrollBarHeight;
    private bool _syncing;

    public NativeScrollOverlay(Control target, bool vertical, bool horizontal)
    {
        _target = target;
        if (target.Parent is null)
        {
            return;
        }

        if (vertical)
        {
            _vertical = new ThinVScrollBar { Visible = false };
            _vertical.HandleWheelInternally = false;
            _vertical.WheelMoved += (_, e) => ForwardWheelToTarget(e);
            _vertical.ValueChanged += (_, _) => SetNativePosition(SbVert, _vertical.Value);
            target.Parent.Controls.Add(_vertical);
            _vertical.BringToFront();
        }

        if (horizontal)
        {
            _horizontal = new ThinHScrollBar { Visible = false };
            _horizontal.HandleWheelInternally = false;
            _horizontal.WheelMoved += (_, e) => ForwardWheelToTarget(e);
            _horizontal.ValueChanged += (_, _) => SetNativePosition(SbHorz, _horizontal.Value);
            target.Parent.Controls.Add(_horizontal);
            _horizontal.BringToFront();
        }

        if (vertical && horizontal)
        {
            _corner = new Panel { Visible = false, BackColor = target.Parent.BackColor };
            target.Parent.Controls.Add(_corner);
            _corner.BringToFront();
        }

        target.HandleCreated += (_, _) => SyncFromNative();
        target.Resize += (_, _) => SyncFromNative();
        target.LocationChanged += (_, _) => SyncFromNative();
        target.VisibleChanged += (_, _) => SyncFromNative();
        target.Parent.Resize += (_, _) => SyncFromNative();
    }

    private void ForwardWheelToTarget(MouseEventArgs e)
    {
        if (!_target.IsHandleCreated)
        {
            return;
        }

        var screen = Control.MousePosition;
        var wParam = new IntPtr(e.Delta << 16);
        SendMessage(_target.Handle, WmMouseWheel, wParam, MakeLParam(screen.X, screen.Y));
        Program.LogDebug($"NativeScrollOverlay forwarded wheel target={_target.GetType().Name} delta={e.Delta}");
        SyncFromNative();
    }

    public void SyncFromNative()
    {
        if (_target.Parent is null || !_target.IsHandleCreated)
        {
            return;
        }

        _syncing = true;
        try
        {
            var verticalVisible = SyncBar(SbVert, _vertical);
            var horizontalVisible = SyncBar(SbHorz, _horizontal);
            LayoutBars(verticalVisible, horizontalVisible);
            Program.LogDebug($"NativeScrollOverlay sync target={_target.GetType().Name} v={verticalVisible} h={horizontalVisible}");
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool SyncBar(int bar, Control? control)
    {
        if (control is null)
        {
            return false;
        }

        var info = ScrollInfo.Create();
        if (!GetScrollInfo(_target.Handle, bar, ref info))
        {
            control.Visible = false;
            return false;
        }

        var maximum = Math.Max(0, info.NMax - (int)info.NPage + 1);
        var visible = maximum > 0 && _target.Visible;
        control.Visible = visible;
        if (control is ThinVScrollBar vertical)
        {
            vertical.LargeChange = Math.Max(1, (int)info.NPage);
            vertical.Maximum = maximum;
            vertical.Value = Math.Clamp(info.NPos, 0, maximum);
        }
        else if (control is ThinHScrollBar horizontal)
        {
            horizontal.LargeChange = Math.Max(1, (int)info.NPage);
            horizontal.Maximum = maximum;
            horizontal.Value = Math.Clamp(info.NPos, 0, maximum);
        }

        return visible;
    }

    private void LayoutBars(bool verticalVisible, bool horizontalVisible)
    {
        var bounds = _target.Bounds;
        var bottomInset = horizontalVisible ? _nativeHorizontalHeight : 0;
        var rightInset = verticalVisible ? _nativeVerticalWidth : 0;
        if (_vertical is not null)
        {
            _vertical.Bounds = new Rectangle(bounds.Right - _nativeVerticalWidth, bounds.Top, _nativeVerticalWidth, Math.Max(8, bounds.Height - bottomInset));
            _vertical.BringToFront();
        }

        if (_horizontal is not null)
        {
            _horizontal.Bounds = new Rectangle(bounds.Left, bounds.Bottom - _nativeHorizontalHeight, Math.Max(8, bounds.Width - rightInset), _nativeHorizontalHeight);
            _horizontal.BringToFront();
        }

        if (_corner is not null)
        {
            _corner.Visible = verticalVisible && horizontalVisible;
            _corner.Bounds = new Rectangle(bounds.Right - _nativeVerticalWidth, bounds.Bottom - _nativeHorizontalHeight, _nativeVerticalWidth, _nativeHorizontalHeight);
            _corner.BackColor = _target.Parent?.BackColor ?? ClipTheme.Surface;
            _corner.BringToFront();
        }
    }

    private void SetNativePosition(int bar, int value)
    {
        if (_syncing || !_target.IsHandleCreated)
        {
            return;
        }

        var info = ScrollInfo.Create();
        info.FMask = SifPos;
        info.NPos = value;
        SetScrollInfo(_target.Handle, bar, ref info, true);
        var message = bar == SbVert ? WmVScroll : WmHScroll;
        var wParam = new IntPtr((value << 16) | SbThumbPosition);
        SendMessage(_target.Handle, message, wParam, IntPtr.Zero);
        SyncFromNative();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public uint CbSize;
        public uint FMask;
        public int NMin;
        public int NMax;
        public uint NPage;
        public int NPos;
        public int NTrackPos;

        public static ScrollInfo Create() => new()
        {
            CbSize = (uint)Marshal.SizeOf<ScrollInfo>(),
            FMask = SifAll,
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetScrollInfo(IntPtr hwnd, int nBar, ref ScrollInfo scrollInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetScrollInfo(IntPtr hwnd, int nBar, ref ScrollInfo scrollInfo, bool redraw);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr MakeLParam(int low, int high)
    {
        return new IntPtr((high << 16) | (low & 0xffff));
    }
}

internal sealed class ToastBanner : Control
{
    public ToastBanner()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Font = ClipTheme.InfoFont;
        ForeColor = ClipTheme.Text;
        BackColor = Color.Transparent;
    }

    public void SizeToContent()
    {
        var size = TextRenderer.MeasureText(Text, Font);
        Size = new Size(size.Width + 34, 36);
        ApplyRoundedRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Width - 2, Height - 2);
        using var fill = new SolidBrush(ClipTheme.ControlBackground);
        using var border = new Pen(ClipTheme.Border);
        using var path = RoundedPath(rect, 10);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            rect,
            ClipTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = RoundedPath(new Rectangle(0, 0, Width, Height), 10);
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class TextEditorForm : Form
{
    private readonly TextBox _textBox = new();
    private readonly Label _countLabel = new();

    public TextEditorForm(string value)
    {
        Text = "Edit Text";
        Width = 700;
        Height = 500;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = ClipTheme.Surface;
        ForeColor = ClipTheme.Text;
        Font = ClipTheme.UiFont;
        Padding = new Padding(16);

        _textBox.Multiline = true;
        _textBox.ScrollBars = ScrollBars.Vertical;
        _textBox.Dock = DockStyle.Fill;
        _textBox.Text = value;
        _textBox.Font = ClipTheme.MonoFont;
        _textBox.BorderStyle = BorderStyle.None;
        _textBox.BackColor = ClipTheme.PreviewBackground;
        _textBox.ForeColor = ClipTheme.Text;
        _textBox.WordWrap = true;
        _textBox.TextChanged += (_, _) => UpdateCount();

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = ClipTheme.Surface,
            Padding = new Padding(0, 0, 0, 10),
        };
        var title = new Label
        {
            Text = "Edit text",
            Dock = DockStyle.Left,
            Width = 220,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ClipTheme.TitleFont,
            ForeColor = ClipTheme.Text,
        };
        var trim = new Button { Text = "Trim", Width = 68, Height = 30, Dock = DockStyle.Right };
        var wrap = new Button { Text = "Wrap", Width = 68, Height = 30, Dock = DockStyle.Right };
        StyleEditorButton(trim, primary: false);
        StyleEditorButton(wrap, primary: false);
        trim.Click += (_, _) => _textBox.Text = _textBox.Text.Trim();
        wrap.Click += (_, _) =>
        {
            _textBox.WordWrap = !_textBox.WordWrap;
            _textBox.ScrollBars = _textBox.WordWrap ? ScrollBars.Vertical : ScrollBars.Both;
        };
        header.Controls.Add(trim);
        header.Controls.Add(wrap);
        header.Controls.Add(title);

        var editorShell = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            BackColor = ClipTheme.PreviewBackground,
        };
        editorShell.Paint += (_, e) =>
        {
            using var pen = new Pen(ClipTheme.Accent);
            e.Graphics.DrawRectangle(pen, 0, 0, editorShell.Width - 1, editorShell.Height - 1);
        };
        editorShell.Controls.Add(_textBox);

        _countLabel.Dock = DockStyle.Bottom;
        _countLabel.Height = 28;
        _countLabel.TextAlign = ContentAlignment.MiddleLeft;
        _countLabel.Font = ClipTheme.InfoFont;
        _countLabel.ForeColor = ClipTheme.MutedText;
        _countLabel.BackColor = ClipTheme.Surface;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(0, 10, 0, 0),
            BackColor = ClipTheme.Footer,
        };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        StyleEditorButton(save, primary: true);
        StyleEditorButton(cancel, primary: false);
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);

        Controls.Add(editorShell);
        Controls.Add(_countLabel);
        Controls.Add(header);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
        UpdateCount();
        Shown += (_, _) =>
        {
            NativeDarkMode.ApplyToTree(this);
            _textBox.Focus();
            Program.LogDebug("Text editor themed and opened");
        };
    }

    public string Value => _textBox.Text;

    private void UpdateCount()
    {
        var text = _textBox.Text;
        var words = Regex.Matches(text, @"\S+").Count;
        var lines = text.Length == 0 ? 0 : _textBox.Lines.Length;
        _countLabel.Text = $"{text.Length:N0} characters   {words:N0} words   {lines:N0} lines";
    }

    private static void StyleEditorButton(Button button, bool primary)
    {
        button.Width = 92;
        button.Height = 32;
        button.Margin = new Padding(8, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? ClipTheme.Accent : ClipTheme.ControlBackground;
        button.ForeColor = primary ? Color.White : ClipTheme.Text;
        button.FlatAppearance.BorderColor = primary ? ClipTheme.Accent : ClipTheme.Border;
        button.FlatAppearance.MouseOverBackColor = primary ? ClipTheme.SelectionBorder : ClipTheme.Selection;
    }
}

internal sealed class InfoValueTextBox : TextBox
{
    public MouseEventHandler? WheelRedirect { get; set; }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        WheelRedirect?.Invoke(this, e);
    }
}

internal sealed class SettingsForm : Form
{
    private readonly Panel _content = new();
    private readonly Dictionary<string, Button> _navButtons = [];
    private string _activePage = "General";

    public SettingsForm()
    {
        Text = "Clip Settings";
        Width = 720;
        Height = 500;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = ClipTheme.Surface;
        ForeColor = ClipTheme.Text;
        Font = ClipTheme.UiFont;
        Padding = new Padding(16);

        var title = new Label
        {
            Text = "Clip - Settings",
            Dock = DockStyle.Top,
            Height = 42,
            Font = ClipTheme.TitleFont,
            ForeColor = ClipTheme.Text,
            BackColor = ClipTheme.Surface,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ClipTheme.Surface,
        };

        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 150,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = ClipTheme.ControlBackground,
            Padding = new Padding(8),
        };
        foreach (var label in new[] { "General", "History", "Shortcuts", "Appearance" })
        {
            var button = SettingsNavButton(label, label == "General");
            button.Click += (_, _) => ShowPage(label);
            _navButtons[label] = button;
            sidebar.Controls.Add(button);
        }

        _content.Dock = DockStyle.Fill;
        _content.BackColor = ClipTheme.Surface;
        _content.Padding = new Padding(20, 4, 0, 0);
        ShowPage("General");

        shell.Controls.Add(_content);
        shell.Controls.Add(sidebar);
        Controls.Add(shell);
        Controls.Add(title);

        Shown += (_, _) =>
        {
            NativeDarkMode.ApplyToTree(this);
            Program.LogDebug("Settings opened");
        };
    }

    private void ShowPage(string page)
    {
        _activePage = page;
        foreach (var (name, button) in _navButtons)
        {
            StyleSettingsNavButton(button, string.Equals(name, page, StringComparison.Ordinal));
        }

        switch (page)
        {
            case "History":
                BuildSettingsPage("History", [
                    ("Pinned items", "Kept until you unpin them"),
                    ("History limit", "Saved locally"),
                    ("Duplicate handling", "Same content updates copy count"),
                    ("Storage", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip")),
                ]);
                break;
            case "Shortcuts":
                BuildSettingsPage("Shortcuts", [
                    ("Open Clip", "Alt+V"),
                    ("Save debug log", "Ctrl+Shift+L"),
                    ("Paste selected", "Enter"),
                    ("Close", "Esc or click outside"),
                ]);
                break;
            case "Appearance":
                BuildSettingsPage("Appearance", [
                    ("Theme", ClipTheme.IsDark ? "Following Windows dark mode" : "Following Windows light mode"),
                    ("Density", "Compact"),
                    ("Preview style", "Native when available"),
                    ("Accent", "Teal"),
                ]);
                break;
            default:
                BuildSettingsPage("General", [
                    ("Theme", ClipTheme.IsDark ? "System dark" : "System light"),
                    ("Hotkey", "Alt+V"),
                    ("Debug log", "Ctrl+Shift+L"),
                    ("Dismiss", "Click outside or Esc"),
                ]);
                break;
        }
    }

    private static Button SettingsNavButton(string text, bool active)
    {
        var button = new Button
        {
            Text = text,
            Width = 130,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = active ? ClipTheme.Selection : ClipTheme.ControlBackground,
            ForeColor = active ? ClipTheme.Text : ClipTheme.MutedText,
            Font = ClipTheme.InfoFont,
            Margin = new Padding(0, 0, 0, 6),
        };
        StyleSettingsNavButton(button, active);
        return button;
    }

    private static void StyleSettingsNavButton(Button button, bool active)
    {
        button.BackColor = active ? ClipTheme.Selection : ClipTheme.ControlBackground;
        button.ForeColor = active ? ClipTheme.Text : ClipTheme.MutedText;
        button.FlatAppearance.BorderColor = active ? ClipTheme.SelectionBorder : ClipTheme.ControlBackground;
    }

    private void BuildSettingsPage(string title, IEnumerable<(string Label, string Value)> rows)
    {
        _content.Controls.Clear();
        foreach (var (label, value) in rows.Reverse())
        {
            _content.Controls.Add(SettingsRow(label, value));
        }

        _content.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 38,
            Font = ClipTheme.TitleFont,
            ForeColor = ClipTheme.Text,
            BackColor = ClipTheme.Surface,
            TextAlign = ContentAlignment.MiddleLeft,
        });
    }

    private static Control SettingsRow(string label, string value)
    {
        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = ClipTheme.Surface,
            Padding = new Padding(0, 8, 0, 8),
        };
        row.Paint += (_, e) =>
        {
            using var pen = new Pen(ClipTheme.Divider);
            e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1);
        };
        row.Controls.Add(new Label
        {
            Text = value,
            Dock = DockStyle.Right,
            Width = 260,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = ClipTheme.Text,
            BackColor = ClipTheme.Surface,
            Font = ClipTheme.InfoFont,
        });
        row.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Left,
            Width = 180,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = ClipTheme.MutedText,
            BackColor = ClipTheme.Surface,
            Font = ClipTheme.InfoFont,
        });
        return row;
    }
}

internal sealed record InfoRowSpec(string Id, string Label, bool Wrap = false, bool HasIcon = false, bool HorizontalScroll = false);

internal sealed record ClipboardGroupHeader(string Header);

internal enum ClipboardFilter
{
    All,
    Text,
    Images,
    Files,
}

internal sealed class InfoRowControls
{
    public InfoRowControls(InfoValueTextBox label, Panel valuePanel, Control value, PictureBox? icon)
    {
        Label = label;
        ValuePanel = valuePanel;
        Value = value;
        Icon = icon;
    }

    public InfoValueTextBox Label { get; }

    public Panel ValuePanel { get; }

    public Control Value { get; }

    public PictureBox? Icon { get; }

    public string? LastValue { get; set; }

    public string? LastIconPath { get; set; }

    public void Dispose()
    {
        Icon?.Image?.Dispose();
        Label.Dispose();
        ValuePanel.Dispose();
    }
}

internal sealed record AppChoice(string Name, string? ExecutablePath, string Source, bool IsDefault = false, bool IsRecent = false, string? AppUserModelId = null);

internal sealed class ExcelPreviewGrid : DataGridView
{
    private const int WmMouseHWheel = 0x020E;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Shift) == Keys.Shift && HorizontalScrollingOffset >= 0)
        {
            var delta = e.Delta > 0 ? -80 : 80;
            HorizontalScrollingOffset = Math.Max(0, HorizontalScrollingOffset + delta);
            return;
        }

        base.OnMouseWheel(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseHWheel)
        {
            var delta = (short)((m.WParam.ToInt64() >> 16) & 0xffff);
            HorizontalScrollingOffset = Math.Max(0, HorizontalScrollingOffset + (delta > 0 ? -80 : 80));
            return;
        }

        base.WndProc(ref m);
    }
}

internal sealed class OpenWithPickerForm : Form
{
    private readonly string _targetPath;
    private readonly TextBox _search = new();
    private readonly ListView _apps = new();
    private readonly ImageList _icons = new();
    private List<AppChoice> _allApps = [];
    private bool _isLoadingApps = true;
    private static readonly Dictionary<string, Image> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public OpenWithPickerForm(string targetPath)
    {
        _targetPath = targetPath;
        Program.LogDebug($"OpenWith creating target={targetPath}");
        Text = "Open With";
        Width = 680;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;
        BackColor = ClipTheme.Surface;
        Font = ClipTheme.UiFont;
        Padding = new Padding(16);

        _icons.ImageSize = new Size(34, 34);
        _icons.ColorDepth = ColorDepth.Depth32Bit;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 62,
            BackColor = ClipTheme.Surface,
            Padding = new Padding(0, 0, 0, 10),
        };
        var targetIcon = new PictureBox
        {
            Dock = DockStyle.Left,
            Width = 42,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = File.Exists(targetPath) || Directory.Exists(targetPath) ? ShellIconReader.TryGetIcon(targetPath, large: false) ?? SystemIcons.Application.ToBitmap() : SystemIcons.Application.ToBitmap(),
        };
        var headerText = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"Open with\r\n{Path.GetFileName(targetPath)}",
            Font = ClipTheme.TitleFont,
            ForeColor = ClipTheme.Text,
            BackColor = ClipTheme.Surface,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };
        header.Controls.Add(headerText);
        header.Controls.Add(targetIcon);

        _search.Dock = DockStyle.Top;
        _search.PlaceholderText = "Search apps";
        _search.Font = ClipTheme.UiFont;
        _search.BackColor = ClipTheme.ControlBackground;
        _search.ForeColor = ClipTheme.Text;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.Height = 32;
        _search.Margin = new Padding(0, 0, 0, 10);
        _search.TextChanged += (_, _) => RenderApps();

        _apps.Dock = DockStyle.Fill;
        _apps.View = View.Details;
        _apps.FullRowSelect = true;
        _apps.HideSelection = false;
        _apps.MultiSelect = false;
        _apps.HeaderStyle = ColumnHeaderStyle.None;
        _apps.SmallImageList = _icons;
        _apps.BackColor = ClipTheme.Surface;
        _apps.ForeColor = ClipTheme.Text;
        _apps.BorderStyle = BorderStyle.None;
        _apps.Font = ClipTheme.UiFont;
        _apps.Columns.Add("App", 580);
        _apps.DoubleClick += (_, _) => AcceptSelection();
        _apps.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AcceptSelection();
                e.Handled = true;
            }
        };

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            BackColor = ClipTheme.Footer,
            Padding = new Padding(0, 7, 0, 0),
        };
        var browse = new Button
        {
            Text = "Browse...",
            Dock = DockStyle.Right,
            Width = 96,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = ClipTheme.ControlBackground,
            ForeColor = ClipTheme.Text,
            Font = ClipTheme.InfoFont,
        };
        browse.FlatAppearance.BorderColor = ClipTheme.Border;
        browse.Click += (_, _) => BrowseForApp();
        footer.Controls.Add(browse);
        footer.Controls.Add(new Label
        {
            Text = "Enter  Open     Ctrl+Enter  Open and remember",
            AutoSize = true,
            ForeColor = ClipTheme.MutedText,
            BackColor = ClipTheme.Footer,
            Font = ClipTheme.InfoFont,
            Padding = new Padding(0, 4, 0, 0),
        });

        Controls.Add(_apps);
        Controls.Add(_search);
        Controls.Add(header);
        Controls.Add(footer);

        Shown += async (_, _) =>
        {
            NativeDarkMode.ApplyToTree(this);
            Program.LogDebug("OpenWith shown");
            RenderApps();
            _search.Focus();
            await LoadAppsAsync();
        };
        Resize += (_, _) => _apps.Columns[0].Width = Math.Max(120, _apps.ClientSize.Width - 8);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
    }

    public static void WarmIconCache(IEnumerable<AppChoice> apps)
    {
        var watch = Stopwatch.StartNew();
        var count = 0;
        foreach (var app in apps)
        {
            var key = IconKey(app);
            if (IconCache.ContainsKey(key))
            {
                continue;
            }

            IconCache[key] = LoadAppIcon(app);
            count++;
        }

        Program.LogDebug($"OpenWith icon cache warmed count={count} elapsedMs={watch.ElapsedMilliseconds}");
    }

    public AppChoice? SelectedApp { get; private set; }

    private async Task LoadAppsAsync()
    {
        var discoveryWatch = Stopwatch.StartNew();
        try
        {
            var apps = await Task.Run(() => AppDiscovery.GetApps(_targetPath).ToList());
            _allApps = apps;
            Program.LogDebug($"OpenWith loaded apps count={_allApps.Count} elapsedMs={discoveryWatch.ElapsedMilliseconds} target={_targetPath}");
        }
        catch (Exception ex)
        {
            Program.LogDebug($"OpenWith load failed elapsedMs={discoveryWatch.ElapsedMilliseconds} error={ex}");
            _allApps = [];
        }
        finally
        {
            _isLoadingApps = false;
            if (!IsDisposed)
            {
                RenderApps();
            }
        }
    }

    private void RenderApps()
    {
        if (_isLoadingApps)
        {
            _apps.BeginUpdate();
            try
            {
                _apps.Items.Clear();
                _icons.Images.Clear();
                _apps.Items.Add(new ListViewItem("Loading apps..."));
            }
            finally
            {
                _apps.EndUpdate();
            }

            return;
        }

        var query = _search.Text.Trim();
        var apps = _allApps
            .Where(app => string.IsNullOrWhiteSpace(query) ||
                app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (app.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                (app.AppUserModelId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(app => app.IsDefault)
            .ThenByDescending(app => app.IsRecent)
            .ThenBy(app => Score(app, query))
            .ThenBy(app => app.Name)
            .Take(80)
            .ToList();

        _apps.BeginUpdate();
        try
        {
            _apps.Items.Clear();
            _icons.Images.Clear();
            foreach (var app in apps)
            {
                var key = app.ExecutablePath ?? app.AppUserModelId ?? "default";
                if (!_icons.Images.ContainsKey(key))
                {
                    _icons.Images.Add(key, AppIcon(app));
                }

                var subtitle = app.IsDefault ? "Default app" : app.Source;
                _apps.Items.Add(new ListViewItem($"{app.Name}    {subtitle}")
                {
                    Tag = app,
                    ImageKey = key,
                });
            }

            if (_apps.Items.Count > 0)
            {
                _apps.Items[0].Selected = true;
                _apps.Items[0].Focused = true;
            }
        }
        finally
        {
            _apps.EndUpdate();
        }
    }

    private static int Score(AppChoice app, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        return app.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? -20 : -10;
    }

    private static Image AppIcon(AppChoice app)
    {
        var key = IconKey(app);
        if (IconCache.TryGetValue(key, out var cached))
        {
            return new Bitmap(cached);
        }

        var watch = Stopwatch.StartNew();
        var image = LoadAppIcon(app);
        IconCache[key] = image;
        Program.LogDebug($"OpenWith icon loaded app={app.Name} elapsedMs={watch.ElapsedMilliseconds} key={key}");
        return new Bitmap(image);
    }

    private static string IconKey(AppChoice app)
    {
        return app.ExecutablePath ?? app.AppUserModelId ?? app.Name;
    }

    private static Image LoadAppIcon(AppChoice app)
    {
        try
        {
            if (ShouldPreferStartMenuIcon(app.Name))
            {
                var startMenuIcon = StartMenuIconLookup.TryGetIcon(app.Name);
                if (startMenuIcon is not null)
                {
                    Program.LogDebug($"OpenWith icon start-menu preferred app={app.Name}");
                    return startMenuIcon;
                }

                var packageLogo = PackageLogoLookup.TryGetIcon(app.AppUserModelId);
                if (packageLogo is not null)
                {
                    Program.LogDebug($"OpenWith icon package-logo preferred app={app.Name}");
                    return packageLogo;
                }
            }

            if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
            {
                var shellIcon = ShellIconReader.TryGetIcon($"shell:AppsFolder\\{app.AppUserModelId}", large: false);
                if (shellIcon is not null)
                {
                    return shellIcon;
                }

                var packageLogo = PackageLogoLookup.TryGetIcon(app.AppUserModelId);
                if (packageLogo is not null)
                {
                    Program.LogDebug($"OpenWith icon package-logo fallback app={app.Name}");
                    return packageLogo;
                }
            }

            if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
            {
                using var icon = Icon.ExtractAssociatedIcon(app.ExecutablePath);
                if (icon is not null)
                {
                    return icon.ToBitmap();
                }
            }

            var fallbackStartMenuIcon = StartMenuIconLookup.TryGetIcon(app.Name);
            if (fallbackStartMenuIcon is not null)
            {
                Program.LogDebug($"OpenWith icon start-menu fallback app={app.Name}");
                return fallbackStartMenuIcon;
            }
        }
        catch
        {
        }

        Program.LogDebug($"OpenWith generic icon app={app.Name} source={app.Source} aumid={app.AppUserModelId} exe={app.ExecutablePath}");
        return SystemIcons.Application.ToBitmap();
    }

    private static bool ShouldPreferStartMenuIcon(string appName)
    {
        var name = appName.Trim().ToLowerInvariant();
        return name is "calendar" or "contacts" or "drawboard pdf" or "find my iphone" or "icloud" or "keynote" or "mail" or "notes" or "numbers";
    }

    private void AcceptSelection()
    {
        if (_apps.SelectedItems.Count == 0)
        {
            return;
        }

        SelectedApp = _apps.SelectedItems[0].Tag as AppChoice;
        if (SelectedApp is null)
        {
            return;
        }

        OpenWithRecentStore.Save(_targetPath, SelectedApp);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseForApp()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose an app",
            Filter = "Applications|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SelectedApp = new AppChoice(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName, "Browse");
        OpenWithRecentStore.Save(_targetPath, SelectedApp);
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal static class AppLauncher
{
    public static void OpenWith(string targetPath, AppChoice app)
    {
        if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
        {
            PackagedAppLauncher.OpenFile(app.AppUserModelId, targetPath);
            return;
        }

        if (app.IsDefault || string.IsNullOrWhiteSpace(app.ExecutablePath))
        {
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(app.ExecutablePath, Quote(targetPath))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app.ExecutablePath) ?? Environment.CurrentDirectory,
        });
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

internal static class AppDiscovery
{
    private static IReadOnlyList<AppChoice>? _desktopAppCache;

    public static IReadOnlyList<AppChoice> GetApps(string targetPath)
    {
        var apps = new List<AppChoice>
        {
            new("Default app", null, "Windows", IsDefault: true),
        };

        AddAssociatedApp(apps, Path.GetExtension(targetPath));
        apps.AddRange(OpenWithRecentStore.Load(targetPath));
        apps.AddRange(DesktopApps());

        return apps
            .Where(app => app.IsDefault ||
                !string.IsNullOrWhiteSpace(app.AppUserModelId) ||
                (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath)))
            .Where(IsUsefulOpenWithApp)
            .GroupBy(NormalizedAppName, StringComparer.OrdinalIgnoreCase)
            .Select(BestChoice)
            .OrderByDescending(app => app.IsDefault)
            .ThenByDescending(app => app.IsRecent)
            .ThenBy(app => app.Name)
            .ToList();
    }

    private static IReadOnlyList<AppChoice> DesktopApps()
    {
        return _desktopAppCache ??= PackagedApps().Concat(StartMenuApps()).Concat(AppPathRegistryApps()).ToList();
    }

    private static bool IsUsefulOpenWithApp(AppChoice app)
    {
        if (app.IsDefault || app.IsRecent || app.Source == "Recommended")
        {
            return true;
        }

        var name = app.Name.ToLowerInvariant();
        if (name is "acrobatinfo" or "7zfm" or "acrodist" or "acrobat" or "adobe acrobat distiller" or "magnifier" or "magnify" or "node.js website" or "chrome" or "dokumentation")
        {
            return false;
        }

        if (name.Contains("help") ||
            name.Contains("documentation") ||
            name.Contains("dokumentation") ||
            name.Contains("uninstall") ||
            name.Contains("support") ||
            name.Contains("update") ||
            name.Contains("feedback") ||
            name.Contains("component services") ||
            name.Contains("event viewer") ||
            name.Contains("control panel") ||
            name.Contains("command prompt") ||
            name.Contains("console") ||
            name.Contains("debugging") ||
            name.Contains("application verifier") ||
            name.Contains("defragment") ||
            name.Contains("disk cleanup") ||
            name.Contains("global flags") ||
            name.Contains("gflags") ||
            name.Contains("usb recovery") ||
            name.Contains("administrative tools") ||
            name.Contains("computer management") ||
            name.Contains("license manager") ||
            name.Contains("ghostscript") ||
            name.Contains("git bash") ||
            name.Contains("git cmd") ||
            name.Contains("git gui") ||
            name.Contains("git for windows") ||
            name.Contains("git release") ||
            name.Contains("git faq") ||
            name.Contains("idle (python") ||
            name.Contains("homepage") ||
            name.Contains("faq") ||
            name.Contains("get started") ||
            name.Contains("live captions") ||
            name.Contains("hyper-v quick create") ||
            name.Contains("install additional tools") ||
            name.Contains("gpview") ||
            name.Contains("iscsicli") ||
            name.Contains("local security policy") ||
            name.Contains("mail app wizard") ||
            name.Contains("msoadfsb") ||
            name.Contains("msoasb") ||
            name.Contains("iediagcmd") ||
            name.Equals("iexplore") ||
            name.Contains("importwizard") ||
            name.Contains("iexpress") ||
            name.Contains("diagnostic"))
        {
            return false;
        }

        return true;
    }

    private static string NormalizedAppName(AppChoice app)
    {
        if (app.IsDefault)
        {
            return "default";
        }

        var name = app.Name;
        foreach (var suffix in new[] { " app", " file manager", " x64", " wow", " (preview)" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
            }
        }

        return name.Trim();
    }

    private static AppChoice BestChoice(IEnumerable<AppChoice> choices)
    {
        return choices
            .OrderByDescending(app => app.IsDefault)
            .ThenByDescending(app => app.IsRecent)
            .ThenByDescending(app => app.Source == "Recommended")
            .ThenByDescending(app => app.Source == "Start Menu")
            .ThenByDescending(app => app.Source == "Installed app")
            .First();
    }

    private static void AddAssociatedApp(List<AppChoice> apps, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return;
        }

        var executable = AssociationQuery.GetString(extension, AssociationString.Executable, "open");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        var friendlyName = AssociationQuery.GetString(extension, AssociationString.FriendlyAppName, "open");
        apps.Add(new(string.IsNullOrWhiteSpace(friendlyName) ? Path.GetFileNameWithoutExtension(executable) : friendlyName, executable, "Recommended"));
    }

    private static IEnumerable<AppChoice> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var link in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                var target = ShortcutResolver.Resolve(link);
                if (!string.IsNullOrWhiteSpace(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                {
                    yield return new(Path.GetFileNameWithoutExtension(link), target, "Start Menu");
                }
            }
        }
    }

    private static IEnumerable<AppChoice> PackagedApps()
    {
        foreach (var app in PackagedAppDiscovery.GetStartApps())
        {
            yield return new(app.Name, null, "Store app", AppUserModelId: app.AppUserModelId);
        }
    }

    private static IEnumerable<AppChoice> AppPathRegistryApps()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null)
            {
                continue;
            }

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var appKey = key.OpenSubKey(subKeyName);
                var path = appKey?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (File.Exists(path))
                {
                    yield return new(Path.GetFileNameWithoutExtension(path), path, "Installed app");
                }
            }
        }
    }
}

internal static class ShortcutResolver
{
    public static string? Resolve(string linkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(linkPath);
            string targetPath = shortcut.TargetPath;
            return Environment.ExpandEnvironmentVariables(targetPath);
        }
        catch
        {
            return null;
        }
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

internal static class NativeDarkMode
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static void ApplyToTree(Control root)
    {
        if (!ClipTheme.IsDark)
        {
            return;
        }

        var count = 0;
        ApplyToTree(root, ref count);
        Program.LogDebug($"DarkMode applied control tree count={count}");
    }

    private static void ApplyToTree(Control root, ref int count)
    {
        Apply(root);
        count++;
        foreach (Control child in root.Controls)
        {
            ApplyToTree(child, ref count);
        }
    }

    private static void Apply(Control control)
    {
        if (!control.IsHandleCreated)
        {
            control.HandleCreated += (_, _) => Apply(control);
            return;
        }

        try
        {
            var useDark = 1;
            DwmSetWindowAttribute(control.Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);
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

internal sealed record PackagedAppInfo(string Name, string AppUserModelId);

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

internal static class PackagedAppDiscovery
{
    private static IReadOnlyList<PackagedAppInfo>? _cache;

    public static IReadOnlyList<PackagedAppInfo> GetStartApps()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return _cache = [];
            }

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3500);
            if (string.IsNullOrWhiteSpace(json))
            {
                return _cache = [];
            }

            var items = JsonSerializer.Deserialize<List<StartAppJson>>(json) ?? [];
            _cache = items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.AppID))
                .Select(item => new PackagedAppInfo(item.Name!, item.AppID!))
                .ToList();
            return _cache;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return _cache = [];
        }
    }

    private sealed class StartAppJson
    {
        public string? Name { get; set; }
        public string? AppID { get; set; }
    }
}

internal static class PackagedAppLauncher
{
    public static void OpenFile(string appUserModelId, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("Target file was not found.", path);
        }

        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var shellItem);
        try
        {
            SHCreateShellItemArrayFromShellItem(shellItem, typeof(IShellItemArray).GUID, out var shellItemArray);
            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                var hr = manager.ActivateForFile(appUserModelId, shellItemArray, null, out _);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            finally
            {
                if (shellItemArray is not null && Marshal.IsComObject(shellItemArray))
                {
                    Marshal.FinalReleaseComObject(shellItemArray);
                }
            }
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
            {
                Marshal.FinalReleaseComObject(shellItem);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromShellItem(
        IShellItem psi,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemArray ppv);
}

[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
internal interface IApplicationActivationManager
{
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        uint options,
        out uint processId);

    int ActivateForFile(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IShellItemArray itemArray,
        [MarshalAs(UnmanagedType.LPWStr)] string? verb,
        out uint processId);

    int ActivateForProtocol(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IShellItemArray itemArray,
        out uint processId);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal interface IShellItem;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal interface IShellItemArray;

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

internal static class OpenWithRecentStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "open-with-recent.json");

    public static IReadOnlyList<AppChoice> Load(string targetPath)
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return [];
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, List<RecentApp>>>(File.ReadAllText(StorePath)) ?? [];
            return data.TryGetValue(ExtensionKey(targetPath), out var recent)
                ? recent
                    .Where(app => !string.IsNullOrWhiteSpace(app.AppUserModelId) || (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath)))
                    .Select(app => new AppChoice(app.Name, app.ExecutablePath, "Recent", IsRecent: true, AppUserModelId: app.AppUserModelId))
                    .ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(string targetPath, AppChoice app)
    {
        if (app.IsDefault || (string.IsNullOrWhiteSpace(app.ExecutablePath) && string.IsNullOrWhiteSpace(app.AppUserModelId)))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var data = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<RecentApp>>>(File.ReadAllText(StorePath)) ?? []
                : [];
            var key = ExtensionKey(targetPath);
            if (!data.TryGetValue(key, out var recent))
            {
                recent = [];
                data[key] = recent;
            }

            var appKey = app.AppUserModelId ?? app.ExecutablePath ?? string.Empty;
            recent.RemoveAll(item => item.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, new RecentApp(app.Name, app.ExecutablePath, app.AppUserModelId));
            if (recent.Count > 8)
            {
                recent.RemoveRange(8, recent.Count - 8);
            }

            File.WriteAllText(StorePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    private static string ExtensionKey(string targetPath)
    {
        return Directory.Exists(targetPath) ? "<folder>" : Path.GetExtension(targetPath).ToLowerInvariant();
    }

    private sealed record RecentApp(string Name, string? ExecutablePath, string? AppUserModelId)
    {
        public string AppKey => AppUserModelId ?? ExecutablePath ?? string.Empty;
    }
}

internal enum AssociationString
{
    Executable = 2,
    FriendlyAppName = 4,
    ShellExtension = 16,
}

internal static class AssociationQuery
{
    public static string? GetString(string association, AssociationString value, string? extra)
    {
        try
        {
            uint length = 0;
            _ = AssocQueryString(0, value, association, extra, null, ref length);
            if (length == 0)
            {
                return null;
            }

            var builder = new StringBuilder((int)length);
            return AssocQueryString(0, value, association, extra, builder, ref length) == 0 ? builder.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int AssocQueryString(
        int flags,
        AssociationString str,
        string pszAssoc,
        string? pszExtra,
        StringBuilder? pszOut,
        ref uint pcchOut);
}

internal sealed class ShellPreviewPanel : Panel
{
    private IPreviewHandler? _handler;
    private readonly ShellPreviewMessageFilter _messageFilter;
    private readonly Panel _wordViewportGutter = new();
    private string? _currentPath;
    private int _previewVersion;

    public ShellPreviewPanel()
    {
        _messageFilter = new ShellPreviewMessageFilter(this, () => UserInteracted?.Invoke(this, EventArgs.Empty), () => IsVisioPreview, DirectChildWindow);
        Application.AddMessageFilter(_messageFilter);
        _wordViewportGutter.BackColor = ClipTheme.PreviewBackground;
        _wordViewportGutter.Visible = false;
        _wordViewportGutter.Enabled = false;
        Controls.Add(_wordViewportGutter);
    }

    public event EventHandler? UserInteracted;

    private bool IsVisioPreview => _currentPath is not null &&
        Path.GetExtension(_currentPath) is string extension &&
        (extension.Equals(".vsdx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vsd", StringComparison.OrdinalIgnoreCase));

    private bool IsWordPreview => _currentPath is not null &&
        Path.GetExtension(_currentPath) is string extension &&
        (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".doc", StringComparison.OrdinalIgnoreCase));

    public bool TryPreview(string path)
    {
        ClearPreview();
        var version = ++_previewVersion;
        try
        {
            var clsid = PreviewHandlerResolver.GetPreviewHandlerClsid(path);
            if (clsid is null)
            {
                return false;
            }

            var type = Type.GetTypeFromCLSID(clsid.Value);
            var instance = type is null ? null : Activator.CreateInstance(type);
            if (instance is not IPreviewHandler handler || instance is not IInitializeWithFile initializer)
            {
                return false;
            }

            initializer.Initialize(path, 0);
            var rect = PreviewRect();
            Program.LogDebug($"ShellPreview SetWindow path={path} visible={Visible} client={ClientSize.Width}x{ClientSize.Height}");
            handler.SetWindow(Handle, ref rect);
            handler.DoPreview();
            _handler = handler;
            _currentPath = path;
            FocusPreviewWindow();
            RefreshPreviewBounds();
            _ = RefreshPreviewBoundsLaterAsync(version, 150);
            _ = RefreshPreviewBoundsLaterAsync(version, 600);
            _ = RefreshPreviewBoundsLaterAsync(version, 1200);
            _ = RefreshPreviewBoundsLaterAsync(version, 2200);
            Program.LogDebug($"ShellPreview active path={path} clsid={clsid}");
            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            ClearPreview();
            return false;
        }
    }

    public void ClearPreview()
    {
        try
        {
            _handler?.Unload();
        }
        catch
        {
        }

        if (_handler is not null && Marshal.IsComObject(_handler))
        {
            Marshal.FinalReleaseComObject(_handler);
        }

        _handler = null;
        _currentPath = null;
        UpdateWordViewportGutter(false);
    }

    public void RefreshPreviewBounds()
    {
        if (_handler is null)
        {
            return;
        }

        try
        {
            var rect = PreviewRect();
            _handler.SetRect(ref rect);
            FitChildWindowsToClient();
            UpdateWordViewportGutter(IsWordPreview);

            Program.LogDebug($"ShellPreview bounds refreshed path={_currentPath} client={ClientSize.Width}x{ClientSize.Height}");
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }

    private async Task RefreshPreviewBoundsLaterAsync(int version, int delayMs)
    {
        await Task.Delay(delayMs);
        if (IsDisposed || version != _previewVersion || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && version == _previewVersion)
                {
                    RefreshPreviewBounds();
                }
            }));
        }
        catch
        {
        }
    }

    public void FocusPreviewWindow()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var child = DirectChildWindow();
        var focused = SetFocus(child == IntPtr.Zero ? Handle : child);
        Program.LogDebug($"ShellPreview focused child={child} result={focused}");
    }

    private IntPtr DirectChildWindow()
    {
        if (!IsHandleCreated)
        {
            return IntPtr.Zero;
        }

        var child = IntPtr.Zero;
        while (true)
        {
            child = FindWindowEx(Handle, child, null, null);
            if (child == IntPtr.Zero || child != _wordViewportGutter.Handle)
            {
                return child;
            }
        }
    }

    private void FitChildWindowsToClient()
    {
        var children = new List<IntPtr>();
        var child = IntPtr.Zero;
        while (true)
        {
            child = FindWindowEx(Handle, child, null, null);
            if (child == IntPtr.Zero)
            {
                break;
            }

            if (child == _wordViewportGutter.Handle)
            {
                continue;
            }

            children.Add(child);
        }

        foreach (var childHandle in children)
        {
            MoveWindow(childHandle, 0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), true);
            LogChildWindowBounds(childHandle);
        }

        if (children.Count > 0)
        {
            Program.LogDebug($"ShellPreview direct child windows fitted count={children.Count} client={ClientSize.Width}x{ClientSize.Height}");
        }
    }

    private void UpdateWordViewportGutter(bool visible)
    {
        if (!_wordViewportGutter.IsHandleCreated && !IsHandleCreated)
        {
            return;
        }

        if (!visible)
        {
            _wordViewportGutter.Visible = false;
            return;
        }

        var width = Math.Max(1, SystemInformation.VerticalScrollBarWidth);
        _wordViewportGutter.SetBounds(
            Math.Max(0, ClientSize.Width - width),
            0,
            width,
            Math.Max(1, ClientSize.Height));
        _wordViewportGutter.Visible = true;
        _wordViewportGutter.BringToFront();
        Program.LogDebug($"ShellPreview word viewport gutter visible width={width} client={ClientSize.Width}x{ClientSize.Height}");
    }

    private void LogChildWindowBounds(IntPtr childHandle)
    {
        if (!GetWindowRect(childHandle, out var childRect) || !GetWindowRect(Handle, out var hostRect))
        {
            return;
        }

        Program.LogDebug(
            $"ShellPreview child bounds hwnd={childHandle} rel={childRect.Left - hostRect.Left},{childRect.Top - hostRect.Top},{childRect.Width}x{childRect.Height} host={ClientSize.Width}x{ClientSize.Height} word={IsWordPreview} visio={IsVisioPreview}");
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        RefreshPreviewBounds();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(_messageFilter);
            ClearPreview();
        }

        base.Dispose(disposing);
    }

    private NativeRect ClientRect()
    {
        return new NativeRect { Left = 0, Top = 0, Right = ClientSize.Width, Bottom = ClientSize.Height };
    }

    private NativeRect PreviewRect()
    {
        return ClientRect();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);
}

internal sealed class ShellPreviewMessageFilter(Control owner, Action markInteraction, Func<bool> isVisioPreview, Func<IntPtr> previewChildWindow) : IMessageFilter
{
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseWheel = 0x020A;
    private const int WmContextMenu = 0x007B;
    private const int WmCommand = 0x0111;

    public bool PreFilterMessage(ref Message m)
    {
        if (!owner.IsHandleCreated || !owner.Visible)
        {
            return false;
        }

        if (m.HWnd != owner.Handle && !IsChild(owner.Handle, m.HWnd))
        {
            return false;
        }

        switch (m.Msg)
        {
            case WmLButtonDown:
            case WmRButtonDown:
            case WmRButtonUp:
            case WmMouseWheel:
            case WmContextMenu:
            case WmCommand:
                markInteraction();
                Program.LogDebug($"ShellPreview msg=0x{m.Msg:X} hwnd={m.HWnd} wparam={m.WParam} lparam={m.LParam}");
                if (m.Msg == WmLButtonDown || m.Msg == WmRButtonDown)
                {
                    SetFocus(m.HWnd);
                    Program.LogDebug($"ShellPreview focused hwnd={m.HWnd} visio={isVisioPreview()}");
                }

                if (m.Msg == WmRButtonDown && isVisioPreview())
                {
                    GetCursorPos(out var cursor);
                    Program.LogDebug($"ShellPreview Visio right-click native hwnd={m.HWnd} cursor={cursor.X},{cursor.Y}");
                    ForwardVisioMouseMessage(m.Msg, m.WParam, cursor);
                }

                if (m.Msg == WmRButtonUp && isVisioPreview())
                {
                    GetCursorPos(out var cursor);
                    ForwardVisioMouseMessage(m.Msg, m.WParam, cursor);
                    var child = previewChildWindow();
                    if (child != IntPtr.Zero)
                    {
                        var screenLParam = MakePointLParam(cursor);
                        SendMessage(child, WmContextMenu, child, screenLParam);
                        Program.LogDebug($"ShellPreview Visio context-menu sent child={child} source={m.HWnd} cursor={cursor.X},{cursor.Y} lparam={screenLParam}");
                    }
                }
                break;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref Point lpPoint);

    private void ForwardVisioMouseMessage(int msg, IntPtr wParam, Point screenPoint)
    {
        var child = previewChildWindow();
        if (child == IntPtr.Zero || child == owner.Handle)
        {
            return;
        }

        var clientPoint = screenPoint;
        ScreenToClient(child, ref clientPoint);
        var clientLParam = MakePointLParam(clientPoint);
        SendMessage(child, msg, wParam, clientLParam);
        Program.LogDebug($"ShellPreview Visio mouse forwarded msg=0x{msg:X} child={child} screen={screenPoint.X},{screenPoint.Y} client={clientPoint.X},{clientPoint.Y}");
    }

    private static IntPtr MakePointLParam(Point point)
    {
        var x = point.X & 0xffff;
        var y = point.Y & 0xffff;
        return (IntPtr)((y << 16) | x);
    }
}

internal sealed class PaletteShortcutMessageFilter(Form owner, Action writeSnapshot) : IMessageFilter
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    public bool PreFilterMessage(ref Message m)
    {
        if (!owner.Visible || owner.IsDisposed || (m.Msg != WmKeyDown && m.Msg != WmSysKeyDown))
        {
            return false;
        }

        if ((Keys)m.WParam.ToInt32() != Keys.L)
        {
            return false;
        }

        if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) != (Keys.Control | Keys.Shift))
        {
            return false;
        }

        var foreground = GetForegroundWindow();
        if (foreground != owner.Handle && !IsChild(owner.Handle, foreground))
        {
            return false;
        }

        Program.LogDebug($"Palette shortcut snapshot captured hwnd={m.HWnd} foreground={foreground}");
        writeSnapshot();
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
}

internal static class PreviewHandlerResolver
{
    private const string PreviewHandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    public static Guid? GetPreviewHandlerClsid(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var value = AssociationQuery.GetString(extension, AssociationString.ShellExtension, PreviewHandlerKey);
        return Guid.TryParse(value, out var clsid) ? clsid : null;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("8895B1C6-B41F-4C1C-A562-0D564250836F")]
internal interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref NativeRect rect);
    void SetRect(ref NativeRect rect);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr phwnd);
    [PreserveSig]
    int TranslateAccelerator(IntPtr pmsg);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
internal interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
}

internal sealed class ImagePreviewBox : Control
{
    private Image? _image;

    public Image? PreviewImage
    {
        get => _image;
        private set
        {
            _image?.Dispose();
            _image = value;
            Invalidate();
        }
    }

    public void SetImage(Image image)
    {
        PreviewImage = image;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image?.Dispose();
            _image = null;
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs pe)
    {
        pe.Graphics.Clear(BackColor);
        if (_image is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        pe.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        pe.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        var padding = 14;
        var availableWidth = Math.Max(1, ClientSize.Width - padding * 2);
        var availableHeight = Math.Max(1, ClientSize.Height - padding * 2);
        var heightScale = (double)availableHeight / _image.Height;
        var widthScale = (double)availableWidth / _image.Width;
        var scale = Math.Min(1.0, Math.Min(heightScale, widthScale));
        var width = (int)Math.Round(_image.Width * scale);
        var height = (int)Math.Round(_image.Height * scale);
        var x = (ClientSize.Width - width) / 2;
        var y = (ClientSize.Height - height) / 2;
        pe.Graphics.DrawImage(_image, new Rectangle(x, y, width, height));
    }
}
