using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

// The project also has WinForms and GDI in scope for the tray icon, and they name several of these
// types too. Everything here means the WPF one.
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Path = System.IO.Path;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Clip.Shell;

/// <summary>
/// The picture-in-picture player, drawn by Clip rather than by a browser.
///
/// This began life as a web page in an embedded browser, because the preview pane already had one
/// for documents and video was added alongside. The browser earned its place there and none of it
/// here: its own player controls were replaced with hand-written ones anyway, so all it contributed
/// was a second process that repaints on its own schedule. Everything reported about this window —
/// the picture shuddering while it was dragged, the close button arriving late, the scrubber
/// snapping back — was that one fact in different clothes.
///
/// Drawn here, the frame, the picture and the controls are one visual tree laid out in a single
/// pass, so they cannot disagree about what size the window is. The lag is not reduced; there is
/// nowhere for it to occur. <see cref="MediaPipWindow"/> stays as a fallback for the rare file
/// Windows will not open.
/// </summary>
internal sealed class NativeMediaPipWindow : Window
{
    private readonly MediaElement _player = new();
    private readonly Slider _seek = new();
    private readonly TextBlock _time = new();
    private readonly Button _play = new();
    private readonly Grid _bar = new();
    private readonly Button _back = new();
    private readonly Button _close = new();
    private readonly Popup _menu = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(2200) };

    private readonly string _filePath;
    private readonly double _startTime;

    private bool _scrubbing;
    private bool _opened;
    private double _aspect;

    /// <summary>Raised when the user asks to go back to Clip, carrying the playback position.</summary>
    public event Action<double>? BackRequested;

    /// <summary>Raised if Windows cannot play the file, so the browser player can take over.</summary>
    public event Action<double>? PlaybackUnavailable;

    public NativeMediaPipWindow(string filePath, double startTime, Rect ownerWorkArea)
    {
        _filePath = filePath;
        _startTime = startTime;

        Title = Path.GetFileName(filePath);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Width = 360;
        Height = 203;
        Left = ownerWorkArea.Right - Width - 24;
        Top = ownerWorkArea.Bottom - Height - 24;

        Content = BuildLayout();
        Loaded += OnLoaded;
    }

    private Grid BuildLayout()
    {
        _player.LoadedBehavior = MediaState.Manual;
        _player.UnloadedBehavior = MediaState.Manual;
        _player.ScrubbingEnabled = true;
        // The window is locked to the video's shape, so filling it is exactly fitting it — and it
        // spares the layout the letterbox arithmetic on every frame of a drag.
        _player.Stretch = Stretch.Fill;
        _player.Source = new Uri(_filePath);
        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += (_, _) => { _player.Position = TimeSpan.Zero; _player.Pause(); ShowPlaying(false); };
        _player.MediaFailed += (_, _) => PlaybackUnavailable?.Invoke(_player.Position.TotalSeconds);

        var root = new Grid();
        root.Children.Add(_player);
        root.Children.Add(BuildCorner(_back, "↖", HorizontalAlignment.Left, "Back to Clip"));
        root.Children.Add(BuildCorner(_close, "✕", HorizontalAlignment.Right, "Close"));
        root.Children.Add(BuildBar());

        root.MouseLeftButtonDown += OnPicturePressed;
        root.MouseMove += (_, _) => Wake();
        root.MouseLeave += (_, _) => { StopIdleCountdown(); ShowChrome(false); };

        return root;
    }

    private Border BuildCorner(Button button, string glyph, HorizontalAlignment side, string tip)
    {
        Style(button, glyph, 13);
        button.ToolTip = tip;
        button.Click += (_, _) =>
        {
            if (ReferenceEquals(button, _back))
            {
                BackRequested?.Invoke(_player.Position.TotalSeconds);
            }

            Close();
        };

        return new Border
        {
            Child = button,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
        };
    }

    private Grid BuildBar()
    {
        Style(_play, "▶", 13);
        _play.Click += (_, _) => TogglePlayback();

        _time.Foreground = Brushes.White;
        _time.FontSize = 11;
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.Margin = new Thickness(8, 0, 8, 0);
        _time.Text = "0:00 / 0:00";

        _seek.Minimum = 0;
        _seek.Maximum = 1;
        _seek.VerticalAlignment = VerticalAlignment.Center;
        _seek.Focusable = false;
        // A slider will not shrink past its contents unless told it may, which is what left the
        // scrubber stuck at one width and shoving the menu button off the end of a small window.
        _seek.MinWidth = 0;
        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _scrubbing = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _scrubbing = false;
            SeekToSlider();
        }));
        _seek.ValueChanged += (_, _) => { if (_scrubbing) { SeekToSlider(); } };

        var more = new Button();
        Style(more, "⋮", 13);
        more.ToolTip = "More";
        more.Click += (_, _) => _menu.IsOpen = !_menu.IsOpen;

        _menu.PlacementTarget = more;
        _menu.Placement = PlacementMode.Top;
        _menu.StaysOpen = false;
        _menu.Child = BuildSpeedMenu();

        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bar.VerticalAlignment = VerticalAlignment.Bottom;
        _bar.Height = 34;
        _bar.Background = new LinearGradientBrush(
            Color.FromArgb(210, 0, 0, 0),
            Color.FromArgb(0, 0, 0, 0),
            new Point(0, 1),
            new Point(0, 0));

        Grid.SetColumn(_play, 0);
        Grid.SetColumn(_time, 1);
        Grid.SetColumn(_seek, 2);
        Grid.SetColumn(more, 3);

        _bar.Children.Add(_play);
        _bar.Children.Add(_time);
        _bar.Children.Add(_seek);
        _bar.Children.Add(more);
        _bar.Children.Add(_menu);

        // The bar is a control, not part of the picture: pressing it must not start playback.
        _bar.MouseLeftButtonDown += (_, e) => e.Handled = true;

        return _bar;
    }

    private StackPanel BuildSpeedMenu()
    {
        var panel = new StackPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            Margin = new Thickness(0, 0, 0, 6),
        };

        foreach (var speed in new[] { 0.25, 0.5, 1, 1.25, 1.5, 2 })
        {
            var item = new Button
            {
                Content = speed == 1 ? "Normal" : $"{speed}×",
                Foreground = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 11,
            };

            item.Click += (_, _) =>
            {
                _player.SpeedRatio = speed;
                _menu.IsOpen = false;
            };

            panel.Children.Add(item);
        }

        return panel;
    }

    private static void Style(Button button, string glyph, double size)
    {
        button.Content = glyph;
        button.FontSize = size;
        button.Width = 26;
        button.Height = 26;
        button.Foreground = Brushes.White;
        button.Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
        button.BorderThickness = new Thickness(1);
        button.Focusable = false;
        button.Margin = new Thickness(4, 0, 4, 0);
        button.Cursor = Cursors.Hand;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var source = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(FrameHook);

        _player.Play();
        if (_startTime > 0)
        {
            _player.Position = TimeSpan.FromSeconds(_startTime);
        }

        ShowPlaying(true);
        _ticker.Tick += (_, _) => UpdateProgress();
        _ticker.Start();
        _idle.Tick += (_, _) => { StopIdleCountdown(); if (!_menu.IsOpen) { ShowChrome(false); } };
        ShowChrome(false);
    }

    private void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        _opened = true;

        if (_player.NaturalVideoWidth > 0 && _player.NaturalVideoHeight > 0)
        {
            _aspect = (double)_player.NaturalVideoWidth / _player.NaturalVideoHeight;
            Height = Math.Round(Width / _aspect);
        }

        UpdateProgress();
    }

    private void TogglePlayback()
    {
        if (_player.CanPause && IsPlaying)
        {
            _player.Pause();
            ShowPlaying(false);
            return;
        }

        _player.Play();
        ShowPlaying(true);
    }

    private bool IsPlaying { get; set; }

    private void ShowPlaying(bool playing)
    {
        IsPlaying = playing;
        _play.Content = playing ? "❚❚" : "▶";
    }

    private void UpdateProgress()
    {
        if (!_opened || !_player.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var total = _player.NaturalDuration.TimeSpan;
        _time.Text = $"{Clock(_player.Position)} / {Clock(total)}";

        if (!_scrubbing && total.TotalSeconds > 0)
        {
            _seek.ValueChanged -= OnSeekChanged;
            _seek.Value = _player.Position.TotalSeconds / total.TotalSeconds;
            _seek.ValueChanged += OnSeekChanged;
        }
    }

    private void OnSeekChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_scrubbing)
        {
            SeekToSlider();
        }
    }

    private void SeekToSlider()
    {
        if (_player.NaturalDuration.HasTimeSpan)
        {
            _player.Position = TimeSpan.FromSeconds(
                _seek.Value * _player.NaturalDuration.TimeSpan.TotalSeconds);
        }
    }

    private static string Clock(TimeSpan span) => $"{(int)span.TotalMinutes}:{span.Seconds:00}";

    /// <summary>
    /// A press on the picture either moves the window or toggles playback. Which one it was is only
    /// known once the pointer either moves or does not, so the drag decides it.
    /// </summary>
    private void OnPicturePressed(object? sender, MouseButtonEventArgs e)
    {
        var start = e.GetPosition(this);
        var moved = false;

        void Track(object? _, MouseEventArgs move)
        {
            var now = move.GetPosition(this);
            if (Math.Abs(now.X - start.X) < 3 && Math.Abs(now.Y - start.Y) < 3)
            {
                return;
            }

            moved = true;
            Detach();
            DragMove();
        }

        void Release(object? _, MouseButtonEventArgs up)
        {
            Detach();
            if (!moved)
            {
                TogglePlayback();
            }
        }

        void Detach()
        {
            MouseMove -= Track;
            MouseLeftButtonUp -= Release;
        }

        MouseMove += Track;
        MouseLeftButtonUp += Release;
    }

    private void Wake()
    {
        ShowChrome(true);
        StopIdleCountdown();
        _idle.Start();
    }

    private void StopIdleCountdown() => _idle.Stop();

    private void ShowChrome(bool visible)
    {
        var state = visible ? Visibility.Visible : Visibility.Hidden;
        _bar.Visibility = state;
        _back.Visibility = state;
        _close.Visibility = state;

        if (!visible)
        {
            _menu.IsOpen = false;
        }
    }

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmSizing = 0x0214;
    private const int GripPx = 7;

    private IntPtr FrameHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // Taking the whole window as the client area leaves Windows no frame to draw, which is
            // what removes the caption strip a borderless resizable window otherwise keeps.
            case WmNcCalcSize when wParam != IntPtr.Zero:
                handled = true;
                return IntPtr.Zero;

            // Nothing is left for Windows to hit-test as a border, so the edges are named here.
            // Doing it this way is what gives the double-headed arrows for free.
            case WmNcHitTest:
                handled = true;
                return new IntPtr(EdgeAt(hwnd, lParam));

            case WmSizing when _aspect > 0:
                var dragged = Marshal.PtrToStructure<MediaPipWindow.RECT>(lParam);
                Marshal.StructureToPtr(
                    MediaPipWindow.ResizedBounds(wParam.ToInt32(), dragged, WorkArea(hwnd), _aspect, MinWidthPx),
                    lParam,
                    false);
                handled = true;
                return new IntPtr(1);

            default:
                return IntPtr.Zero;
        }
    }

    private int MinWidthPx => (int)Math.Round(200 * VisualTreeHelper.GetDpi(this).DpiScaleX);

    private int EdgeAt(IntPtr hwnd, IntPtr lParam)
    {
        GetWindowRect(hwnd, out var window);

        var x = unchecked((short)((long)lParam & 0xFFFF));
        var y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        var grip = (int)Math.Round(GripPx * VisualTreeHelper.GetDpi(this).DpiScaleX);

        var left = x <= window.Left + grip;
        var right = x >= window.Right - grip;
        var top = y <= window.Top + grip;
        var bottom = y >= window.Bottom - grip;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => HtTopLeft,
            (_, true, true, _) => HtTopRight,
            (true, _, _, true) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtLeft,
            (_, true, _, _) => HtRight,
            (_, _, true, _) => HtTop,
            (_, _, _, true) => HtBottom,
            _ => HtClient,
        };
    }

    private static MediaPipWindow.RECT WorkArea(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);

        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? info.rcWork
            : new MediaPipWindow.RECT { Left = -32000, Top = -32000, Right = 32000, Bottom = 32000 };
    }

    protected override void OnClosed(EventArgs e)
    {
        _ticker.Stop();
        _idle.Stop();
        _player.Close();
        base.OnClosed(e);
    }

    private const int HtClient = 1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public MediaPipWindow.RECT rcMonitor;
        public MediaPipWindow.RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out MediaPipWindow.RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
