using System.Diagnostics;
using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Clip.CommandPalette;

internal sealed partial class ClipHistoryActionCommand : InvokableCommand
{
    private readonly ClipboardHistoryListAction _action;
    private readonly ClipboardHistoryStore? _store;
    private readonly Action? _afterHistoryMutation;

    public ClipHistoryActionCommand(ClipboardHistoryListAction action, ClipboardHistoryStore? store = null, Action? afterHistoryMutation = null)
    {
        _action = action;
        _store = store;
        _afterHistoryMutation = afterHistoryMutation;
        Name = action.Label;
        Icon = new IconInfo(IconForAction(action.Id));
    }

    public override ICommandResult Invoke()
    {
        if (TryInvokeInProcess(out var result))
        {
            return result;
        }

        var executable = ClipExecutableLocator.Resolve(_action.Executable);
        if (executable is null)
        {
            ShowError("Clip is not installed or its helper command was not found.");
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

            foreach (var argument in _action.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
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

    private bool TryInvokeInProcess(out ICommandResult result)
    {
        result = CommandResult.KeepOpen();
        if (_store is null || _action.Arguments.Count < 2)
        {
            return false;
        }

        var command = _action.Arguments[0];
        try
        {
            var execution = ClipboardHistoryActionExecutor.Execute(_store, _action);
            if (execution.Handled)
            {
                if (!execution.Succeeded)
                {
                    ShowError(execution.Message ?? "Action failed.");
                    result = CommandResult.KeepOpen();
                    return true;
                }

                if (execution.MutatedHistory)
                {
                    _afterHistoryMutation?.Invoke();
                }

                result = CommandResult.KeepOpen();
                return true;
            }

            var isPaste = command.Equals("paste", StringComparison.OrdinalIgnoreCase);
            if (!isPaste &&
                !command.Equals("copy", StringComparison.OrdinalIgnoreCase) &&
                !command.Equals("open", StringComparison.OrdinalIgnoreCase) &&
                !command.Equals("reveal", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var item = _store.GetItem(_action.Arguments[1]);
            if (item is null)
            {
                ShowError("Clipboard item was not found.");
                return true;
            }

            if (isPaste)
            {
                result = InvokePaste(item);
                return true;
            }

            if (command.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                ClipClipboardWriter.SetItem(item, ClipSharedSettings.LoadDefaultPasteFormat());
                result = CommandResult.Dismiss();
                return true;
            }

            var startInfo = command.Equals("open", StringComparison.OrdinalIgnoreCase)
                ? ClipboardItemLaunchCommand.CreateOpenStartInfo(item, _action.Arguments.Count > 2 ? _action.Arguments[2] : null)
                : ClipboardItemLaunchCommand.CreateRevealStartInfo(item);

            if (startInfo is null)
            {
                ShowError("This item cannot be opened from Command Palette.");
                return true;
            }

            Process.Start(startInfo);
            result = CommandResult.Dismiss();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return true;
        }
    }

    private ICommandResult InvokePaste(Clip.Core.ClipboardHistoryItem item)
    {
        // Honor the user's format preference: 'paste' uses the default, 'paste-plain' forces plain text.
        var format = _action.Id.Equals("paste-plain", StringComparison.OrdinalIgnoreCase)
            ? PasteFormatPreference.PlainText
            : ClipSharedSettings.LoadDefaultPasteFormat();

        // Set the clipboard in-process (fast, well under 50ms) so the correct format is delivered.
        ClipClipboardWriter.SetItem(item, format);

        // Hand the actual focus-restore + Ctrl+V injection to the proven Watcher path.
        // It is fire-and-forget: the clipboard is already populated, so the Watcher only needs
        // to restore focus to the prior app and synthesize the keystroke.
        var executable = ClipExecutableLocator.Resolve(_action.Executable);
        if (executable is null)
        {
            ShowError("Clip helper (Clip.Watcher.exe) was not found, so the paste keystroke could not be sent.");
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

            startInfo.ArgumentList.Add("paste");
            startInfo.ArgumentList.Add(item.Id);

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return CommandResult.KeepOpen();
        }

        // Dismiss so the palette closes and the OS restores focus to the prior app.
        return CommandResult.Dismiss();
    }

    private static void ShowError(string message)
    {
        new ToastStatusMessage(new StatusMessage
        {
            Message = message,
            State = MessageState.Error,
        }).Show();
    }

    private static string IconForAction(string actionId) => actionId switch
    {
        "paste" => "\uE77F",
        "paste-plain" => "\uE77F",
        "copy" => "\uE8C8",
        "rename" => "\uE8AC",
        "edit-text" => "\uE70F",
        "pin" => "\uE718",
        "unpin" => "\uE77A",
        "move-pin-up" => "\uE70E",
        "move-pin-down" => "\uE70D",
        "delete" => "\uE74D",
        "save-as-file" => "\uE792",
        "copy-path" => "\uE8C8",
        "open" => "\uE8A7",
        "reveal" => "\uEC50",
        _ => "\uE8A7",
    };
}

internal static class ClipClipboardWriter
{
    public static void SetItem(Clip.Core.ClipboardHistoryItem item, PasteFormatPreference format = PasteFormatPreference.PlainText)
    {
        var data = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };

        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            var effectiveFormat = format == PasteFormatPreference.OriginalFormatting && ClipboardPasteData.HasOriginalFormatting(item)
                ? PasteFormatPreference.OriginalFormatting
                : PasteFormatPreference.PlainText;
            var payload = ClipboardPasteData.Create(item, effectiveFormat);
            data.SetText(payload.Text);
            if (!string.IsNullOrWhiteSpace(payload.Html))
            {
                data.SetHtmlFormat(payload.Html);
            }

            if (!string.IsNullOrWhiteSpace(payload.Rtf))
            {
                data.SetRtf(payload.Rtf);
            }
        }
        else if (item.Kind == ClipboardItemKind.Image && item.AssetPath is not null && File.Exists(item.AssetPath))
        {
            var file = StorageFile.GetFileFromPathAsync(item.AssetPath).AsTask().GetAwaiter().GetResult();
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        else if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count > 0)
        {
            var files = item.FilePaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                .Select(StorageItemForPath)
                .ToList();
            if (files.Count == 0)
            {
                throw new InvalidOperationException("No copied files or folders are still available.");
            }

            data.SetStorageItems(files);
        }
        else
        {
            throw new InvalidOperationException("This item cannot be copied from Command Palette.");
        }

        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    private static IStorageItem StorageItemForPath(string path)
    {
        return File.Exists(path)
            ? StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult()
            : StorageFolder.GetFolderFromPathAsync(path).AsTask().GetAwaiter().GetResult();
    }
}
