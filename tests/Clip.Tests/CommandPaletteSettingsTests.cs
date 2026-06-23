using System.Text.Json;
using Clip.Core;

namespace Clip.Tests;

public sealed class CommandPaletteSettingsTests
{
    [Fact]
    public void ConfigureClipHistoryHotkeyAddsAltVCommandHotkey()
    {
        var json = """{ "Hotkey": { "win": false, "ctrl": false, "alt": true, "shift": false, "code": 32 }, "CommandHotkeys": [] }""";

        var updated = CommandPaletteSettings.ConfigureClipHistoryHotkeyJson(json);

        using var document = JsonDocument.Parse(updated);
        var commandHotkey = Assert.Single(document.RootElement.GetProperty("CommandHotkeys").EnumerateArray());
        Assert.Equal(CommandPaletteSettings.ClipHistoryCommandId, commandHotkey.GetProperty("CommandId").GetString());
        var hotkey = commandHotkey.GetProperty("Hotkey");
        Assert.False(hotkey.GetProperty("win").GetBoolean());
        Assert.False(hotkey.GetProperty("ctrl").GetBoolean());
        Assert.True(hotkey.GetProperty("alt").GetBoolean());
        Assert.False(hotkey.GetProperty("shift").GetBoolean());
        Assert.Equal(0x56, hotkey.GetProperty("code").GetInt32());
    }

    [Fact]
    public void ConfigureClipHistoryHotkeyReplacesDuplicateCommandOrHotkeyBindings()
    {
        var json = """
        {
          "CommandHotkeys": [
            { "CommandId": "clip.history", "Hotkey": { "win": false, "ctrl": true, "alt": false, "shift": false, "code": 72 } },
            { "CommandId": "other.command", "Hotkey": { "win": false, "ctrl": false, "alt": true, "shift": false, "code": 86 } }
          ]
        }
        """;

        var updated = CommandPaletteSettings.ConfigureClipHistoryHotkeyJson(json);

        using var document = JsonDocument.Parse(updated);
        var commandHotkeys = document.RootElement.GetProperty("CommandHotkeys").EnumerateArray().ToArray();
        var commandHotkey = Assert.Single(commandHotkeys);
        Assert.Equal(CommandPaletteSettings.ClipHistoryCommandId, commandHotkey.GetProperty("CommandId").GetString());
    }

    [Fact]
    public void ConfigureClipHistoryHotkeyCanEnableExternalReloadForImmediateApply()
    {
        var updated = CommandPaletteSettings.ConfigureClipHistoryHotkeyJson("{}", enableExternalReloadForApply: true);

        using var document = JsonDocument.Parse(updated);
        Assert.True(document.RootElement.GetProperty("AllowExternalReload").GetBoolean());
    }
}
