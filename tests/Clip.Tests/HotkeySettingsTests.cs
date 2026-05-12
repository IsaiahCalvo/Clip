using System.Windows.Input;
using Clip.Shell;

namespace Clip.Tests;

public sealed class HotkeySettingsTests
{
    [Fact]
    public void HotkeySettingsUseClipDefaults()
    {
        var hotkeys = new ClipHotkeySettings();

        Assert.Equal("Alt+V", hotkeys.OpenClip);
        Assert.Equal("Enter", hotkeys.PasteSelected);
        Assert.Equal("Ctrl+C", hotkeys.CopySelected);
        Assert.Equal("Ctrl+P", hotkeys.PinSelected);
        Assert.Equal("Ctrl+K", hotkeys.OpenActions);
        Assert.Equal("Ctrl+O", hotkeys.OpenSelected);
        Assert.Equal("Ctrl+E", hotkeys.EditSelected);
        Assert.Equal("Ctrl+Shift+L", hotkeys.SaveDebugLog);
        Assert.Equal("Delete", hotkeys.DeleteSelected);
        Assert.Equal("Esc", hotkeys.CloseClip);
    }

    [Fact]
    public void ResetRestoresDefaultHotkeys()
    {
        var hotkeys = new ClipHotkeySettings
        {
            OpenClip = "Ctrl+Space",
            PasteSelected = "Ctrl+Enter",
            SaveDebugLog = "Ctrl+Alt+L",
        };

        hotkeys.ResetToDefaults();

        Assert.Equal("Alt+V", hotkeys.OpenClip);
        Assert.Equal("Enter", hotkeys.PasteSelected);
        Assert.Equal("Ctrl+Shift+L", hotkeys.SaveDebugLog);
    }

    [Fact]
    public void HotkeyGestureParsesWindowsAndWpfValues()
    {
        var parsed = ClipHotkeyGesture.TryParse("Ctrl+Shift+L", out var gesture);

        Assert.True(parsed);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, gesture.WpfModifiers);
        Assert.Equal(Key.L, gesture.WpfKey);
        Assert.Equal("Ctrl+Shift+L", gesture.DisplayText);
    }

    [Fact]
    public void HotkeyGestureAllowsSingleActionKeys()
    {
        var parsed = ClipHotkeyGesture.TryParse("Delete", out var gesture);

        Assert.True(parsed);
        Assert.Equal(Key.Delete, gesture.WpfKey);
        Assert.Equal("Delete", gesture.DisplayText);
    }

    [Fact]
    public void GlobalHotkeysRequireModifier()
    {
        Assert.True(ClipHotkeyGesture.TryParseGlobal("Alt+V", out _));
        Assert.False(ClipHotkeyGesture.TryParseGlobal("V", out _));
    }
}
