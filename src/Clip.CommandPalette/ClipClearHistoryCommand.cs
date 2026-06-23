using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipClearHistoryCommand : InvokableCommand
{
    private readonly Lazy<ClipboardHistoryStore> _store;
    private readonly bool _includePinned;
    private readonly Action _afterClear;

    public ClipClearHistoryCommand(Lazy<ClipboardHistoryStore> store, bool includePinned, Action afterClear)
    {
        _store = store;
        _includePinned = includePinned;
        _afterClear = afterClear;
        Name = includePinned ? "Clear all history" : "Clear unpinned history";
        Icon = new IconInfo("\uE74D");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            var includePinned = _includePinned;
            var removed = _store.Value.ClearHistory(includePinned);
            _afterClear();
            new ToastStatusMessage(new StatusMessage
            {
                Message = removed == 1 ? "Removed 1 clipboard item." : $"Removed {removed} clipboard items.",
            }).Show();
        }
        catch (Exception ex)
        {
            new ToastStatusMessage(new StatusMessage
            {
                Message = ex.Message,
                State = MessageState.Error,
            }).Show();
        }

        return CommandResult.KeepOpen();
    }
}
