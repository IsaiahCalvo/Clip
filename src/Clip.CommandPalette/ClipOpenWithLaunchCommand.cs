using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

// Launches a clipboard item's open-target with a specific discovered app (a row in
// ClipOpenWithPage). Reuses the shared Clip.Core launcher verbatim so behavior matches the
// standalone shell exactly: default app => shell-open, desktop app => .exe with the file
// argument, packaged/Store app => COM activation. On success the choice is recorded as Recent
// (per extension) so it surfaces first next time.
internal sealed partial class ClipOpenWithLaunchCommand : InvokableCommand
{
    private readonly ClipboardHistoryListItem _listItem;
    private readonly ClipboardHistoryStore _store;
    private readonly AppChoice _app;
    private readonly string _targetPath;

    public ClipOpenWithLaunchCommand(ClipboardHistoryListItem listItem, ClipboardHistoryStore store, AppChoice app, string targetPath)
    {
        _listItem = listItem;
        _store = store;
        _app = app;
        _targetPath = targetPath;
        Name = app.Name;
        Icon = new IconInfo(ClipOpenWithPage.IconForSource(app));
    }

    public override ICommandResult Invoke()
    {
        try
        {
            // Confirm the item still exists (it may have been deleted while the picker was open);
            // GetOpenTarget keeps the resolved path authoritative even if the asset moved.
            var fullItem = _store.GetItem(_listItem.Id);
            if (fullItem is null)
            {
                ShowError("Clipboard item was not found.");
                return CommandResult.KeepOpen();
            }

            var target = ClipboardItemLaunchCommand.GetOpenTarget(fullItem) ?? _targetPath;
            OpenWithAppLauncher.OpenWith(target, _app);

            OpenWithRecentStore.Save(target, _app);
            return CommandResult.Dismiss();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return CommandResult.KeepOpen();
        }
    }

    private static void ShowError(string message) =>
        new ToastStatusMessage(new StatusMessage
        {
            Message = message,
            State = MessageState.Error,
        }).Show();
}
