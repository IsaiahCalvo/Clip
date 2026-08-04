using System.Collections.Concurrent;
using System.Diagnostics;
using Clip.Core;
using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The queue's contract is that OCR never disturbs capture: bad input is ignored, a file that
/// cannot be read still completes the batch, and a store failure is logged rather than thrown.
/// The batch tests need the Windows OCR engine present (its language pack is a machine fact); on a
/// machine without one they no-op, because without the engine the queue refuses work by design.
/// </summary>
[Collection("OcrStatics")]
public sealed class OcrQueueCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public OcrQueueCoverageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup only.
        }
    }

    [Fact]
    public void BlankIdsAndPathsAreIgnoredWithoutStartingAWorker()
    {
        var logs = new ConcurrentQueue<string>();
        using var queue = new OcrQueue(
            () => throw new InvalidOperationException("the store must never be touched"),
            logs.Enqueue);

        queue.Enqueue("", @"C:\somewhere\image.png");
        queue.Enqueue("item-1", null);
        queue.Enqueue("item-1", "   ");

        Assert.Empty(logs);
    }

    [Fact]
    public void AnUnreadableImageStillCompletesItsBatch()
    {
        if (!OcrTextExtractor.IsAvailable)
        {
            return; // no OCR language pack on this machine; the queue refuses work by design
        }

        var asset = Path.Combine(_root, "garbage.png");
        File.WriteAllBytes(asset, [1, 2, 3, 4, 5]);

        var store = new ClipboardHistoryStore(Path.Combine(_root, "store"));
        var logs = new ConcurrentQueue<string>();
        using var queue = new OcrQueue(() => store, logs.Enqueue);

        queue.Enqueue("item-1", asset);

        Assert.True(
            WaitFor(() => logs.Any(l => l.StartsWith("ocr batch complete", StringComparison.Ordinal))),
            $"batch never completed; logs: {string.Join(" | ", logs)}");

        var line = logs.First(l => l.StartsWith("ocr batch complete", StringComparison.Ordinal));
        Assert.Contains("queued=1", line);
        Assert.Contains("updated=0", line);  // the store holds no item with that id
        Assert.Contains("withText=0", line); // garbage bytes decode to no text
    }

    [Fact]
    public void AFailingStoreIsLoggedRatherThanThrown()
    {
        if (!OcrTextExtractor.IsAvailable)
        {
            return; // no OCR language pack on this machine; the queue refuses work by design
        }

        var asset = Path.Combine(_root, "garbage2.png");
        File.WriteAllBytes(asset, [9, 9, 9, 9]);

        var logs = new ConcurrentQueue<string>();
        using var queue = new OcrQueue(
            () => throw new InvalidOperationException("store unavailable"),
            logs.Enqueue);

        queue.Enqueue("item-err", asset);

        Assert.True(
            WaitFor(() => logs.Any(l => l.StartsWith("ocr save failed", StringComparison.Ordinal))),
            $"failure was never logged; logs: {string.Join(" | ", logs)}");

        Assert.Contains("error=InvalidOperationException",
            logs.First(l => l.StartsWith("ocr save failed", StringComparison.Ordinal)));
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 30_000)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }
}
