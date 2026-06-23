using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryDateFilterTests
{
    private static readonly DateTime Today = new(2026, 6, 23);

    [Fact]
    public void IsDateFilterRecognizesDateIdsOnly()
    {
        Assert.True(ClipboardHistoryDateFilter.IsDateFilter(ClipboardHistoryDateFilter.Today));
        Assert.True(ClipboardHistoryDateFilter.IsDateFilter(ClipboardHistoryDateFilter.All));
        Assert.False(ClipboardHistoryDateFilter.IsDateFilter(ClipboardHistoryListFilter.All));
        Assert.False(ClipboardHistoryDateFilter.IsDateFilter(ClipboardHistoryListFilter.Pinned));
        Assert.False(ClipboardHistoryDateFilter.IsDateFilter(null));
        Assert.False(ClipboardHistoryDateFilter.IsDateFilter(""));
    }

    [Fact]
    public void NonDateAndDateAllFiltersMatchEverything()
    {
        var older = ItemCopiedOn(new DateTime(2024, 1, 1));

        // Kind/pinned ids and date "All" must short-circuit to true so the predicate composes
        // with the kind predicate on the single shared dropdown.
        Assert.True(ClipboardHistoryDateFilter.Matches(null, older, Today));
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryListFilter.Files, older, Today));
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.All, older, Today));
    }

    [Fact]
    public void TodayAndYesterdayFiltersAreExact()
    {
        var todayItem = ItemCopiedOn(Today);
        var yesterdayItem = ItemCopiedOn(Today.AddDays(-1));

        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Today, todayItem, Today));
        Assert.False(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Today, yesterdayItem, Today));

        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Yesterday, yesterdayItem, Today));
        Assert.False(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Yesterday, todayItem, Today));
    }

    [Fact]
    public void WeekFilterIsCumulativeIncludingTodayAndYesterday()
    {
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Week, ItemCopiedOn(Today), Today));
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Week, ItemCopiedOn(Today.AddDays(-1)), Today));
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Week, ItemCopiedOn(Today.AddDays(-5)), Today));
        // More than a week back drops out.
        Assert.False(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Week, ItemCopiedOn(new DateTime(2026, 6, 5)), Today));
    }

    [Fact]
    public void MonthFilterIsCumulativeWithinCurrentMonth()
    {
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Month, ItemCopiedOn(new DateTime(2026, 6, 5)), Today));
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Month, ItemCopiedOn(Today), Today));
        Assert.False(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Month, ItemCopiedOn(new DateTime(2026, 1, 15)), Today));
    }

    [Fact]
    public void YearFilterMatchesEverythingButOlder()
    {
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Year, ItemCopiedOn(new DateTime(2026, 1, 15)), Today));
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Year, ItemCopiedOn(Today), Today));
        Assert.False(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Year, ItemCopiedOn(new DateTime(2025, 12, 31)), Today));
    }

    [Fact]
    public void OlderFilterMatchesOnlyPriorYears()
    {
        Assert.True(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Older, ItemCopiedOn(new DateTime(2025, 12, 31)), Today));
        Assert.False(ClipboardHistoryDateFilter.Matches(ClipboardHistoryDateFilter.Older, ItemCopiedOn(new DateTime(2026, 1, 15)), Today));
    }

    [Fact]
    public void LabelForReturnsHumanReadableNames()
    {
        Assert.Equal("Today", ClipboardHistoryDateFilter.LabelFor(ClipboardHistoryDateFilter.Today));
        Assert.Equal("This week", ClipboardHistoryDateFilter.LabelFor(ClipboardHistoryDateFilter.Week));
        Assert.Equal("Older", ClipboardHistoryDateFilter.LabelFor(ClipboardHistoryDateFilter.Older));
        Assert.Equal("All dates", ClipboardHistoryDateFilter.LabelFor("nonsense"));
    }

    private static ClipboardHistoryListItem ItemCopiedOn(DateTime localDate)
    {
        var copied = new DateTimeOffset(localDate.AddHours(10), TimeZoneInfo.Local.GetUtcOffset(localDate.AddHours(10)));
        return new ClipboardHistoryListItem(
            Id: Guid.NewGuid().ToString("N"),
            Kind: "Text",
            Title: "Text",
            Preview: "Text",
            FilePaths: [],
            IsPinned: false,
            PinOrder: 0,
            HasOriginalFormatting: false,
            SourceApplication: "Test",
            AssetSizeBytes: null,
            CharacterCount: null,
            WordCount: null,
            LastUsedAt: copied,
            LastCopiedAt: copied,
            CopyCount: 1,
            ImageWidth: null,
            ImageHeight: null,
            DefaultActionId: null,
            Actions: []);
    }
}
