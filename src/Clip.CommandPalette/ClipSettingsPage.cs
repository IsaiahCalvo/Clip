using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipSettingsPage : ContentPage
{
    private const string OpenModeKey = "openMode";
    private const string AppIconKey = "appIcon";
    private const string CheckUpdatesKey = "checkForUpdatesOnStartup";
    private const string StandaloneValue = "standalone";
    private const string CommandPaletteValue = "command-palette";
    private const string LightValue = "light";
    private const string DarkValue = "dark";

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
    }

    private static string OpenModeValue(ClipSharedOpenMode mode) =>
        mode == ClipSharedOpenMode.CommandPalette ? CommandPaletteValue : StandaloneValue;

    private static string AppIconValue(ClipSharedAppIcon icon) =>
        icon == ClipSharedAppIcon.Dark ? DarkValue : LightValue;
}
