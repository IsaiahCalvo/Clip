using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace Clip.Shell;

/// <summary>
/// The small always-on-top player Clip shows for picture-in-picture.
///
/// The browser's own picture-in-picture window is drawn by the browser: it carries a fixed set of
/// buttons Clip cannot change, including a Settings item that opens Edge and a "back to tab" that
/// goes nowhere useful. WebView2 also does not support the newer API that would let a page supply
/// its own window. Hosting the window here is the only way to get the same controls as the main
/// player, plus a real back and close.
/// </summary>
internal sealed class MediaPipWindow : Window
{
    private const string PipVirtualHost = "clip-pip.local";

    /// <summary>
    /// The visual-hosting browser control, not the ordinary one.
    ///
    /// The ordinary control puts the browser in a child window of its own, which Windows resizes on
    /// its own schedule rather than in step with the frame the user is dragging. The picture
    /// therefore arrives a moment after the border does, every frame of the drag, which is the
    /// stutter. This one hands its output to WPF as an image, so the frame and the picture are one
    /// thing and resize together. It captures from the browser rather than being composed by it, so
    /// it cannot show protected content — that only matters for streaming sites, and this window
    /// only ever plays a local file.
    /// </summary>
    private readonly Microsoft.Web.WebView2.Wpf.WebView2CompositionControl _view = new();
    private readonly string _filePath;
    private readonly bool _isVideo;
    private readonly double _startTime;
    private readonly Func<Task<CoreWebView2Environment>> _environment;

    /// <summary>Raised when the user asks to go back to Clip, carrying the playback position.</summary>
    public event Action<double>? BackRequested;

    public MediaPipWindow(
        string filePath,
        bool isVideo,
        double startTime,
        string backgroundHex,
        string textHex,
        Rect ownerWorkArea,
        Func<Task<CoreWebView2Environment>> environment)
    {
        _filePath = filePath;
        _isVideo = isVideo;
        _startTime = startTime;
        _environment = environment;
        _backgroundHex = backgroundHex;
        _textHex = textHex;

        Title = Path.GetFileName(filePath);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.Black;

        // The window is sized in whole screen pixels, but WPF lays its content out in scaled units,
        // so on a display at anything other than 100% every size lands on a fraction of a pixel.
        // Left alone, consecutive frames of a drag round that fraction different ways and the
        // picture twitches a pixel back and forth. Rounding the layout pins it.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // A chromeless resizable window still reserves a caption strip, which shows as a white bar
        // across the top. WindowChrome removes it, but WindowChrome redraws the frame itself and
        // stutters badly when the content is a hosted native window — which the browser control is
        // (dotnet/wpf#5892). Telling Windows the client area covers the whole window gets the same
        // bare frame with none of that, and keeps the resize edges, since the window still has a
        // real sizing border underneath.
        Width = isVideo ? 360 : 330;
        Height = isVideo ? 230 : 116;

        // Bottom-right of the screen Clip is on, not whichever screen Windows calls primary.
        Left = ownerWorkArea.Right - Width - 24;
        Top = ownerWorkArea.Bottom - Height - 24;

        Content = _view;

        Loaded += async (_, _) =>
        {
            HookAspectLock();
            await InitializeAsync();
        };
    }

    /// <summary>
    /// Constrains dragging from any edge or corner to the video's own aspect, so the picture is
    /// never letterboxed inside its own window.
    /// </summary>
    private void HookAspectLock()
    {
        var source = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook(AspectHook);
    }

    private const int WmSizing = 0x0214;
    private const int WmNcCalcSize = 0x0083;

    /// <summary>WVR_HREDRAW | WVR_VREDRAW: repaint the client area rather than stretching it.</summary>
    private const int WvrRedraw = 0x0300;

    private IntPtr AspectHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Accepting the proposed window rect unchanged as the client rect leaves no non-client
        // frame for Windows to draw, which is what removes the caption strip. Asking for a full
        // redraw with it stops Windows salvaging the previous frame's pixels by stretching them
        // into the new shape, which is what smears the picture while a drag is in progress.
        if (msg == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            handled = true;
            return new IntPtr(WvrRedraw);
        }

        if (msg != WmSizing || _aspect <= 0)
        {
            return IntPtr.Zero;
        }

        var dragged = Marshal.PtrToStructure<RECT>(lParam);
        var rect = ResizedBounds(wParam.ToInt32(), dragged, WorkAreaFor(hwnd), _aspect, MinTrackWidth);

        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return new IntPtr(1);
    }

    /// <summary>
    /// Turns the rectangle the user has dragged out into the one the window will actually take:
    /// the video's shape, anchored to the edge they are not dragging, and wholly on screen.
    /// </summary>
    internal static RECT ResizedBounds(int edge, RECT dragged, RECT workArea, double aspect, int minWidth)
    {
        var (width, height) = AspectCorrected(
            edge,
            dragged.Right - dragged.Left,
            dragged.Bottom - dragged.Top,
            aspect,
            minWidth);

        // The window can never be bigger than the screen it is on, or it could not sit inside it.
        var workWidth = workArea.Right - workArea.Left;
        var workHeight = workArea.Bottom - workArea.Top;

        if (width > workWidth)
        {
            width = workWidth;
            height = (int)Math.Round(width / aspect, MidpointRounding.AwayFromZero);
        }

        if (height > workHeight)
        {
            height = workHeight;
            width = (int)Math.Round(height * aspect, MidpointRounding.AwayFromZero);
        }

        // Grow away from whichever edge is anchored, so the opposite corner stays put.
        var left = edge is WmszLeft or WmszTopLeft or WmszBottomLeft
            ? dragged.Right - width
            : dragged.Left;

        var top = edge is WmszTop or WmszTopLeft or WmszTopRight
            ? dragged.Bottom - height
            : dragged.Top;

        // Dragging an outer edge of a window that is already against the side of the screen would
        // otherwise push it off the screen, since the anchored edge cannot move out of the way.
        // Sliding it back in means it grows towards the middle instead, and stays whole.
        left = Math.Clamp(left, workArea.Left, workArea.Right - width);
        top = Math.Clamp(top, workArea.Top, workArea.Bottom - height);

        return new RECT
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height,
        };
    }

    private const int MonitorDefaultToNearest = 2;

    private static RECT WorkAreaFor(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);

        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? info.rcWork
            : new RECT { Left = int.MinValue / 2, Top = int.MinValue / 2, Right = int.MaxValue / 2, Bottom = int.MaxValue / 2 };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    /// <summary>The smallest the window may get, in physical pixels, before aspect is applied.</summary>
    private int MinTrackWidth =>
        (int)Math.Round(200 * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX);

    /// <summary>
    /// Snaps a dragged size back onto the video's aspect.
    ///
    /// A side edge only moves in one direction, so that direction leads and the other follows. A
    /// corner moves in both at once, and letting either one lead means the window ignores half of
    /// the gesture and springs back to where the leading axis says it should be — a visible shudder
    /// under the pointer for the whole drag. Projecting the dragged box onto the line of
    /// correctly-shaped sizes takes both axes into account, so the window lands as close to the
    /// pointer as the aspect allows and tracks it smoothly.
    /// </summary>
    internal static (int Width, int Height) AspectCorrected(int edge, double width, double height, double aspect, int minWidth)
    {
        if (edge is WmszLeft or WmszRight)
        {
            height = width / aspect;
        }
        else if (edge is WmszTop or WmszBottom)
        {
            width = height * aspect;
        }
        else
        {
            // Closest point on h = w / aspect to the dragged (width, height).
            var t = (width * aspect + height) / (aspect * aspect + 1);
            width = t * aspect;
            height = t;
        }

        if (width < minWidth)
        {
            width = minWidth;
            height = width / aspect;
        }

        // Away-from-zero rather than the default to-even: a half pixel that rounds up or down
        // depending on which side of it you landed on makes the edge wobble by one during a drag.
        return (
            (int)Math.Round(width, MidpointRounding.AwayFromZero),
            (int)Math.Round(height, MidpointRounding.AwayFromZero));
    }


    private const int WmNcLButtonDown = 0x00A1;

    /// <summary>
    /// Hands the drag to Windows as if the user had grabbed the window's own resize border, which
    /// the browser control would otherwise cover.
    /// </summary>
    private void BeginResize(string edge)
    {
        var hit = edge switch
        {
            "left" => 10,
            "right" => 11,
            "top" => 12,
            "topleft" => 13,
            "topright" => 14,
            "bottom" => 15,
            "bottomleft" => 16,
            "bottomright" => 17,
            _ => 0,
        };

        if (hit == 0)
        {
            return;
        }

        BeginNonClientDrag(hit);
    }

    private const int HtCaption = 2;

    /// <summary>
    /// Starts a native move or resize loop. Works from a press that landed inside a child control,
    /// which is the whole window here.
    /// </summary>
    private void BeginNonClientDrag(int hitTest)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // WPF routes the mouse into the visual-hosting control itself, so it may be holding the
        // capture. Windows will not start a move or resize loop until both are let go.
        Mouse.Capture(null);
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, new IntPtr(hitTest), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly string _backgroundHex;
    private readonly string _textHex;

    private async Task InitializeAsync()
    {
        try
        {
            await _view.EnsureCoreWebView2Async(await _environment());
            _view.DefaultBackgroundColor = System.Drawing.Color.Black;
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _view.CoreWebView2.WebMessageReceived += OnWebMessage;

            var folder = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrWhiteSpace(folder))
            {
                Close();
                return;
            }

            _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
                PipVirtualHost,
                folder,
                CoreWebView2HostResourceAccessKind.Allow);

            var mediaUrl = $"https://{PipVirtualHost}/{Uri.EscapeDataString(Path.GetFileName(_filePath))}";
            _view.CoreWebView2.NavigateToString(MediaPreviewPage.Build(
                _filePath,
                mediaUrl,
                _isVideo,
                _backgroundHex,
                _textHex,
                detached: true,
                startTime: _startTime));
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "picture-in-picture window failed");
            Close();
        }
    }

    private double _aspect;

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var raw = e.TryGetWebMessageAsString();
        var ratio = MediaPlayerMessage.RatioOf(raw);
        if (ratio > 0)
        {
            _aspect = ratio;
            // Snap once so the starting window matches the video rather than a guessed box.
            Height = Math.Round(Width / _aspect);
            return;
        }

        var action = MediaPlayerMessage.ActionOf(raw);

        if (action.Name == "drag")
        {
            // Not DragMove: that requires WPF to believe the button is down, and the press
            // happened inside the browser control, so it throws. Hand it to Windows as a caption
            // drag instead — the same route the resize edges use.
            BeginNonClientDrag(HtCaption);
            return;
        }

        if (action.Name == "resize")
        {
            BeginResize(MediaPlayerMessage.EdgeOf(raw));
            return;
        }

        if (action.Name == "back")
        {
            BackRequested?.Invoke(action.Time);
            Close();
            return;
        }

        if (action.Name == "close")
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _view.Dispose();
        }
        catch
        {
            // Disposing a torn-down WebView2 can throw; nothing useful to do about it.
        }

        base.OnClosed(e);
    }
}

/// <summary>Parses the small JSON messages the player page posts to the host.</summary>
internal static class MediaPlayerMessage
{
    public static string EdgeOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("edge", out var e) ? e.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static double RatioOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("action", out var a) || a.GetString() != "ratio") return 0;
            if (!root.TryGetProperty("w", out var w) || !root.TryGetProperty("h", out var h)) return 0;
            var width = w.GetDouble();
            var height = h.GetDouble();
            return height > 0 ? width / height : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static (string Name, double Time) ActionOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (string.Empty, 0);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            var name = root.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
            var time = root.TryGetProperty("time", out var t) && t.TryGetDouble(out var value) ? value : 0;
            return (name, time);
        }
        catch
        {
            return (string.Empty, 0);
        }
    }
}
