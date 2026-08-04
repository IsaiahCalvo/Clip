using System.Windows.Forms;
using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherHotkeyCoverageTests
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    [Fact]
    public void NullOrEmptyFallsBackToAltV()
    {
        foreach (var configured in new string?[] { null, "", "   " })
        {
            var hotkey = WatcherHotkey.OpenHotkey(configured);

            Assert.Equal("Alt+V", hotkey.DisplayText);
            Assert.Equal(ModAlt, hotkey.Modifiers);
            Assert.Equal((uint)'V', hotkey.VirtualKey);
        }
    }

    [Fact]
    public void ParsesMultipleModifiersAndLetterKey()
    {
        var hotkey = WatcherHotkey.OpenHotkey("Ctrl+Shift+P");

        Assert.Equal(ModControl | ModShift, hotkey.Modifiers);
        Assert.Equal((uint)'P', hotkey.VirtualKey);
        Assert.Equal("Ctrl+Shift+P", hotkey.DisplayText);
    }

    [Fact]
    public void ParsesDigitAndWinModifierAliases()
    {
        Assert.Equal((uint)'9', WatcherHotkey.OpenHotkey("Alt+9").VirtualKey);
        Assert.Equal(ModWin, WatcherHotkey.OpenHotkey("Win+2").Modifiers);
        Assert.Equal(ModWin, WatcherHotkey.OpenHotkey("windows+2").Modifiers);
        Assert.Equal(ModWin, WatcherHotkey.OpenHotkey("meta+2").Modifiers);
    }

    [Fact]
    public void ParsesFunctionKeysWithinRange()
    {
        Assert.Equal((uint)Keys.F5, WatcherHotkey.OpenHotkey("Ctrl+F5").VirtualKey);
        Assert.Equal((uint)Keys.F24, WatcherHotkey.OpenHotkey("Alt+F24").VirtualKey);
    }

    [Fact]
    public void OutOfRangeFunctionKeyFallsBack()
    {
        Assert.Equal("Alt+V", WatcherHotkey.OpenHotkey("Ctrl+F25").DisplayText);
    }

    [Fact]
    public void ParsesNamedKeysThroughKeysEnum()
    {
        var hotkey = WatcherHotkey.OpenHotkey("Win+Space");

        Assert.Equal(ModWin, hotkey.Modifiers);
        Assert.Equal((uint)Keys.Space, hotkey.VirtualKey);
    }

    [Fact]
    public void KeyWithoutModifierFallsBack()
    {
        Assert.Equal("Alt+V", WatcherHotkey.OpenHotkey("V").DisplayText);
    }

    [Fact]
    public void UnknownModifierFallsBack()
    {
        Assert.Equal("Alt+V", WatcherHotkey.OpenHotkey("Foo+X").DisplayText);
    }

    [Fact]
    public void KeysNoneIsRejected()
    {
        // Enum.TryParse accepts "None" but a zero virtual key is not a hotkey.
        Assert.Equal("Alt+V", WatcherHotkey.OpenHotkey("Ctrl+None").DisplayText);
    }

    [Fact]
    public void DisplayTextPreservesUserCasing()
    {
        var hotkey = WatcherHotkey.OpenHotkey("ctrl+alt+delete");

        Assert.Equal(ModControl | ModAlt, hotkey.Modifiers);
        Assert.Equal((uint)Keys.Delete, hotkey.VirtualKey);
        Assert.Equal("ctrl+alt+delete", hotkey.DisplayText);
    }
}
