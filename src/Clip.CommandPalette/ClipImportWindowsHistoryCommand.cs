using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipImportWindowsHistoryCommand : InvokableCommand
{
    private readonly Action _afterImport;

    public ClipImportWindowsHistoryCommand(Action afterImport)
    {
        _afterImport = afterImport;
        Name = "Import Windows clipboard history";
        Icon = new IconInfo("\uE896");
    }

    public override ICommandResult Invoke()
    {
        // The Windows clipboard-history reader lives in the net8.0-windows WinRT helper
        // (Clip.WindowsHistory) that the extension does not reference, so importing in-process would
        // pull Windows-only deps the palette host cannot load. Delegate to Clip.Command.exe, which
        // already orchestrates ClipboardHistoryImportService against WindowsClipboardHistorySource.
        var executable = ClipExecutableLocator.Resolve("Clip.Command.exe");
        if (executable is null)
        {
            ShowError("Clip.Command.exe was not found.");
            return CommandResult.KeepOpen();
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                },
            };
            process.StartInfo.ArgumentList.Add("import-windows-history");

            if (!process.Start())
            {
                ShowError("Could not start Clip.Command.exe.");
                return CommandResult.KeepOpen();
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                ShowError(string.IsNullOrWhiteSpace(error)
                    ? "Windows clipboard-history import failed."
                    : error.Trim());
                return CommandResult.KeepOpen();
            }

            _afterImport();

            var imported = ParseImportedCount(output);
            new ToastStatusMessage(new StatusMessage
            {
                Message = imported switch
                {
                    null => "Imported Windows clipboard history.",
                    0 => "No new Windows clipboard items to import.",
                    1 => "Imported 1 Windows clipboard item.",
                    _ => $"Imported {imported} Windows clipboard items.",
                },
            }).Show();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return CommandResult.KeepOpen();
        }

        return CommandResult.KeepOpen();
    }

    private static int? ParseImportedCount(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(line, out var count))
            {
                return count;
            }
        }

        return null;
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
