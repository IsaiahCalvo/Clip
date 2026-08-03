using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Clip.Shell;

/// <summary>
/// Measures how far the picture-in-picture window's contents fall behind its frame while it is
/// being resized — the stutter, as a number.
///
/// Dragging a window is really just changing its size many times a second, and the complaint is
/// that the page inside cannot repaint that fast, so the window's background shows through along
/// the edges being dragged. That is measurable without anyone touching a mouse: drive the window
/// through the same sizes a drag would, at the same rate, and ask the page how big it currently
/// believes it is. The difference is the gap the eye reads as jitter.
///
/// This is a close proxy for a drag, not a replica: Windows runs its own loop while a real border
/// is being held, and that loop follows the actual cursor, which is the one thing this deliberately
/// avoids using. Calibrate it against a real drag before trusting a run of it.
/// </summary>
internal static class JankHarness
{
    public static bool Requested(string[] args) =>
        args.Any(a => a.Equals("--jank-test", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args)
    {
        var options = JankOptions.Parse(args);

        if (!File.Exists(options.File))
        {
            Report($"no sample video at {options.File} — pass --file=<path>");
            return 2;
        }

        var workArea = WorkAreaOfMonitor(options.Monitor);
        Report($"monitor {options.Monitor} work area {workArea.Width}x{workArea.Height} at {workArea.X},{workArea.Y}");

        var environment = await CreateEnvironmentAsync();
        var window = new MediaPipWindow(
            options.File,
            isVideo: true,
            startTime: 0,
            backgroundHex: "#1e1e1e",
            textHex: "#ffffff",
            ownerWorkArea: workArea,
            environment: () => Task.FromResult(environment));

        window.Show();

        var view = window.PlayerView;
        if (!await WaitForPageAsync(view))
        {
            Report("the player never finished loading");
            window.Close();
            return 3;
        }

        var clock = Stopwatch.StartNew();
        var reported = new List<Sample>();
        var applied = new List<Sample>();

        var letterbox = new List<double>();

        view.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            var raw = e.TryGetWebMessageAsString();
            var width = ProbeWidthOf(raw);
            if (width <= 0)
            {
                return;
            }

            reported.Add(new Sample(clock.Elapsed.TotalMilliseconds, width));

            // How much of the page the picture is failing to cover — bars down the sides.
            var picture = FieldOf(raw, "pic");
            if (picture > 0)
            {
                letterbox.Add(width - picture);
            }
        };

        // Report the page's own idea of its width once per painted frame. Anything the page says
        // arrives only after it has actually laid out at that size, which is what makes the
        // difference from the window's size meaningful. The page thinks in its own units, so scale
        // it back to real screen pixels or every reading is short by the display's scaling factor.
        await view.ExecuteScriptAsync(
            """
            (() => {
              const video = document.querySelector('video');
              const tick = () => {
                try {
                  const r = video.getBoundingClientRect();
                  const d = window.devicePixelRatio;
                  window.chrome.webview.postMessage(JSON.stringify({
                    probe: Math.round(window.innerWidth * d),
                    // Where the picture itself actually sits. If this wanders relative to the page
                    // the picture is moving inside a frame that is otherwise keeping up, which
                    // looks the same to the eye and would not show up in the width alone.
                    pic: Math.round(r.width * d),
                    picLeft: Math.round(r.left * d),
                  }));
                } catch {}
                requestAnimationFrame(tick);
              };
              requestAnimationFrame(tick);
            })();
            """);

        await Task.Delay(200);
        reported.Clear();

        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Sweep(handle, options, clock, applied);

        // Let any frame still in flight arrive before the books are closed.
        await Task.Delay(400);

        if (letterbox.Count > 0)
        {
            var bars = letterbox.Where(b => b > 0).ToArray();
            Report(
                $"bars beside the picture: worst={letterbox.Max()}px "
                + $"present={bars.Length * 100 / letterbox.Count}% of frames "
                + $"changes={letterbox.Zip(letterbox.Skip(1), (a, b) => Math.Abs(a - b)).Count(d => d > 0)}");
        }

        var result = Measure(options, applied, reported);
        Write(options, result);
        window.Close();

        return result.WorstGapPx > options.FailOverPx ? 1 : 0;
    }

    /// <summary>
    /// Grows the window a step at a time and then shrinks it back, exactly through the code a real
    /// drag runs: each step is offered to the window as a proposed rectangle, and whatever the
    /// window makes of it — aspect, minimum size, staying on screen — is what gets applied.
    /// </summary>
    private static void Sweep(IntPtr handle, JankOptions options, Stopwatch clock, List<Sample> applied)
    {
        GetWindowRect(handle, out var start);

        SendMessage(handle, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero);

        var deltas = Enumerable.Range(1, options.Steps).Select(i => i * options.StepPx)
            .Concat(Enumerable.Range(0, options.Steps).Select(i => (options.Steps - i - 1) * options.StepPx))
            .ToArray();

        var pace = Stopwatch.StartNew();

        for (var i = 0; i < deltas.Length; i++)
        {
            var proposed = start with { Right = start.Right + deltas[i], Bottom = start.Bottom + deltas[i] };
            var settled = OfferToWindow(handle, proposed);

            // Take the size the window settled on, but leave it where it was put: staying on screen
            // would otherwise drag an off-screen window back into view mid-run.
            SetWindowPos(
                handle,
                IntPtr.Zero,
                start.Left,
                start.Top,
                settled.Right - settled.Left,
                settled.Bottom - settled.Top,
                SwpNoZOrder | SwpNoActivate);

            applied.Add(new Sample(clock.Elapsed.TotalMilliseconds, settled.Right - settled.Left));

            // Pump so the window actually repaints between steps, as it would mid-drag.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.Render);

            var due = (i + 1) * options.IntervalMs;
            var wait = due - pace.Elapsed.TotalMilliseconds;
            if (wait > 0)
            {
                Thread.Sleep((int)wait);
            }
        }

        SendMessage(handle, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Runs a proposed rectangle through the window's own WM_SIZING handling.</summary>
    private static RECT OfferToWindow(IntPtr handle, RECT proposed)
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());

        try
        {
            Marshal.StructureToPtr(proposed, memory, false);
            SendMessage(handle, WmSizing, new IntPtr(WmszBottomRight), memory);
            return Marshal.PtrToStructure<RECT>(memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static JankResult Measure(JankOptions options, List<Sample> applied, List<Sample> reported)
    {
        var gaps = new List<double>();

        foreach (var sample in reported)
        {
            // What the window's width was when the page painted at the width it just reported.
            var host = applied.LastOrDefault(a => a.Ms <= sample.Ms);
            if (host.Width == 0)
            {
                continue;
            }

            gaps.Add(Math.Abs(host.Width - sample.Width));
        }

        if (gaps.Count == 0)
        {
            return new JankResult(0, 0, 0, 0, reported.Count, applied.Count);
        }

        var mean = gaps.Average();
        var variance = gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count;

        return new JankResult(
            WorstGapPx: gaps.Max(),
            MeanGapPx: Math.Round(mean, 2),
            GapSpreadPx: Math.Round(Math.Sqrt(variance), 2),
            BehindFraction: Math.Round(gaps.Count(g => g > options.TolerancePx) / (double)gaps.Count, 3),
            FramesSeen: reported.Count,
            StepsApplied: applied.Count);
    }

    private static void Write(JankOptions options, JankResult result)
    {
        Report(
            $"steps={result.StepsApplied} frames={result.FramesSeen} "
            + $"worst={result.WorstGapPx}px mean={result.MeanGapPx}px spread={result.GapSpreadPx}px "
            + $"behind={result.BehindFraction:P0}");

        if (string.IsNullOrWhiteSpace(options.Out))
        {
            return;
        }

        File.WriteAllText(
            options.Out,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        Report($"written to {options.Out}");
    }


    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(Path.GetTempPath(), "clip-jank-harness"),
            options: new CoreWebView2EnvironmentOptions
            {
                // Chromium stops painting windows it believes nobody can see, which would make a
                // window parked off to one side look flawless and tell us nothing.
                AdditionalBrowserArguments =
                    "--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows",
            });

    private static async Task<bool> WaitForPageAsync(Microsoft.Web.WebView2.Wpf.WebView2 view)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(100);

            try
            {
                if (view.CoreWebView2 is null)
                {
                    continue;
                }

                var ready = await view.ExecuteScriptAsync("!!document.querySelector('video')");
                if (ready == "true")
                {
                    return true;
                }
            }
            catch
            {
                // Not initialised yet; the next attempt will find out.
            }
        }

        return false;
    }

    private static int ProbeWidthOf(string? json) => FieldOf(json, "probe");

    private static int FieldOf(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.Contains("probe", StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Where to put the window. Monitor zero means just off the left of everything, which is the
    /// default: Windows still draws windows nobody can see, so it measures the same while staying
    /// out of the way. Pass a real monitor number to watch a run.
    /// </summary>
    private static Rect WorkAreaOfMonitor(int number)
    {
        var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ToArray();

        if (number <= 0)
        {
            var leftmost = screens[0].Bounds;
            return new Rect(leftmost.X - OffScreenWidth - 200, leftmost.Y, OffScreenWidth, OffScreenHeight);
        }

        var area = screens[Math.Min(number - 1, screens.Length - 1)].WorkingArea;
        return new Rect(area.X, area.Y, area.Width, area.Height);
    }

    private const int OffScreenWidth = 1600;
    private const int OffScreenHeight = 1000;

    private static void Report(string message)
    {
        ShellLog.Info($"jank: {message}");
        Console.Error.WriteLine($"jank: {message}");
    }

    private readonly record struct Sample(double Ms, int Width);

    private const int WmSizing = 0x0214;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmszBottomRight = 8;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private record struct RECT(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}

/// <summary>What a run of the harness reported. Written as JSON so runs can be compared.</summary>
internal sealed record JankResult(
    double WorstGapPx,
    double MeanGapPx,
    double GapSpreadPx,
    double BehindFraction,
    int FramesSeen,
    int StepsApplied);

internal sealed record JankOptions(
    string File,
    int Monitor,
    int Steps,
    int StepPx,
    int IntervalMs,
    int TolerancePx,
    int FailOverPx,
    string? Out)
{
    public static JankOptions Parse(string[] args) => new(
        File: Value(args, "--file") ?? DefaultSample(),
        Monitor: Number(args, "--monitor", 0),
        Steps: Number(args, "--steps", 60),
        StepPx: Number(args, "--step-px", 8),
        IntervalMs: Number(args, "--interval-ms", 16),
        TolerancePx: Number(args, "--tolerance-px", 2),
        FailOverPx: Number(args, "--fail-over-px", int.MaxValue),
        Out: Value(args, "--out"));

    private static string DefaultSample() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clip-jank-sample.mp4");

    private static string? Value(string[] args, string name) => args
        .FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        ?[(name.Length + 1)..];

    private static int Number(string[] args, string name, int fallback) =>
        int.TryParse(Value(args, name), out var parsed) ? parsed : fallback;
}
