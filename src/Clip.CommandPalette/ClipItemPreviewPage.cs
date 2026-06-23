using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipItemPreviewPage : ContentPage
{
    private readonly ClipboardHistoryListItem _item;
    private readonly ClipboardHistoryStore _store;
    private readonly Action _afterHistoryMutation;

    public ClipItemPreviewPage(ClipboardHistoryListItem item, ClipboardHistoryStore store, Action afterHistoryMutation)
    {
        _item = item;
        _store = store;
        _afterHistoryMutation = afterHistoryMutation;
        Name = "Preview";
        Title = ClipText.TrimForDisplay(item.Title, 96);
        Icon = new IconInfo(IconFor(item.Kind));
        Commands = BuildCommands();
    }

    private IContextItem[] BuildCommands()
    {
        var commands = _item.Actions.Select(CreateContextCommand).ToList();

        // Offer the searchable "Open with…" picker for openable file-backed items, matching the
        // list row's MoreCommands. Target is derived from the lightweight list item (no store hit).
        if (_item.TryGetOpenWithTarget(out var openWithTarget))
        {
            commands.Add(new CommandContextItem(new ClipOpenWithPage(_item, _store, openWithTarget))
            {
                Title = "Open with…",
                Icon = new IconInfo("\uE8A7"),
            });
        }

        return commands.ToArray();
    }

    public override IContent[] GetContent()
    {
        var fullItem = _store.GetItem(_item.Id);
        if (fullItem is null)
        {
            return [ClipItemPreviewCard.Unavailable()];
        }

        var card = new ClipItemPreviewCard(fullItem, _item, _store, _afterHistoryMutation);
        if (ClipImagePreviewContent.TryCreate(fullItem, out var imagePreview))
        {
            return [imagePreview, card];
        }

        // For PDF/Office/Visio file items, render (and cache) a first-page thumbnail lazily here so
        // it appears above the text-excerpt card. The render is delegated to the Watcher helper and
        // never runs on cold-open or list render. The cached PNG is then reused by the list details
        // pane on subsequent selections.
        if (ClipImagePreviewContent.TryCreateDocumentThumbnail(fullItem, out var documentPreview))
        {
            return [documentPreview, card];
        }

        return [card];
    }

    private CommandContextItem CreateContextCommand(ClipboardHistoryListAction action)
    {
        ICommand command = action.Id switch
        {
            "rename" => ClipItemTextFormPage.Rename(_item, _store, _afterHistoryMutation),
            "edit-text" => ClipItemTextFormPage.EditText(_item, _store, _afterHistoryMutation),
            "save-as-file" => ClipItemTextFormPage.SaveAsFile(_item, _store),
            "copy-path" => new ClipCopyPathCommand(_item.FilePaths),
            _ => new ClipHistoryActionCommand(action, _store, _afterHistoryMutation),
        };

        return new CommandContextItem(command)
        {
            Title = action.Label,
            Icon = new IconInfo(IconForAction(action.Id)),
        };
    }

    private static string IconFor(string kind) => kind switch
    {
        "Text" => "\uE8D2",
        "Link" => "\uE71B",
        "Files" => "\uE8B7",
        "Image" => "\uEB9F",
        "Color" => "\uE790",
        _ => "\uE8C8",
    };

    private static string IconForAction(string actionId) => actionId switch
    {
        "paste" => "\uE77F",
        "paste-plain" => "\uE8A5",
        "append" => "\uE710",
        "share" => "\uE72D",
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
