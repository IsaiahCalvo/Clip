using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipSettingsPage : ContentPage
{
    private const string OpenModeKey = "openMode";
    private const string AppIconKey = "appIcon";
    private const string CheckUpdatesKey = "checkForUpdatesOnStartup";
    private const string PasteFormatKey = "defaultPasteFormat";
    private const string HistoryLimitKey = "historyLimit";
    private const string MaxItemSizeKey = "maxItemSizeMb";
    private const string DataFolderKey = "clipboardFolderPath";
    private const string StandaloneValue = "standalone";
    private const string CommandPaletteValue = "command-palette";
    private const string LightValue = "light";
    private const string DarkValue = "dark";
    private const string PlainValue = "plain";
    private const string OriginalValue = "original";
    private const long BytesPerMb = 1024L * 1024L;

    private readonly Settings _settings = new();

    public ClipSettingsPage()
    {
        var current = ClipSharedSettings.Load();
        Name = "Settings";
        Icon = new IconInfo("\uE713");
        Title = "Clip Settings";

        _settings.Add(new ChoiceSetSetting(
            "openMode",
            "Choose what Alt+V should open.",
            OpenModeValue(current.OpenMode),
            [
                new ChoiceSetSetting.Choice("Clip.exe", StandaloneValue),
                new ChoiceSetSetting.Choice("Command Palette", CommandPaletteValue),
            ])
        {
            Label = "Alt+V opens",
            Description = "Use the standalone app or the Command Palette extension.",
        });

        _settings.Add(new ChoiceSetSetting(
            "appIcon",
            "Choose the tray icon.",
            AppIconValue(current.AppIcon),
            [
                new ChoiceSetSetting.Choice("Light icon", LightValue),
                new ChoiceSetSetting.Choice("Dark icon", DarkValue),
            ])
        {
            Label = "Tray icon",
            Description = "Used by Clip.exe and the lightweight tray watcher.",
        });

        _settings.Add(new ToggleSetting("checkForUpdatesOnStartup", current.CheckForUpdatesOnStartup)
        {
            Label = "Check for updates on startup",
            Description = "Applies to Clip.exe.",
        });

        _settings.Add(new ChoiceSetSetting(
            PasteFormatKey,
            "How text is delivered when you paste or copy.",
            PasteFormatValue(current.DefaultPasteFormat),
            [
                new ChoiceSetSetting.Choice("Plain text", PlainValue),
                new ChoiceSetSetting.Choice("Keep original formatting", OriginalValue),
            ])
        {
            Label = "Default paste format",
            Description = "Keep formatting pastes HTML/RTF when an item has it; plain text strips formatting.",
        });

        _settings.Add(new ChoiceSetSetting(
            HistoryLimitKey,
            "How many clipboard items to keep.",
            HistoryLimitValue(current.HistoryLimit),
            [
                new ChoiceSetSetting.Choice("100 items", "100"),
                new ChoiceSetSetting.Choice("250 items", "250"),
                new ChoiceSetSetting.Choice("500 items", "500"),
                new ChoiceSetSetting.Choice("1,000 items", "1000"),
                new ChoiceSetSetting.Choice("2,000 items", "2000"),
                new ChoiceSetSetting.Choice("5,000 items", "5000"),
            ])
        {
            Label = "History limit",
            Description = "Older unpinned items are removed once this many are stored.",
        });

        _settings.Add(new ChoiceSetSetting(
            MaxItemSizeKey,
            "Largest clipboard item to capture.",
            MaxItemSizeValue(current.MaxItemSizeBytes),
            [
                new ChoiceSetSetting.Choice("5 MB", "5"),
                new ChoiceSetSetting.Choice("10 MB", "10"),
                new ChoiceSetSetting.Choice("25 MB", "25"),
                new ChoiceSetSetting.Choice("50 MB", "50"),
                new ChoiceSetSetting.Choice("100 MB", "100"),
            ])
        {
            Label = "Max item size",
            Description = "Items larger than this are not captured.",
        });

        _settings.Add(new TextSetting(DataFolderKey, current.ClipboardFolderPath ?? string.Empty)
        {
            Label = "Clipboard data folder",
            Description = "Where history is stored. Leave blank for the default. Takes effect after Clip restarts.",
        });

        _settings.SettingsChanged += OnSettingsChanged;
    }

    public override IContent[] GetContent() => _settings.ToContent();

    private void OnSettingsChanged(object sender, Settings args)
    {
        var openMode = _settings.GetSetting<string>(OpenModeKey) == CommandPaletteValue
            ? ClipSharedOpenMode.CommandPalette
            : ClipSharedOpenMode.Standalone;
        ClipSharedSettings.SetOpenMode(openMode);

        if (openMode == ClipSharedOpenMode.CommandPalette)
        {
            var result = CommandPaletteSettings.ConfigureClipHistoryHotkey(enableExternalReloadForApply: true);
            if (result.Available)
            {
                CommandPaletteSettings.RequestExternalReload();
            }
        }

        var appIcon = _settings.GetSetting<string>(AppIconKey) == DarkValue
            ? ClipSharedAppIcon.Dark
            : ClipSharedAppIcon.Light;
        ClipSharedSettings.SetAppIcon(appIcon);

        ClipSharedSettings.SetCheckForUpdatesOnStartup(_settings.GetSetting<bool>(CheckUpdatesKey));

        var pasteFormat = _settings.GetSetting<string>(PasteFormatKey) == OriginalValue
            ? PasteFormatPreference.OriginalFormatting
            : PasteFormatPreference.PlainText;
        ClipSharedSettings.SetDefaultPasteFormat(pasteFormat);

        if (int.TryParse(_settings.GetSetting<string>(HistoryLimitKey), out var historyLimit) && historyLimit > 0)
        {
            ClipSharedSettings.SetHistoryLimit(historyLimit);
        }

        if (int.TryParse(_settings.GetSetting<string>(MaxItemSizeKey), out var maxItemMb) && maxItemMb > 0)
        {
            ClipSharedSettings.SetMaxItemSizeBytes(maxItemMb * BytesPerMb);
        }

        var folder = _settings.GetSetting<string>(DataFolderKey);
        ClipSharedSettings.SetClipboardFolderPath(string.IsNullOrWhiteSpace(folder) ? null : folder.Trim());
    }

    private static string PasteFormatValue(PasteFormatPreference format) =>
        format == PasteFormatPreference.OriginalFormatting ? OriginalValue : PlainValue;

    private static string HistoryLimitValue(int? limit) =>
        (limit ?? ClipSharedSettings.DefaultHistoryLimit).ToString();

    private static string MaxItemSizeValue(long? bytes) =>
        ((bytes ?? ClipSharedSettings.DefaultMaxItemSizeBytes) / BytesPerMb).ToString();

    private static string OpenModeValue(ClipSharedOpenMode mode) =>
        mode == ClipSharedOpenMode.CommandPalette ? CommandPaletteValue : StandaloneValue;

    private static string AppIconValue(ClipSharedAppIcon icon) =>
        icon == ClipSharedAppIcon.Dark ? DarkValue : LightValue;
}
