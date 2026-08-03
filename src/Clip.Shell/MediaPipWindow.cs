using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
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
        Width = isVideo ? 480 : 420;
        Height = isVideo ? 300 : 140;

        // Bottom-right of the working area, the corner these windows conventionally sit in.
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 24;
        Top = work.Bottom - Height - 24;

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

        Loaded += async (_, _) => await InitializeAsync();
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

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var action = MediaPlayerMessage.ActionOf(e.TryGetWebMessageAsString());
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
