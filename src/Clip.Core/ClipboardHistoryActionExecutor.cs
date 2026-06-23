namespace Clip.Core;

public static class ClipboardHistoryActionExecutor
{
    public static ClipboardHistoryActionExecutionResult Execute(ClipboardHistoryStore store, ClipboardHistoryListAction action)
    {
        if (action.Arguments.Count < 2)
        {
            return ClipboardHistoryActionExecutionResult.NotHandled;
        }

        var command = action.Arguments[0];
        var id = action.Arguments[1];
        var succeeded = command.ToLowerInvariant() switch
        {
            "pin" => store.SetPinned(id, true),
            "unpin" => store.SetPinned(id, false),
            "up" => store.MovePinned(id, -1),
            "down" => store.MovePinned(id, 1),
            "delete" => store.Delete(id),
            _ => (bool?)null,
        };

        return succeeded is null
            ? ClipboardHistoryActionExecutionResult.NotHandled
            : new ClipboardHistoryActionExecutionResult(
                Handled: true,
                Succeeded: succeeded.Value,
                MutatedHistory: succeeded.Value,
                Message: succeeded.Value ? null : "Clipboard item was not found or the action is unavailable.");
    }
}

public readonly record struct ClipboardHistoryActionExecutionResult(
    bool Handled,
    bool Succeeded,
    bool MutatedHistory,
    string? Message)
{
    public static ClipboardHistoryActionExecutionResult NotHandled => new(false, false, false, null);
}
