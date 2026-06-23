using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.ApplicationModel.DataTransfer;

namespace Clip.CommandPalette;

internal sealed partial class ClipCopyPathCommand : InvokableCommand
{
    private readonly IReadOnlyList<string> _paths;

    public ClipCopyPathCommand(IReadOnlyList<string> paths)
    {
        _paths = paths;
        Name = "Copy path";
        Icon = new IconInfo("\uE8C8");
    }

    public override ICommandResult Invoke()
    {
        if (_paths.Count == 0)
        {
            new ToastStatusMessage(new StatusMessage
            {
                Message = "This item has no paths to copy.",
                State = MessageState.Error,
            }).Show();
            return CommandResult.KeepOpen();
        }

        var data = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        data.SetText(string.Join(Environment.NewLine, _paths));
        Clipboard.SetContent(data);
        Clipboard.Flush();
        return CommandResult.ShowToast("Path copied.");
    }
}
