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

    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _view = new();
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

        // A chromeless resizable window still reserves a caption strip, which showed as a white
        // bar across the top. Zeroing the chrome removes it while keeping the resize grips.
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
        });

        Width = isVideo ? 360 : 330;
        Height = isVideo ? 230 : 116;

        // Bottom-right of the screen Clip is on, not whichever screen Windows calls primary.
        Left = ownerWorkArea.Right - Width - 24;
        Top = ownerWorkArea.Bottom - Height - 24;

        Content = _view;

        // Dragging anywhere that is not a control moves the window, as a chromeless window has no
        // title bar of its own.
        _view.PreviewMouseLeftButtonDown += (_, _) => { };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

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

    private IntPtr AspectHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmSizing || _aspect <= 0)
        {
            return IntPtr.Zero;
        }

        var rect = Marshal.PtrToStructure<RECT>(lParam);
        var edge = wParam.ToInt32();
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        // Left/right drags drive height; top/bottom drags drive width; corners follow width.
        if (edge is WmszLeft or WmszRight)
        {
            height = (int)Math.Round(width / _aspect);
        }
        else if (edge is WmszTop or WmszBottom)
        {
            width = (int)Math.Round(height * _aspect);
        }
        else
        {
            height = (int)Math.Round(width / _aspect);
        }

        // Grow away from whichever edge is anchored, so the opposite corner stays put.
        if (edge is WmszLeft or WmszTopLeft or WmszBottomLeft)
        {
            rect.Left = rect.Right - width;
        }
        else
        {
            rect.Right = rect.Left + width;
        }

        if (edge is WmszTop or WmszTopLeft or WmszTopRight)
        {
            rect.Top = rect.Bottom - height;
        }
        else
        {
            rect.Bottom = rect.Top + height;
        }

        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return new IntPtr(1);
    }

    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
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
