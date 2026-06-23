using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

/// <summary>
/// Top-level command that pastes the most recently copied item without opening the history
/// list — the palette equivalent of the standalone's tray "Paste latest item". It reuses the
/// same reliable paste path as the history rows (set clipboard + Dismiss + Watcher handoff).
/// </summary>
internal sealed partial class ClipPasteLatestCommand : InvokableCommand
{
    public ClipPasteLatestCommand()
    {
        Name = "Paste latest item";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            var store = ClipboardHistoryStore.OpenForCommandSurface();
            var latest = ClipboardHistoryListCommand.Create(store, query: null, limit: 1).Items.FirstOrDefault();
            if (latest is null)
            {
                Toast("Clipboard history is empty.", MessageState.Success);
                return CommandResult.KeepOpen();
            }

            var pasteAction = latest.Actions.FirstOrDefault(action =>
                action.Id.Equals("paste", StringComparison.OrdinalIgnoreCase));
            if (pasteAction is null)
            {
                Toast("The latest clipboard item cannot be pasted.", MessageState.Error);
                return CommandResult.KeepOpen();
            }

            // Delegate to the shared paste command: it sets the clipboard, dismisses the palette,
            // and hands the keystroke to the Watcher.
            return new ClipHistoryActionCommand(pasteAction, store).Invoke();
        }
        catch (Exception ex)
        {
            Toast(ex.Message, MessageState.Error);
            return CommandResult.KeepOpen();
        }
    }

    private static void Toast(string message, MessageState state) =>
        new ToastStatusMessage(new StatusMessage { Message = message, State = state }).Show();
}
