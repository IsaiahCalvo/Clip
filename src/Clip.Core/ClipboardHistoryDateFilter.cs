namespace Clip.Core;

/// <summary>
/// Date-recency filter for clipboard history (All / Today / Yesterday / This week /
/// This month / This year / Older). Mirrors the standalone shell's date dropdown
/// attached to the All pill. Reuses <see cref="ClipboardHistoryTimeBucket"/> for the
/// recency predicate so every surface buckets identically rather than duplicating
/// the DateKey logic.
/// </summary>
public static class ClipboardHistoryDateFilter
{
    /// <summary>Prefix that namespaces date filter ids so they never collide with kind ids.</summary>
    public const string IdPrefix = "date:";

    public const string All = IdPrefix + "all";
    public const string Today = IdPrefix + ClipboardHistoryTimeBucket.Today;
    public const string Yesterday = IdPrefix + ClipboardHistoryTimeBucket.Yesterday;
    public const string Week = IdPrefix + ClipboardHistoryTimeBucket.Week;
    public const string Month = IdPrefix + ClipboardHistoryTimeBucket.Month;
    public const string Year = IdPrefix + ClipboardHistoryTimeBucket.Year;
    public const string Older = IdPrefix + ClipboardHistoryTimeBucket.Older;

    /// <summary>
    /// True when <paramref name="filterId"/> selects a date-recency filter (i.e. it is one
    /// of the date ids, including the date "All"). Lets callers tell a date selection apart
    /// from a kind/pinned selection on the single shared dropdown.
    /// </summary>
    public static bool IsDateFilter(string? filterId) =>
        !string.IsNullOrWhiteSpace(filterId) &&
        filterId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the item falls inside the recency window selected by
    /// <paramref name="filterId"/>. Non-date ids (kind/pinned) and the date "All" id match
    /// everything, so this can be applied unconditionally alongside the kind predicate.
    /// </summary>
    public static bool Matches(string? filterId, ClipboardHistoryListItem item) =>
        Matches(filterId, item, DateTime.Today);

    /// <summary>
    /// Recency predicate relative to the supplied local <paramref name="today"/>.
    /// </summary>
    public static bool Matches(string? filterId, ClipboardHistoryListItem item, DateTime today)
    {
        if (!IsDateFilter(filterId) || string.Equals(filterId, All, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var bucket = ClipboardHistoryTimeBucket.KeyFor(item, today);
        var normalized = filterId!.Trim().ToLowerInvariant();
        return normalized switch
        {
            Today => bucket == ClipboardHistoryTimeBucket.Today,
            Yesterday => bucket == ClipboardHistoryTimeBucket.Yesterday,
            // "This week" includes the more recent Today/Yesterday buckets, matching the
            // standalone's cumulative recency windows.
            Week => bucket is ClipboardHistoryTimeBucket.Today
                or ClipboardHistoryTimeBucket.Yesterday
                or ClipboardHistoryTimeBucket.Week,
            Month => bucket is ClipboardHistoryTimeBucket.Today
                or ClipboardHistoryTimeBucket.Yesterday
                or ClipboardHistoryTimeBucket.Week
                or ClipboardHistoryTimeBucket.Month,
            Year => bucket != ClipboardHistoryTimeBucket.Older,
            Older => bucket == ClipboardHistoryTimeBucket.Older,
            _ => true,
        };
    }

    /// <summary>Human-readable label for a date filter id.</summary>
    public static string LabelFor(string filterId) => filterId switch
    {
        All => "All dates",
        Today => "Today",
        Yesterday => "Yesterday",
        Week => "This week",
        Month => "This month",
        Year => "This year",
        Older => "Older",
        _ => "All dates",
    };
}
