namespace Clip.Core;

/// <summary>
/// Buckets clipboard history items into temporal groups (Today, Yesterday, This week,
/// This month, This year, Older) for section grouping. Promoted from the standalone
/// shell's GroupItems/DateKey logic so every surface buckets identically.
/// </summary>
public static class ClipboardHistoryTimeBucket
{
    public const string Today = "today";
    public const string Yesterday = "yesterday";
    public const string Week = "week";
    public const string Month = "month";
    public const string Year = "year";
    public const string Older = "older";

    /// <summary>
    /// Bucket keys ordered most-recent first. Use to order sections.
    /// </summary>
    public static IReadOnlyList<string> OrderedKeys { get; } =
        [Today, Yesterday, Week, Month, Year, Older];

    /// <summary>
    /// Computes the bucket key for an item based on when it was last copied,
    /// relative to today (local time).
    /// </summary>
    public static string KeyFor(ClipboardHistoryListItem item) => KeyFor(item, DateTime.Today);

    /// <summary>
    /// Computes the bucket key for an item relative to the supplied local <paramref name="today"/>.
    /// </summary>
    public static string KeyFor(ClipboardHistoryListItem item, DateTime today) =>
        KeyFor(item.LastCopiedAt, today);

    /// <summary>
    /// Computes the bucket key for a copied timestamp relative to the supplied local
    /// <paramref name="today"/>. Mirrors Clip.Shell DateKey.
    /// </summary>
    public static string KeyFor(DateTimeOffset lastCopiedAt, DateTime today)
    {
        var copied = lastCopiedAt.LocalDateTime.Date;
        if (copied == today)
        {
            return Today;
        }

        if (copied == today.AddDays(-1))
        {
            return Yesterday;
        }

        if (copied >= today.AddDays(-7))
        {
            return Week;
        }

        if (copied.Year == today.Year && copied.Month == today.Month)
        {
            return Month;
        }

        return copied.Year == today.Year ? Year : Older;
    }

    /// <summary>
    /// Human-readable section label for a bucket key (e.g. "Today", "This week").
    /// </summary>
    public static string LabelFor(string key) => key switch
    {
        Today => "Today",
        Yesterday => "Yesterday",
        Week => "This week",
        Month => "This month",
        Year => "This year",
        Older => "Older",
        _ => "Older",
    };

    /// <summary>
    /// Section label for an item (e.g. "Today", "This week").
    /// </summary>
    public static string LabelFor(ClipboardHistoryListItem item) => LabelFor(KeyFor(item));

    /// <summary>
    /// Section label for an item relative to the supplied local <paramref name="today"/>.
    /// </summary>
    public static string LabelFor(ClipboardHistoryListItem item, DateTime today) =>
        LabelFor(KeyFor(item, today));
}
