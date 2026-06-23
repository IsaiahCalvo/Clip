using Clip.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinHistoryStatus = Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus;
using WinFormats = Windows.ApplicationModel.DataTransfer.StandardDataFormats;

namespace Clip.WindowsHistory;

internal sealed class WindowsClipboardHistorySource : IClipboardHistorySource
{
    public async Task<IReadOnlyList<ClipboardHistorySnapshotItem>> GetItemsAsync(Func<string, string> reserveImagePath, CancellationToken cancellationToken = default)
    {
        if (!WinClipboard.IsHistoryEnabled())
        {
            return [];
        }

        var result = await WinClipboard.GetHistoryItemsAsync();
        if (result.Status != WinHistoryStatus.Success)
        {
            return [];
        }

        var items = new List<ClipboardHistorySnapshotItem>();
        foreach (var historyItem in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await TryReadItemAsync(historyItem, reserveImagePath, cancellationToken);
            if (snapshot is not null)
            {
                items.Add(snapshot);
            }
        }

        return items;
    }

    private static async Task<ClipboardHistorySnapshotItem?> TryReadItemAsync(Windows.ApplicationModel.DataTransfer.ClipboardHistoryItem historyItem, Func<string, string> reserveImagePath, CancellationToken cancellationToken)
    {
        try
        {
            var content = historyItem.Content;
            if (content.Contains(WinFormats.StorageItems))
            {
                var paths = await ReadStoragePathsAsync(content);
                if (paths.Count > 0)
                {
                    return new ClipboardHistorySnapshotItem
                    {
                        Kind = ClipboardItemKind.Files,
                        FilePaths = paths,
                        Preview = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files",
                        CopiedAt = historyItem.Timestamp,
                        SourceApplication = SourceApplication(content),
                    };
                }
            }

            if (content.Contains(WinFormats.Bitmap))
            {
                var image = await ReadBitmapAsync(content, reserveImagePath, cancellationToken);
                if (image.Path is not null)
                {
                    return new ClipboardHistorySnapshotItem
                    {
                        Kind = ClipboardItemKind.Image,
                        AssetPath = image.Path,
                        Preview = "Image",
                        CopiedAt = historyItem.Timestamp,
                        SourceApplication = SourceApplication(content),
                        ImageWidth = image.Width,
                        ImageHeight = image.Height,
                    };
                }
            }

            if (content.Contains(WinFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var html = content.Contains(WinFormats.Html) ? await TryReadHtmlAsync(content) : null;
                    var rtf = content.Contains(WinFormats.Rtf) ? await TryReadRtfAsync(content) : null;
                    return new ClipboardHistorySnapshotItem
                    {
                        Kind = ClipboardLinkDetector.IsLinkOrEmail(text) ? ClipboardItemKind.Link : ClipboardItemKind.Text,
                        Text = text,
                        Preview = ClipboardHistoryStore.PreviewText(text),
                        HtmlText = html,
                        RtfText = rtf,
                        CopiedAt = historyItem.Timestamp,
                        SourceApplication = SourceApplication(content),
                    };
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadStoragePathsAsync(DataPackageView content)
    {
        try
        {
            var storageItems = await content.GetStorageItemsAsync();
            return storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<(string? Path, int? Width, int? Height)> ReadBitmapAsync(DataPackageView content, Func<string, string> reserveImagePath, CancellationToken cancellationToken)
    {
        try
        {
            var reference = await content.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            if (stream.Size <= 0 || stream.Size > uint.MaxValue)
            {
                return (null, null, null);
            }

            var path = reserveImagePath(".png");
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return (path, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static async Task<string?> TryReadHtmlAsync(DataPackageView content)
    {
        try
        {
            return await content.GetHtmlFormatAsync();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryReadRtfAsync(DataPackageView content)
    {
        try
        {
            return await content.GetRtfAsync();
        }
        catch
        {
            return null;
        }
    }

    private static string? SourceApplication(DataPackageView content)
    {
        try
        {
            return content.Properties.ApplicationName;
        }
        catch
        {
            return "Windows Clipboard History";
        }
    }
}
