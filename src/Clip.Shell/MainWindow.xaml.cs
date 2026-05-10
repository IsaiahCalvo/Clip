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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
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
using WpfTextBox = System.Windows.Controls.TextBox;
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

internal sealed class ClipShellSettings
{
    public ClipThemePreference Theme { get; set; } = ClipThemePreference.System;
    public AppIconPreference AppIcon { get; set; } = AppIconPreference.Light;

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

            return JsonSerializer.Deserialize<ClipShellSettings>(File.ReadAllText(SettingsPath)) ?? new ClipShellSettings();
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
            ShellLog.Info($"settings saved path={SettingsPath} theme={Theme} appIcon={AppIcon}");
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "settings save failed");
        }
    }
}

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x4350;
    private const int ModAlt = 0x0001;
    private const int VkV = 0x56;
    private const int WmHotkey = 0x0312;
    private const int WmClipboardUpdate = 0x031D;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int DwmwaWindowCornerPreference = 33;

    private readonly ClipboardHistoryStore _store = new();
    private readonly Dictionary<string, Border> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly System.Windows.Threading.DispatcherTimer _hotkeyRetryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<ClipboardHistoryItem> _allItems = [];
    private ClipboardHistoryItem? _selected;
    private HwndSource? _source;
    private bool _hotkeyRegistered;
    private string _kindFilter = "all";
    private string _dateFilter = "all";
    private string _fileFilter = "all";
    private int _previewToken;
    private bool _suppressDeactivate;
    private bool _itemsDirtySinceRender = true;
    private bool _paletteRequested;
    private IntPtr _returnFocusHwnd;
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
    private readonly ClipShellSettings _settings = ClipShellSettings.Load();
    public bool KeepOpenForDebug { get; set; }
    internal AppIconPreference AppIconPreference => _settings.AppIcon;
    internal event Action<AppIconPreference>? AppIconChanged;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTheme(_settings.Theme, save: false);
        ApplyAppIcon(_settings.AppIcon, save: false);
        Opacity = 0;
        SettingsIcon.Source = RenderSvg("settings-svgrepo-com.svg", 24);
        DateDropIcon.Source = RenderSvg("dropdown-arrow-svgrepo-com.svg", 24);
        FileDropIcon.Source = RenderSvg("dropdown-arrow-svgrepo-com.svg", 24);
        ExpandImageIcon.Source = RenderSvg("expand-alt-svgrepo-com.svg", 24);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _hotkeyRetryTimer.Tick += (_, _) => EnsureHotkeyRegistered("retry");
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
            OpenWithWindow.WarmCacheAsync();
            ClipboardSharePayload.CleanupStaleTemporaryFiles();
        };

        Closing += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hotkeyRetryTimer.Stop();
            if (_hotkeyRegistered)
            {
                var released = UnregisterHotKey(hwnd, HotkeyId);
                ShellLog.Info($"Alt+V hotkey unregistered={released} hwnd={hwnd} win32={Marshal.GetLastWin32Error()}");
                _hotkeyRegistered = false;
            }

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
            _returnFocusHwnd = foreground;
        }

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
        Activate();
        SearchBox.Focus();
        ShellLog.Info($"palette shown elapsedMs={watch.ElapsedMilliseconds} selected={_selected?.Id ?? "none"} rows={_rows.Count} dirty={_itemsDirtySinceRender}");

        if (_itemsDirtySinceRender || _rows.Count == 0)
        {
            Dispatcher.BeginInvoke(() => LoadItems(selectFirst: _selected is null, reason: "show-refresh"), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void ConcealPalette(string reason)
    {
        Opacity = 0;
        IsHitTestVisible = false;
        MoveOffscreen();
        ShellLog.Info($"palette concealed reason={reason}");
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
        else if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ShellLog.Info("Alt+V received");
            ShowPalette();
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
        if (_hotkeyRegistered)
        {
            _hotkeyRetryTimer.Stop();
            return true;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            ShellLog.Info($"Alt+V hotkey skipped reason={reason} hwnd=0");
            return false;
        }

        _hotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, ModAlt, VkV);
        var win32 = Marshal.GetLastWin32Error();
        ShellLog.Info($"Alt+V hotkey register reason={reason} registered={_hotkeyRegistered} hwnd={hwnd} win32={win32}");

        if (_hotkeyRegistered)
        {
            _hotkeyRetryTimer.Stop();
        }
        else if (!_hotkeyRetryTimer.IsEnabled)
        {
            _hotkeyRetryTimer.Start();
        }

        return _hotkeyRegistered;
    }

    private void CaptureClipboard()
    {
        try
        {
            ClipboardHistoryItem? item = null;
            var source = ForegroundSource();
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
                if (ClipboardPathText.TryParseExistingFilePaths(text, out var paths))
                {
                    item = new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Files,
                        FilePaths = paths,
                        Preview = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files",
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                    };
                }
                else if (TryNormalizeColorText(text, source.Name, out var colorHex))
                {
                    item = new ClipboardHistoryItem
                    {
                        Kind = ClipboardItemKind.Color,
                        Text = colorHex,
                        Preview = colorHex,
                        ContentHash = HashText(colorHex),
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                    };
                }
                else
                {
                    item = new ClipboardHistoryItem
                    {
                        Kind = IsLinkOrEmail(text) ? ClipboardItemKind.Link : ClipboardItemKind.Text,
                        Text = text,
                        Preview = ClipboardHistoryStore.PreviewText(text),
                        ContentHash = HashText(text),
                        SourceApplication = source.Name,
                        SourceApplicationPath = source.Path,
                    };
                }
            }

            if (item is null)
            {
                return;
            }

            var saved = _store.AddOrUpdate(item);
            ShellLog.Info($"clipboard captured id={saved.Id} kind={saved.Kind} source={saved.SourceApplication} preview={saved.Preview}");
            _allItems = _store.QueryItems();
            if (IsVisible)
            {
                RenderItems(reason: "clipboard-live");
            }
            else
            {
                _itemsDirtySinceRender = true;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "clipboard capture failed");
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
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                PasteSelected();
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

    private void ShowActionMenu(ClipboardHistoryItem item)
    {
        _menuItem = item;
        var actions = new List<MenuAction>
        {
            new("Paste", PasteSelected, true, shortcut: "Enter"),
            new("Copy", CopySelected, true, shortcut: "Ctrl+C"),
            new(item.IsPinned ? "Unpin" : "Pin", () => TogglePin(item), true, shortcut: "Ctrl+P"),
            new("Move Pin Up", () => MovePin(item, -1), CanMovePin(item, -1)),
            new("Move Pin Down", () => MovePin(item, 1), CanMovePin(item, 1)),
            MenuAction.Separator,
        };

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            actions.Add(new MenuAction("Edit Text", () => EditText(item), true, shortcut: "Ctrl+E"));
            actions.Add(new MenuAction("Append to Clipboard", () => AppendText(item)));
        }

        if (item.Kind == ClipboardItemKind.Link)
        {
            actions.Add(new MenuAction("Open", () => OpenItem(item), true, shortcut: "Ctrl+O"));
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
                    row.Background = (WpfBrush)FindResource("Surface3");
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
                row.MouseLeave += (_, _) => row.Background = WpfBrushes.Transparent;
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
                row.MouseEnter += (_, _) => row.Background = (WpfBrush)FindResource("Surface3");
                row.MouseLeave += (_, _) => row.Background = WpfBrushes.Transparent;
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
        OpenButton.Visibility = item.Kind is ClipboardItemKind.Link or ClipboardItemKind.Files or ClipboardItemKind.Image ? Visibility.Visible : Visibility.Collapsed;
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
            Foreground = (WpfBrush)FindResource("Muted"),
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
        SetClipboard(_selected);
        ShellLog.Info($"copy selected id={_selected.Id}");
    }

    private void PasteSelected()
    {
        if (_selected is null) return;
        SetClipboard(_selected);
        ConcealPalette("paste");
        if (_returnFocusHwnd != IntPtr.Zero)
        {
            SetForegroundWindow(_returnFocusHwnd);
        }

        Forms.SendKeys.SendWait("^v");
        ShellLog.Info($"paste selected id={_selected.Id}");
    }

    private static void SetClipboard(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            System.Windows.Clipboard.SetText(TextPayload(item));
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
        var editor = new TextEditWindow(TextPayload(item), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Line"))
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
                Process.Start(new ProcessStartInfo(TextPayload(item)) { UseShellExecute = true });
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
                (WpfBrush)FindResource("Selected"))
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
    private void OnOpenClick(object sender, RoutedEventArgs e) { if (_selected is not null) OpenItem(_selected); }
    private void OnCloseClick(object sender, RoutedEventArgs e) => ConcealPalette("close");
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => ConcealPalette("minimize");
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellLog.Info("settings opening");
            var settings = new SettingsWindow(_settings, ApplyTheme, ApplyAppIcon, RenderSvg("dropdown-arrow-svgrepo-com.svg", 24), (WpfBrush)FindResource("Bg"), (WpfBrush)FindResource("Surface"), (WpfBrush)FindResource("Surface2"), (WpfBrush)FindResource("Surface3"), (WpfBrush)FindResource("Text"), (WpfBrush)FindResource("Muted"), (WpfBrush)FindResource("Line"), (WpfBrush)FindResource("Selected"))
            {
                Owner = this,
            };
            _suppressDeactivate = true;
            settings.Closed += (_, _) =>
            {
                _suppressDeactivate = false;
                ShellLog.Info("settings closed");
                ShowPalette();
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

    private void ApplyAppIcon(AppIconPreference preference, bool save)
    {
        _settings.AppIcon = preference;
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

        ApplyWindowTitleIcon(useDark);
        SetBrush("Bg", useDark ? "#1A1816" : "#FAFAF8");
        SetBrush("Surface", useDark ? "#211F1C" : "#FFFFFF");
        SetBrush("Surface2", useDark ? "#26231F" : "#F4F3EF");
        SetBrush("Surface3", useDark ? "#2F2C27" : "#ECEBE6");
        SetBrush("Line", useDark ? "#322E29" : "#E6E4DD");
        SetBrush("Line2", useDark ? "#3D3934" : "#D8D5CC");
        SetBrush("Text", useDark ? "#F2EFE9" : "#1A1816");
        SetBrush("Muted", useDark ? "#8A8478" : "#7A756C");
        SetBrush("Muted2", useDark ? "#BDB6AB" : "#4A4641");
        SetBrush("Muted3", useDark ? "#5F5A52" : "#A8A299");
        SetBrush("Accent", useDark ? "#8FC8D9" : "#2B9AAD");
        SetBrush("AccentSoft", useDark ? "#263941" : "#E1F4F5");
        SetBrush("Selected", useDark ? "#27363B" : "#D8F1EF");
        SetBrush("SelectedBorder", useDark ? "#56828E" : "#7CCBD0");
        SetBrush("Danger", useDark ? "#D56B5D" : "#B94A3D");
        Background = (WpfBrush)FindResource("Bg");
        HtmlPreview.DefaultBackgroundColor = ToDrawingColor((SolidColorBrush)FindResource("Bg"));
        ShellLog.Info($"theme applied preference={preference} dark={useDark}");

        if (save)
        {
            _settings.Save();
            RenderItems("theme");
            if (_selected is not null)
            {
                RenderInfo(_selected);
                RenderPreview(_selected);
            }

            ShowToast($"Theme set to {ThemeLabel(preference)}");
        }
    }

    private void ApplyWindowTitleIcon(bool useDark)
    {
        var titleIcon = useDark ? AppIconPreference.Light : AppIconPreference.Dark;
        var iconPath = AppIconPath(titleIcon);
        if (File.Exists(iconPath))
        {
            var icon = LoadBitmap(iconPath);
            Icon = icon;
            AppHeaderIcon.Source = icon;
            ShellLog.Info($"window title icon applied themeDark={useDark} icon={titleIcon} path={iconPath}");
        }
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

    private static bool IsWindowsDarkMode()
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

    private static System.Drawing.Color ToDrawingColor(SolidColorBrush brush) =>
        System.Drawing.Color.FromArgb(brush.Color.A, brush.Color.R, brush.Color.G, brush.Color.B);

    private static string ThemeLabel(ClipThemePreference preference) => preference switch
    {
        ClipThemePreference.Light => "Light",
        ClipThemePreference.Dark => "Dark",
        _ => "System",
    };

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
        if (e.Key == Key.L && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            WriteDebugSnapshot("keyboard");
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            PasteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelected();
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_selected is not null)
            {
                TogglePin(_selected);
                ShellLog.Info($"hotkey pin id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
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
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_selected is not null && _selected.Kind is (ClipboardItemKind.Link or ClipboardItemKind.Files or ClipboardItemKind.Image))
            {
                OpenItem(_selected);
                ShellLog.Info($"hotkey open id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_selected is not null && _selected.Kind is (ClipboardItemKind.Text or ClipboardItemKind.Link))
            {
                EditText(_selected);
                ShellLog.Info($"hotkey edit id={_selected.Id}");
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_selected is not null)
            {
                DeleteItem(_selected);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
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

    private void ShowHotkeyHelp()
    {
        try
        {
            ShellLog.Info("hotkey help opening");
            var help = new HotkeyHelpWindow(
                (WpfBrush)FindResource("Bg"),
                (WpfBrush)FindResource("Surface"),
                (WpfBrush)FindResource("Surface2"),
                (WpfBrush)FindResource("Text"),
                (WpfBrush)FindResource("Muted"),
                (WpfBrush)FindResource("Line"))
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
        DateDropButton.Background = _kindFilter == "all" ? (WpfBrush)FindResource("Surface3") : WpfBrushes.Transparent;
        FileDropButton.Foreground = _kindFilter == "files" ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        FileDropButton.Background = _kindFilter == "files" ? (WpfBrush)FindResource("Surface3") : WpfBrushes.Transparent;
    }

    private void SetFilterVisual(WpfButton button, Border? shell, bool selected)
    {
        button.Foreground = selected ? (WpfBrush)FindResource("Text") : (WpfBrush)FindResource("Muted");
        button.Background = selected ? (WpfBrush)FindResource("Surface3") : WpfBrushes.Transparent;
        if (shell is not null)
        {
            shell.Background = selected ? (WpfBrush)FindResource("Surface3") : WpfBrushes.Transparent;
        }
    }

    private static string TitleFor(ClipboardHistoryItem item)
    {
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

        return item.SourceApplication ?? item.Kind.ToString();
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

        return source.Equals("olk", StringComparison.OrdinalIgnoreCase) ? "Outlook" : source;
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
    private static bool IsLinkOrEmail(string text) => Uri.TryCreate(text.Trim(), UriKind.Absolute, out _) || Regex.IsMatch(text.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
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

    private ImageSource RenderSvg(string fileName, int size, double scaleX = 1.0)
    {
        var renderWidth = Math.Max(1, (int)Math.Round(size * scaleX));
        using var bitmap = new System.Drawing.Bitmap(Math.Max(size, renderWidth), size);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        var svg = ThemeSvg(File.ReadAllText(AssetIconPath(fileName)));
        var document = SvgDocument.FromSvg<SvgDocument>(svg);
        document.Width = renderWidth;
        document.Height = size;
        using var rendered = document.Draw(renderWidth, size);
        graphics.DrawImage(rendered, (bitmap.Width - renderWidth) / 2, 0, renderWidth, size);
        return BitmapFromDrawingImage(bitmap);
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

    private static string ThemeSvg(string svg)
    {
        var color = "#F4EEE7";
        return Regex.Replace(svg, @"#[0-9a-fA-F]{3,8}|rgb\([^)]+\)|black|#000", color, RegexOptions.IgnoreCase);
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

    internal static Style ThinScrollBarStyle()
    {
        return (Style)XamlReader.Parse("""
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
                    <Border Background="#6B656B" CornerRadius="3" Margin="1"/>
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

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
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
    public HotkeyHelpWindow(WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush text, WpfBrush muted, WpfBrush line)
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
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var rows = new StackPanel { Margin = new Thickness(22, 18, 22, 22) };
        foreach (var (key, action) in new[]
        {
            ("Alt+V", "Open Clip"),
            ("Enter", "Paste selected item"),
            ("Ctrl+C", "Copy selected item"),
            ("Ctrl+P", "Pin or unpin selected item"),
            ("Ctrl+K", "Open actions"),
            ("Ctrl+O", "Open selected link, file, or image"),
            ("Ctrl+E", "Edit selected text"),
            ("Ctrl+Shift+L", "Save debug log snapshot"),
            ("Delete", "Delete selected item"),
            ("Esc", "Close Clip, close a document preview, or escape modals"),
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

    public OpenWithWindow(string targetPath, WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush surface3, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush selected)
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

        Title = "Open With";
        Width = 620;
        Height = 520;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = bg;
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), MainWindow.ThinScrollBarStyle());
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
        return new WpfButton
        {
            Content = label,
            Foreground = _muted,
            Background = WpfBrushes.Transparent,
            BorderBrush = _line,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
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

internal sealed class SettingsWindow : Window
{
    private readonly Grid _content = new();
    private readonly Dictionary<string, WpfButton> _nav = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipShellSettings _settings;
    private readonly Action<ClipThemePreference> _applyTheme;
    private readonly Action<AppIconPreference> _applyAppIcon;
    private readonly ImageSource _dropdownIcon;
    private readonly WpfBrush _bg;
    private readonly WpfBrush _surface;
    private readonly WpfBrush _surface2;
    private readonly WpfBrush _surface3;
    private readonly WpfBrush _text;
    private readonly WpfBrush _muted;
    private readonly WpfBrush _line;
    private readonly WpfBrush _selected;

    public SettingsWindow(ClipShellSettings settings, Action<ClipThemePreference> applyTheme, Action<AppIconPreference> applyAppIcon, ImageSource dropdownIcon, WpfBrush bg, WpfBrush surface, WpfBrush surface2, WpfBrush surface3, WpfBrush text, WpfBrush muted, WpfBrush line, WpfBrush selected)
    {
        _settings = settings;
        _applyTheme = applyTheme;
        _applyAppIcon = applyAppIcon;
        _dropdownIcon = dropdownIcon;
        _bg = bg;
        _surface = surface;
        _surface2 = surface2;
        _surface3 = surface3;
        _text = text;
        _muted = muted;
        _line = line;
        _selected = selected;

        Title = "Clip Settings";
        Width = 720;
        Height = 500;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = bg;
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
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
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
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
            Text = "Settings",
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
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        shell.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        shell.Children.Add(body);

        var sidebar = new StackPanel
        {
            Background = surface2,
            Margin = new Thickness(12),
        };
        foreach (var page in new[] { "General", "History", "Shortcuts", "Appearance" })
        {
            var button = NavButton(page);
            button.Click += (_, _) => ShowPage(page);
            _nav[page] = button;
            sidebar.Children.Add(button);
        }
        body.Children.Add(sidebar);

        _content.Background = surface;
        _content.Margin = new Thickness(0);
        Grid.SetColumn(_content, 1);
        body.Children.Add(_content);

        Content = root;
        ShowPage("General");
    }

    private WpfButton NavButton(string label)
    {
        return new WpfButton
        {
            Content = label,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Height = 36,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 0, 12, 0),
            Background = WpfBrushes.Transparent,
            Foreground = _muted,
            BorderThickness = new Thickness(0),
            FontSize = 13,
        };
    }

    private void ShowPage(string page)
    {
        foreach (var (name, button) in _nav)
        {
            var active = string.Equals(name, page, StringComparison.OrdinalIgnoreCase);
            button.Background = active ? _selected : WpfBrushes.Transparent;
            button.Foreground = active ? _text : _muted;
        }

        _content.Children.Clear();
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        panel.Children.Add(new TextBlock
        {
            Text = page,
            Foreground = _text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18),
        });

        if (string.Equals(page, "General", StringComparison.OrdinalIgnoreCase) || string.Equals(page, "Appearance", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(ThemeRow());
            panel.Children.Add(AppIconRow());
        }

        foreach (var row in RowsFor(page))
        {
            panel.Children.Add(Row(row.Label, row.Value));
        }

        _content.Children.Add(panel);
    }

    private IEnumerable<(string Label, string Value)> RowsFor(string page)
    {
        return page switch
        {
            "History" => new[]
            {
                ("Pinned items", "Kept until unpinned"),
                ("History limit", "Saved locally"),
                ("Duplicate handling", "Same content updates copy count"),
                ("Storage", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip")),
            },
            "Shortcuts" => new[]
            {
                ("Open Clip", "Alt+V"),
                ("Save debug log", "Ctrl+Shift+L"),
                ("Paste selected", "Enter"),
                ("Close", "Esc or click outside"),
            },
            "Appearance" => new[]
            {
                ("Density", "Compact"),
                ("Preview style", "Native when available"),
                ("Accent", "Teal"),
            },
            _ => new[]
            {
                ("Hotkey", "Alt+V"),
                ("Debug log", "Ctrl+Shift+L"),
                ("Dismiss", "Click outside or Esc"),
            },
        };
    }

    private Border ThemeRow()
    {
        return ControlRow(
            "Theme",
            "Choose System, Light, or Dark.",
            StyledDropdown(_settings.Theme.ToString(), new[] { "System", "Light", "Dark" }, selected =>
            {
                if (!Enum.TryParse<ClipThemePreference>(selected, out var theme) || theme == _settings.Theme)
                {
                    return;
                }

                _applyTheme(theme);
                ShellLog.Info($"settings theme changed theme={theme}");
            }));
    }

    private Border AppIconRow()
    {
        return ControlRow(
            "App icon",
            "Choose Light or Dark.",
            StyledDropdown(_settings.AppIcon.ToString(), new[] { "Light", "Dark" }, selected =>
            {
                if (!Enum.TryParse<AppIconPreference>(selected, out var icon) || icon == _settings.AppIcon)
                {
                    return;
                }

                _applyAppIcon(icon);
                ShellLog.Info($"settings app icon changed icon={icon}");
            }));
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
        };

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
                Background = string.Equals(item, selected, StringComparison.OrdinalIgnoreCase) ? _selected : WpfBrushes.Transparent,
            };
            row.Child = new TextBlock
            {
                Text = item,
                Foreground = string.Equals(item, selected, StringComparison.OrdinalIgnoreCase) ? _text : _muted,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
            };
            row.MouseEnter += (_, _) => row.Background = _surface3;
            row.MouseLeave += (_, _) => row.Background = string.Equals(item, label.Text, StringComparison.OrdinalIgnoreCase) ? _selected : WpfBrushes.Transparent;
            row.MouseLeftButtonDown += (_, e) =>
            {
                popup.IsOpen = false;
                label.Text = item;
                onSelected(item);
                foreach (Border optionRow in optionHost.Children)
                {
                    var isSelected = optionRow.Child is TextBlock text && string.Equals(text.Text, item, StringComparison.OrdinalIgnoreCase);
                    optionRow.Background = isSelected ? _selected : WpfBrushes.Transparent;
                    if (optionRow.Child is TextBlock optionText)
                    {
                        optionText.Foreground = isSelected ? _text : _muted;
                    }
                }

                e.Handled = true;
            };
            optionHost.Children.Add(row);
        }

        button.Click += (_, _) => popup.IsOpen = true;
        return button;
    }

    private Border ControlRow(string label, string hint, System.Windows.Controls.Control control)
    {
        var grid = new Grid { MinHeight = 58 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
            Margin = new Thickness(0, 3, 0, 0),
        });
        grid.Children.Add(textPanel);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

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

internal sealed class TextEditWindow : Window
{
    private readonly System.Windows.Controls.TextBox _box = new();
    public string Value => _box.Text;

    public TextEditWindow(string value, System.Windows.Media.Brush background, System.Windows.Media.Brush foreground, System.Windows.Media.Brush line)
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
        SourceInitialized += (_, _) => MainWindow.ApplyRoundedWindowCorners(new WindowInteropHelper(this).Handle);
        _box.Text = value;
        _box.TextWrapping = TextWrapping.Wrap;
        _box.AcceptsReturn = true;
        _box.FocusVisualStyle = null;
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _box.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _box.Margin = new Thickness(0);
        _box.Padding = new Thickness(14);
        _box.Background = WpfBrushes.Transparent;
        _box.Foreground = foreground;
        _box.BorderThickness = new Thickness(0);
        _box.FontFamily = new System.Windows.Media.FontFamily("JetBrains Mono, Cascadia Mono, Consolas");
        _box.FontSize = 13;
        _box.SelectionBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x50, 0x79, 0x88));

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
        var trim = ModalButton("Trim", foreground, line, false);
        trim.Margin = new Thickness(0, 0, 8, 0);
        trim.Click += (_, _) => _box.Text = _box.Text.Trim();
        Grid.SetColumn(trim, 1);
        header.Children.Add(trim);
        grid.Children.Add(header);

        var editorShell = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x23, 0x24)),
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(18, 0, 18, 0),
            Child = _box,
        };
        Grid.SetRow(editorShell, 1);
        grid.Children.Add(editorShell);

        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(18, 14, 18, 18) };
        var cancel = ModalButton("Cancel", foreground, line, false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = ModalButton("Save", foreground, line, true);
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
    }

    private static WpfButton ModalButton(string text, WpfBrush foreground, WpfBrush line, bool primary)
    {
        var button = new WpfButton
        {
            Content = text,
            Height = 32,
            MinWidth = primary ? 74 : 68,
            Padding = new Thickness(14, 0, 14, 0),
            Background = primary
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0x39, 0x41))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x23, 0x24)),
            BorderBrush = primary
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x56, 0x82, 0x8E))
                : line,
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            FontSize = 12,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
        };
        button.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="{x:Type Button}" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Border x:Name="Root" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True">
      <Setter TargetName="Root" Property="Opacity" Value="0.88"/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        return button;
    }
}
