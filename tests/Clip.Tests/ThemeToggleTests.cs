using Clip.Shell;

namespace Clip.Tests;

public sealed class ThemeToggleTests
{
    [Theory]
    [InlineData(ClipThemePreference.Light, false, ClipThemePreference.Dark)]
    [InlineData(ClipThemePreference.Dark, true, ClipThemePreference.Light)]
    [InlineData(ClipThemePreference.System, false, ClipThemePreference.Dark)]
    [InlineData(ClipThemePreference.System, true, ClipThemePreference.Light)]
    internal void NextThemeTogglePreferenceSwitchesBetweenLightAndDark(ClipThemePreference current, bool systemIsDark, ClipThemePreference expected)
    {
        Assert.Equal(expected, MainWindow.NextThemeTogglePreference(current, systemIsDark));
    }
}
