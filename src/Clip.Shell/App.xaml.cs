using System.IO;
using System.Windows;

namespace Clip.Shell;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _updateMenuItem;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShellLog.Info("app startup");

        _singleInstanceMutex = new Mutex(true, @"Global\ClipShellSingleInstance", out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            ShellLog.Info("another Clip shell instance is already running; exiting duplicate");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            ShellLog.Error(ex.Exception, "dispatcher unhandled exception");
            ex.Handled = true;
            _window?.WriteDebugSnapshot("dispatcher-exception");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exception)
            {
                ShellLog.Error(exception, "appdomain unhandled exception");
            }
            else
            {
                ShellLog.Info($"appdomain unhandled exception object={ex.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ShellLog.Error(ex.Exception, "task unobserved exception");
            ex.SetObserved();
        };
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _window = new MainWindow();
        var debugVisible = e.Args.Any(a => string.Equals(a, "--debug-visible", StringComparison.OrdinalIgnoreCase));
        _window.KeepOpenForDebug = debugVisible;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Clip",
            Icon = LoadTrayIcon(_window.AppIconPreference),
            Visible = true,
        };
        _window.AppIconChanged += preference => _tray.Icon = LoadTrayIcon(preference);
        _tray.DoubleClick += (_, _) => _window.ShowPalette();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Clip", null, (_, _) => _window.ShowPalette());
        menu.Items.Add("Paste latest item", null, (_, _) => _window.PasteLatestFromTray());
        _updateMenuItem = new System.Windows.Forms.ToolStripMenuItem("Check for updates");
        _updateMenuItem.Click += (_, _) =>
        {
            if (_window.LastUpdateStatus.State == "Update available" && !string.IsNullOrWhiteSpace(_window.LastUpdateStatus.DownloadUrl))
            {
                _window.InstallKnownUpdateFromTray();
            }
            else
            {
                _window.CheckForUpdatesFromTray();
            }
        };
        menu.Opening += (_, _) => UpdateTrayUpdateItem();
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add("Save log snapshot", null, (_, _) => _window.WriteDebugSnapshot("tray"));
        menu.Items.Add("Settings", null, (_, _) => _window.OpenSettingsFromTray());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;

        _window.InitializeShell();
        if (debugVisible)
        {
            Dispatcher.BeginInvoke(() => _window.ShowPalette());
        }
        ShellLog.Info("app startup complete");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShellLog.Info("app exit");
        _tray?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static System.Drawing.Icon LoadTrayIcon(AppIconPreference preference)
    {
        try
        {
            var path = global::Clip.Shell.MainWindow.AppIconPath(preference);
            if (File.Exists(path))
            {
                return new System.Drawing.Icon(path);
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "tray icon load failed");
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void UpdateTrayUpdateItem()
    {
        if (_window is null || _updateMenuItem is null)
        {
            return;
        }

        var status = _window.LastUpdateStatus;
        if (status.State == "Update available" && !string.IsNullOrWhiteSpace(status.DownloadUrl))
        {
            _updateMenuItem.Text = $"Install latest update ({status.LatestVersion ?? "new version"} available)";
        }
        else
        {
            _updateMenuItem.Text = "Check for updates";
        }
    }
}
