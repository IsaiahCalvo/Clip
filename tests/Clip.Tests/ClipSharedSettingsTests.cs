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

    [Fact]
    public void SetDefaultPasteFormatJsonRoundTrips()
    {
        var json = ClipSharedSettings.SetDefaultPasteFormatJson("{}", PasteFormatPreference.OriginalFormatting);

        Assert.Equal(PasteFormatPreference.OriginalFormatting, ClipSharedSettings.LoadFromJson(json).DefaultPasteFormat);
    }

    [Fact]
    public void SetHistoryLimitJsonRoundTrips()
    {
        var json = ClipSharedSettings.SetHistoryLimitJson("{}", 1000);

        Assert.Equal(1000, ClipSharedSettings.LoadFromJson(json).HistoryLimit);
    }

    [Fact]
    public void SetMaxItemSizeBytesJsonRoundTrips()
    {
        var json = ClipSharedSettings.SetMaxItemSizeBytesJson("{}", 25L * 1024 * 1024);

        Assert.Equal(25L * 1024 * 1024, ClipSharedSettings.LoadFromJson(json).MaxItemSizeBytes);
    }

    [Fact]
    public void SetClipboardFolderPathJsonRoundTripsAndClears()
    {
        var set = ClipSharedSettings.SetClipboardFolderPathJson("{}", "D:\\Clips");
        Assert.Equal("D:\\Clips", ClipSharedSettings.LoadFromJson(set).ClipboardFolderPath);

        var cleared = ClipSharedSettings.SetClipboardFolderPathJson(set, "   ");
        Assert.Null(ClipSharedSettings.LoadFromJson(cleared).ClipboardFolderPath);
    }

    [Fact]
    public void WritersPreserveUnknownKeys()
    {
        var json = """{ "OpenMode": 1, "Privacy": { "ExcludedApps": ["foo.exe"] } }""";

        var updated = ClipSharedSettings.SetHistoryLimitJson(json, 250);

        Assert.Contains("Privacy", updated);
        Assert.Contains("foo.exe", updated);
        Assert.Equal(ClipSharedOpenMode.CommandPalette, ClipSharedSettings.LoadFromJson(updated).OpenMode);
        Assert.Equal(250, ClipSharedSettings.LoadFromJson(updated).HistoryLimit);
    }
}
