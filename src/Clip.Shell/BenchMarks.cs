using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Clip.Shell;

/// <summary>
/// Stamps, in milliseconds from the start of one open, the moments the shell already logs.
///
/// The trace lines each carry their own stopwatch, started at different points, so they cannot be
/// added up into an answer to "how long did that open take". These stamps share one clock started
/// when the open begins, which is the only way the pieces line up. Everything is recorded on the UI
/// thread — that is where all of the marked work happens — so no locking is needed, and the whole
/// class costs nothing until a harness turns it on.
/// </summary>
internal static class BenchMarks
{
    private static readonly List<Stage> Stamps = new(32);
    private static Stopwatch? _clock;

    public static bool Enabled { get; set; }

    /// <summary>Starts a new open. Everything marked after this is relative to now.</summary>
    public static void Begin()
    {
        Stamps.Clear();
        _clock = Stopwatch.StartNew();
    }

    public static void Mark(string stage)
    {
        if (!Enabled || _clock is not { } clock)
        {
            return;
        }

        Stamps.Add(new Stage(stage, clock.Elapsed.TotalMilliseconds));
    }

    /// <summary>Stamps a stage the first time only — for conditions polled in a loop.</summary>
    public static void MarkOnce(string stage)
    {
        if (!Enabled || When(stage) is not null)
        {
            return;
        }

        Mark(stage);
    }

    public static double Elapsed => _clock?.Elapsed.TotalMilliseconds ?? 0;

    /// <summary>When a stage first happened, or null if it never did.</summary>
    public static double? When(string stage) => Stamps
        .Where(s => s.Name == stage)
        .Select(s => (double?)s.Ms)
        .FirstOrDefault();

    public static IReadOnlyList<Stage> Taken => Stamps;

    internal readonly record struct Stage(string Name, double Ms);
}
