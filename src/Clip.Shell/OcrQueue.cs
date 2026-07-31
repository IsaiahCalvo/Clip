using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Clip.Core;

namespace Clip.Shell;

/// <summary>
/// Runs image text recognition well away from the clipboard capture path.
///
/// Capture writes the image to disk before the item is stored, so this only ever needs an id and
/// a file path — it never touches the live clipboard or the captured bitmap. Missing a copy would
/// be far worse than a screenshot taking a few seconds to become searchable.
/// </summary>
internal sealed class OcrQueue : IDisposable
{
    private readonly Channel<(string Id, string AssetPath)> _queue =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Func<ClipboardHistoryStore> _store;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _worker;

    public OcrQueue(Func<ClipboardHistoryStore> store, Action<string> log)
    {
        _store = store;
        _log = log;
    }

    public void Enqueue(string id, string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        if (!OcrTextExtractor.IsAvailable)
        {
            return;
        }

        _queue.Writer.TryWrite((id, assetPath!));
        EnsureWorker();
    }

    private void EnsureWorker()
    {
        _worker ??= Task.Run(() => RunAsync(_cancellation.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            // Drain whatever has piled up and write it in a single batch. Each save rewrites the
            // history file and rebuilds the indexes, so one write per image would be wasteful.
            var results = new Dictionary<string, string?>();
            while (reader.TryRead(out var request))
            {
                try
                {
                    results[request.Id] = await OcrTextExtractor
                        .TryExtractAsync(request.AssetPath, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"ocr failed id={request.Id} error={ex.GetType().Name}");
                    results[request.Id] = null;
                }
            }

            if (results.Count == 0)
            {
                continue;
            }

            try
            {
                var updated = _store().SetOcrText(results);
                var found = 0;
                foreach (var value in results.Values)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        found++;
                    }
                }

                _log($"ocr batch complete queued={results.Count} updated={updated} withText={found}");
            }
            catch (Exception ex)
            {
                _log($"ocr save failed error={ex.GetType().Name}");
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
