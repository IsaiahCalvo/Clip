using Clip.Core;

namespace Clip.Tests;

public sealed class ClipboardHistoryTimeBucketTests
{
    private static readonly DateTime Today = new(2026, 6, 23);

    [Fact]
    public void TodayCopiedItemIsTodayBucket()
    {
        var item = Item(Local(Today.AddHours(9)));

        Assert.Equal(ClipboardHistoryTimeBucket.Today, ClipboardHistoryTimeBucket.KeyFor(item, Today));
        Assert.Equal("Today", ClipboardHistoryTimeBucket.LabelFor(item, Today));
    }

    [Fact]
    public void YesterdayCopiedItemIsYesterdayBucket()
    {
        var item = Item(Local(Today.AddDays(-1).AddHours(13)));

        Assert.Equal(ClipboardHistoryTimeBucket.Yesterday, ClipboardHistoryTimeBucket.KeyFor(item, Today));
        Assert.Equal("Yesterday", ClipboardHistoryTimeBucket.LabelFor(item, Today));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-7)]
    public void ItemWithinSevenDaysIsWeekBucket(int dayOffset)
    {
        var item = Item(Local(Today.AddDays(dayOffset)));

        Assert.Equal(ClipboardHistoryTimeBucket.Week, ClipboardHistoryTimeBucket.KeyFor(item, Today));
        Assert.Equal("This week", ClipboardHistoryTimeBucket.LabelFor(item, Today));
    }

    [Fact]
    public void EarlierThisMonthButOverAWeekAgoIsMonthBucket()
    {
        // June 23 today; June 5 is same month/year but more than 7 days back.
        var item = Item(Local(new DateTime(2026, 6, 5)));

        Assert.Equal(ClipboardHistoryTimeBucket.Month, ClipboardHistoryTimeBucket.KeyFor(item, Today));
        Assert.Equal("This month", ClipboardHistoryTimeBucket.LabelFor(item, Today));
    }

    [Fact]
    public void EarlierThisYearButPriorMonthIsYearBucket()
    {
        var item = Item(Local(new DateTime(2026, 1, 15)));

        Assert.Equal(ClipboardHistoryTimeBucket.Year, ClipboardHistoryTimeBucket.KeyFor(item, Today));
        Assert.Equal("This year", ClipboardHistoryTimeBucket.LabelFor(item, Today));
    }

    [Fact]
    public void PriorYearIsOlderBucket()
    {
        var item = Item(Local(new DateTime(2025, 12, 31)));

        Assert.Equal(ClipboardHistoryTimeBucket.Older, ClipboardHistoryTimeBucket.KeyFor(item, Today));
        Assert.Equal("Older", ClipboardHistoryTimeBucket.LabelFor(item, Today));
    }

    [Fact]
    public void OrderedKeysAreMostRecentFirst()
    {
        Assert.Equal(
            [
                ClipboardHistoryTimeBucket.Today,
                ClipboardHistoryTimeBucket.Yesterday,
                ClipboardHistoryTimeBucket.Week,
                ClipboardHistoryTimeBucket.Month,
                ClipboardHistoryTimeBucket.Year,
                ClipboardHistoryTimeBucket.Older,
            ],
            ClipboardHistoryTimeBucket.OrderedKeys);
    }

    [Fact]
    public void LabelForUnknownKeyFallsBackToOlder()
    {
        Assert.Equal("Older", ClipboardHistoryTimeBucket.LabelFor("nonsense"));
    }

    private static DateTimeOffset Local(DateTime localDate) =>
        new(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));

    private static ClipboardHistoryListItem Item(DateTimeOffset lastCopiedAt) => new(
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
        LastUsedAt: lastCopiedAt,
        LastCopiedAt: lastCopiedAt,
        CopyCount: 1,
        ImageWidth: null,
        ImageHeight: null,
        DefaultActionId: null,
        Actions: []);
}
