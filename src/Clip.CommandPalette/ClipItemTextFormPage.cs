using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipItemTextFormPage : ContentPage
{
    private const string ValueKey = "value";

    private readonly Settings _settings = new();
    private readonly Func<string, ICommandResult> _submit;
    private bool _committing;

    private ClipItemTextFormPage(
        string title,
        string label,
        string description,
        string initialValue,
        bool multiline,
        Func<string, ICommandResult> submit)
    {
        Name = title;
        Title = title;
        Icon = new IconInfo("\uE70F");
        _submit = submit;

        _settings.Add(new TextSetting(ValueKey, initialValue)
        {
            Label = label,
            Description = description,
            Placeholder = description,
            Multiline = multiline,
        });
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public static ClipItemTextFormPage Rename(ClipboardHistoryListItem item, ClipboardHistoryStore store, Action afterSave) =>
        new(
            title: "Rename",
            label: "Name",
            description: "Set the display name for this item.",
            initialValue: item.Title,
            multiline: false,
            submit: value =>
            {
                if (!store.Rename(item.Id, value))
                {
                    return CommandResult.ShowToast("Clipboard item was not found.");
                }

                afterSave();
                return CommandResult.GoBack();
            });

    public static ClipItemTextFormPage EditText(ClipboardHistoryListItem item, ClipboardHistoryStore store, Action afterSave)
    {
        var fullItem = store.GetItem(item.Id);
        return new(
            title: "Edit Text",
            label: "Text",
            description: "Replace this text clipboard item.",
            initialValue: fullItem?.Text ?? item.Preview,
            multiline: true,
            submit: value =>
            {
                if (!store.EditText(item.Id, value))
                {
                    return CommandResult.ShowToast("Clipboard item was not found or is not editable text.");
                }

                afterSave();
                return CommandResult.GoBack();
            });
    }

    public static ClipItemTextFormPage SaveAsFile(ClipboardHistoryListItem item, ClipboardHistoryStore store) =>
        new(
            title: "Save as File",
            label: "File path",
            description: "Leave blank to save to the desktop with Clip's default file name.",
            initialValue: string.Empty,
            multiline: false,
            submit: value =>
            {
                try
                {
                    var path = store.SaveAsFile(item.Id, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
                    return CommandResult.ShowToast($"Saved to {path}");
                }
                catch (Exception ex)
                {
                    return CommandResult.ShowToast(ex.Message);
                }
            });

    public override IContent[] GetContent() => _settings.ToContent();

    private void OnSettingsChanged(object sender, Settings args)
    {
        if (_committing)
        {
            return;
        }

        _committing = true;
        try
        {
            _ = _submit(_settings.GetSetting<string>(ValueKey) ?? string.Empty);
        }
        finally
        {
            _committing = false;
        }
    }
}
