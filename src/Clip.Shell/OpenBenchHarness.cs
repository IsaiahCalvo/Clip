using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Clip.Core;

namespace Clip.Shell;

/// <summary>
/// Times how long a page takes to become usable, off screen, the same way every run.
///
/// The shell already logs a dozen elapsed times per open, but each starts its own stopwatch, so
/// they cannot be added together and none of them covers the whole thing: "palette shown" stops
/// before the rows are rendered and long before a preview arrives. This drives the real open path
/// with one clock and reports every sample, so the driver can take a median and a p95 rather than
/// trusting a single run on a machine that was doing something else at the time.
///
/// Open is treated as finished when a user could act on the window: it is up and opaque, the search
/// box has keyboard focus so typing filters, and enough rows exist to fill what is on screen. Rows
/// past the fold are deliberately not waited for — nobody can see them, and rendering them later is
/// the right design rather than a delay to measure. Pages with a preview additionally wait for the
/// preview pane to be revealed.
///
/// Off screen means the window is parked beyond the desktop: Windows still renders it, nobody's
/// display is taken, and the foreground-stealing Win32 calls are skipped — see ShowPalette. Those
/// calls are the one part of a real open this cannot see; measure them separately before treating
/// these numbers as absolute. Comparisons between runs are unaffected.
/// </summary>
internal static class OpenBenchHarness
{
    public static bool Requested(string[] args) =>
        args.Any(a => a.Equals("--open-bench", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rows that must exist before the list counts as painted. This is the shell's own first-paint
    /// budget (InitialSummaryFirstPaintLimit) — about what fits on screen. Rows past the fold are
    /// not waited for: nobody can see them, and deferring them is the right design rather than a
    /// delay worth measuring.
    /// </summary>
    private const int RowsForAScreenful = 8;

    public static async Task RunAsync(MainWindow window, string[] args)
    {
        BenchMarks.Enabled = true;

        var page = JankOptions.Value(args, "--page") ?? "palette";
        var runs = JankOptions.Number(args, "--runs", 5);
        var settleMs = JankOptions.Number(args, "--settle-ms", 600);
        var timeoutMs = JankOptions.Number(args, "--timeout-ms", 15000);
        var output = JankOptions.Value(args, "--out");

        // A reopen normally follows a copy — that is what the palette is being opened to paste
        // over. Pass --unchanged to measure the rarer reopen where nothing arrived in between.
        var clipboardChanged = !args.Any(a => a.Equals("--unchanged", StringComparison.OrdinalIgnoreCase));

        // Everything the shell warms at startup has to have finished before the first open, or run
        // one measures the tail of boot rather than an open — and a real user's first press comes
        // minutes after login, not three seconds. Waiting for the warm-up rather than a fixed delay
        // is what keeps that honest as the warm-up grows.
        var settle = System.Diagnostics.Stopwatch.StartNew();
        while (!window.BenchWarmupComplete && settle.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(50);
        }

        Report($"warm-up settled after {settle.Elapsed.TotalMilliseconds:F0}ms complete={window.BenchWarmupComplete}");
        await Task.Delay(500);

        var samples = new List<Sample>();

        for (var run = 0; run < runs; run++)
        {
            window.BenchResetForRun(clipboardChanged);
            var sample = await MeasureOnceAsync(window, page, run, timeoutMs);
            samples.Add(sample);
            Report(sample.ToString());
            if (args.Any(a => a.Equals("--stages", StringComparison.OrdinalIgnoreCase)))
            {
                Report("  stages: " + string.Join("  ", BenchMarks.Taken.Select(s => $"{s.Name}={s.Ms:F1}")));
            }

            window.ConcealForOpenTest();
            await Task.Delay(settleMs);
        }

        // Every stamp of the last run, in order, so the sequence can be read rather than inferred
        // from priorities — the harness's own polling sits in the same queue as the work it watches,
        // and an ordering that looks impossible is usually the harness interfering.
        Report("stages: " + string.Join(
            "  ",
            BenchMarks.Taken.Select(s => $"{s.Name}={s.Ms:F1}")));

        Write(output, page, samples);
        ShellLog.Flush();
    }

    private static async Task<Sample> MeasureOnceAsync(MainWindow window, string page, int run, int timeoutMs)
    {
        BenchMarks.Begin();
        window.ShowPalette();

        // The list has to exist before an item can be picked out of it, so pages that select
        // something wait for the palette first and then time the selection on the same clock.
        var openMs = await WaitForAsync(
            window,
            () =>
            {
                // Stamped separately so a slow open says which of the three conditions it waited on
                // rather than leaving it to be inferred.
                var shown = window.BenchWindowShown;
                var focused = window.BenchSearchFocused;
                var rows = window.BenchRenderedRows >= Math.Min(window.BenchTotalItems, RowsForAScreenful);

                if (shown) BenchMarks.MarkOnce("cond-shown");
                if (focused) BenchMarks.MarkOnce("cond-focused");
                if (rows) BenchMarks.MarkOnce("cond-rows");

                return shown && focused && rows;
            },
            timeoutMs);

        double? readyMs = null;
        var detail = string.Empty;

        if (page.StartsWith("preview-", StringComparison.OrdinalIgnoreCase))
        {
            var kind = page["preview-".Length..];
            var item = PickForPreview(window, kind);
            if (item is null)
            {
                detail = $"no fixture item for {kind}";
            }
            else
            {
                detail = item.Id;
                window.BenchSelect(item);
                readyMs = await WaitForAsync(window, () => BenchMarks.When("preview-ready") is not null, timeoutMs);
            }
        }
        else if (page.Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            window.BenchOpenSettings();
            readyMs = await WaitForAsync(window, () => window.BenchSettingsOpen, timeoutMs);
        }

        return new Sample(
            Run: run,
            Page: page,
            OpenMs: openMs,
            ReadyMs: readyMs,
            Shown: BenchMarks.When("shown"),
            Focused: BenchMarks.When("focused"),
            RowsFirst: BenchMarks.When("rows-first"),
            RowsComplete: BenchMarks.When("rows-complete"),
            Rows: window.BenchRenderedRows,
            Items: window.BenchTotalItems,
            Detail: detail);
    }

    /// <summary>
    /// The first item of the kind a page is named after. Fixed history, fixed pick, so every run of
    /// a page measures the same work rather than whatever happened to be copied last.
    /// </summary>
    private static ClipboardHistoryItem? PickForPreview(MainWindow window, string kind) => kind.ToLowerInvariant() switch
    {
        "text" => window.BenchItems.FirstOrDefault(i => i.Kind == ClipboardItemKind.Text),
        "link" => window.BenchItems.FirstOrDefault(i => i.Kind == ClipboardItemKind.Link),
        "color" => window.BenchItems.FirstOrDefault(i => i.Kind == ClipboardItemKind.Color),
        "image" => window.BenchItems.FirstOrDefault(i => i.Kind == ClipboardItemKind.Image),
        _ => window.BenchItems.FirstOrDefault(i =>
            i.Kind == ClipboardItemKind.Files &&
            FirstFileOf(i) is { } path &&
            ExtensionsFor(kind).Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)),
    };

    private static string? FirstFileOf(ClipboardHistoryItem item) => item.FilePaths?.FirstOrDefault();

    private static string[] ExtensionsFor(string kind) => kind.ToLowerInvariant() switch
    {
        "code" => [".cs", ".ts", ".js", ".py", ".json"],
        "media" => [".mp4", ".mov", ".webm"],
        "audio" => [".m4a", ".mp3", ".wav"],
        "pdf" => [".pdf"],
        "excel" => [".xlsx", ".xls"],
        "word" => [".docx", ".doc"],
        "html" => [".html", ".htm"],
        "textfile" => [".txt", ".md", ".log"],
        _ => [],
    };

    /// <summary>
    /// Waits for a condition and answers with the time it became true.
    ///
    /// The check runs at Input priority because that is where a key press sits in the queue: if the
    /// check gets to run and finds the window ready, a keystroke arriving at that moment would have
    /// been handled too, which is the whole claim being measured. Rendering outranks Input, so
    /// anything drawn is already drawn by then. Checking lower, at Background, waits behind the
    /// deferred rows and the preview and reports a window as unusable while it would happily accept
    /// typing — worth about sixty milliseconds of invented latency per open.
    ///
    /// The delay between checks matters as much as the priority: a tight Input-priority loop sits
    /// above the deferred row rendering and starves the very work it is timing.
    /// </summary>
    private static async Task<double> WaitForAsync(MainWindow window, Func<bool> done, int timeoutMs)
    {
        while (BenchMarks.Elapsed < timeoutMs)
        {
            if (await window.Dispatcher.InvokeAsync(done, DispatcherPriority.Input))
            {
                return BenchMarks.Elapsed;
            }

            await Task.Delay(1);
        }

        return -1;
    }

    private static void Write(string? output, string page, List<Sample> samples)
    {
        var taken = samples.Where(s => s.OpenMs >= 0).Select(s => s.OpenMs).OrderBy(v => v).ToArray();
        Report(taken.Length == 0
            ? $"{page}: no successful runs"
            : $"{page}: runs={taken.Length} min={taken.First():F1} median={Percentile(taken, 50):F1} p95={Percentile(taken, 95):F1} max={taken.Last():F1}");

        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        File.WriteAllText(
            output,
            JsonSerializer.Serialize(
                new { page, samples },
                new JsonSerializerOptions { WriteIndented = true }));

        Report($"written to {output}");
    }

    internal static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return -1;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        // Linear interpolation between ranks: with the handful of runs a bench does, rounding to a
        // whole index makes p95 jump between two samples and read as noise.
        var rank = (percentile / 100.0) * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return low == high ? sorted[low] : sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
    }

    private static void Report(string message)
    {
        ShellLog.Info($"open-bench: {message}");
        Console.Error.WriteLine($"open-bench: {message}");
    }

    private sealed record Sample(
        int Run,
        string Page,
        double OpenMs,
        double? ReadyMs,
        double? Shown,
        double? Focused,
        double? RowsFirst,
        double? RowsComplete,
        int Rows,
        int Items,
        string Detail)
    {
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "run={0} page={1} open={2:F1}ms ready={3} shown={4:F1} focused={5:F1} rowsFirst={6:F1} rowsComplete={7} rows={8}/{9}",
            Run,
            Page,
            OpenMs,
            ReadyMs is { } r ? r.ToString("F1", CultureInfo.InvariantCulture) : "-",
            Shown ?? -1,
            Focused ?? -1,
            RowsFirst ?? -1,
            RowsComplete is { } rc ? rc.ToString("F1", CultureInfo.InvariantCulture) : "-",
            Rows,
            Items);
    }
}
