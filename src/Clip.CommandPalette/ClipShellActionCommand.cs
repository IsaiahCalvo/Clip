using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipShellActionCommand : InvokableCommand
{
    private readonly string? _trayAction;
    private readonly bool _forcePaletteSession;

    private ClipShellActionCommand(string name, string icon, string? trayAction, bool forcePaletteSession = false)
    {
        Name = name;
        Icon = new IconInfo(icon);
        _trayAction = trayAction;
        _forcePaletteSession = forcePaletteSession;
    }

    public static ClipShellActionCommand OpenClip() => new("Open Clip.exe", "\uE8A7", null, forcePaletteSession: true);

    public static ClipShellActionCommand OpenSettings() => new("Open Clip.exe settings", "\uE713", "--tray-action=settings");

    public static ClipShellActionCommand CheckForUpdates() => new("Check for Clip.exe updates", "\uE895", "--tray-action=check-updates");

    public static ClipShellActionCommand SaveLogSnapshot() => new("Save log snapshot", "\uE9F9", "--tray-action=save-log");

    public override ICommandResult Invoke()
    {
        var executable = ClipExecutableLocator.Resolve("Clip.exe");
        if (executable is null)
        {
            ShowError("Clip.exe was not found.");
            return CommandResult.KeepOpen();
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            };

            if (_forcePaletteSession || _trayAction is not null)
            {
                startInfo.ArgumentList.Add("--palette-session");
            }

            if (_trayAction is not null)
            {
                startInfo.ArgumentList.Add(_trayAction);
            }

            Process.Start(startInfo);
            return CommandResult.Dismiss();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return CommandResult.KeepOpen();
        }
    }

    private static void ShowError(string message)
    {
        new ToastStatusMessage(new StatusMessage
        {
            Message = message,
            State = MessageState.Error,
        }).Show();
    }
}
