using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

public sealed partial class ClipCommandsProvider : CommandProvider
{
    private ICommandItem[]? _commands;

    public ClipCommandsProvider()
    {
        Id = "clip";
        DisplayName = "Clip";
        Icon = new IconInfo("\uE8C8");
    }

    public override ICommandItem[] TopLevelCommands()
    {
        if (_commands is not null)
        {
            return _commands;
        }

        var historyPage = new ClipHistoryPage();
        _commands =
        [
            new CommandItem(historyPage)
            {
                Title = CommandPaletteSettings.ClipHistoryTitle,
                Subtitle = "Search Clip clipboard history",
                MoreCommands = historyPage.CreateContextCommands(),
            },
        ];

        return _commands;
    }
}
