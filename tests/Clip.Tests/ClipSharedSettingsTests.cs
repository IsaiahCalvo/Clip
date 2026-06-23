using System.Text.Json;
using Clip.Core;

namespace Clip.Tests;

public sealed class ClipSharedSettingsTests
{
    [Fact]
    public void SetOpenModeJsonPreservesOtherSettings()
    {
        var json = ClipSharedSettings.SetOpenModeJson("""{ "AppIcon": 1, "HistoryLimit": 25 }""", ClipSharedOpenMode.CommandPalette);
        using var document = JsonDocument.Parse(json);

        Assert.Equal((int)ClipSharedOpenMode.CommandPalette, document.RootElement.GetProperty("OpenMode").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("AppIcon").GetInt32());
        Assert.Equal(25, document.RootElement.GetProperty("HistoryLimit").GetInt32());
    }

    [Fact]
    public void LoadFromJsonReadsSharedSettings()
    {
        var settings = ClipSharedSettings.LoadFromJson("""{ "OpenMode": 1, "AppIcon": 1, "CheckForUpdatesOnStartup": false }""");

        Assert.Equal(ClipSharedOpenMode.CommandPalette, settings.OpenMode);
        Assert.Equal(ClipSharedAppIcon.Dark, settings.AppIcon);
        Assert.False(settings.CheckForUpdatesOnStartup);
    }

    [Fact]
    public void LoadFromJsonDefaultsPasteFormatToPlainTextWhenAbsent()
    {
        var settings = ClipSharedSettings.LoadFromJson("""{ "OpenMode": 1 }""");

        Assert.Equal(PasteFormatPreference.PlainText, settings.DefaultPasteFormat);
    }

    [Theory]
    [InlineData("""{ "DefaultPasteFormat": 1 }""")]
    [InlineData("""{ "DefaultPasteFormat": "OriginalFormatting" }""")]
    [InlineData("""{ "DefaultPasteFormat": "originalformatting" }""")]
    public void LoadFromJsonReadsOriginalFormattingFromNumericOrString(string json)
    {
        var settings = ClipSharedSettings.LoadFromJson(json);

        Assert.Equal(PasteFormatPreference.OriginalFormatting, settings.DefaultPasteFormat);
    }

    [Fact]
    public void LoadFromJsonReadsPlainTextFromStringName()
    {
        var settings = ClipSharedSettings.LoadFromJson("""{ "DefaultPasteFormat": "PlainText" }""");

        Assert.Equal(PasteFormatPreference.PlainText, settings.DefaultPasteFormat);
    }

    [Theory]
    [InlineData("""{ "DefaultPasteFormat": 99 }""")]
    [InlineData("""{ "DefaultPasteFormat": "nonsense" }""")]
    [InlineData("""{ "DefaultPasteFormat": true }""")]
    public void LoadFromJsonFallsBackToPlainTextOnInvalidPasteFormat(string json)
    {
        var settings = ClipSharedSettings.LoadFromJson(json);

        Assert.Equal(PasteFormatPreference.PlainText, settings.DefaultPasteFormat);
    }

    [Fact]
    public void LoadFromJsonReadsFoundationSettings()
    {
        var settings = ClipSharedSettings.LoadFromJson("""
            { "HistoryLimit": 250, "MaxItemSizeBytes": 1048576, "ClipboardFolderPath": "D:\\Clips" }
            """);

        Assert.Equal(250, settings.HistoryLimit);
        Assert.Equal(1048576L, settings.MaxItemSizeBytes);
        Assert.Equal("D:\\Clips", settings.ClipboardFolderPath);
    }

    [Fact]
    public void LoadFromJsonAppliesCanonicalDefaultsForFoundationSettings()
    {
        var settings = ClipSharedSettings.LoadFromJson("{}");

        Assert.Equal(ClipSharedSettings.DefaultHistoryLimit, settings.HistoryLimit);
        Assert.Equal(ClipSharedSettings.DefaultMaxItemSizeBytes, settings.MaxItemSizeBytes);
        Assert.Null(settings.ClipboardFolderPath);
        Assert.Equal(PasteFormatPreference.PlainText, settings.DefaultPasteFormat);
    }

    [Fact]
    public void LoadFromJsonTreatsExplicitNullLimitsAsDefaults()
    {
        var settings = ClipSharedSettings.LoadFromJson("""{ "HistoryLimit": null, "MaxItemSizeBytes": null }""");

        Assert.Equal(ClipSharedSettings.DefaultHistoryLimit, settings.HistoryLimit);
        Assert.Equal(ClipSharedSettings.DefaultMaxItemSizeBytes, settings.MaxItemSizeBytes);
    }
}
