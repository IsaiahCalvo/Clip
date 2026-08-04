using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Clip.Core;

/// <summary>
/// Pulls text out of a copied image so screenshots become searchable.
///
/// Uses the OCR engine built into Windows. It costs nothing to ship, runs entirely offline, and
/// needs no extra dependency — which matters because Clip promises clipboard data never leaves
/// the device. Every alternative worth considering wants either a cloud call or tens of megabytes
/// of models for a clipboard utility.
/// </summary>
public static class OcrTextExtractor
{
    // Undocumented but real: the engine returns nothing for very small crops, and upscaling
    // materially improves recognition of small anti-aliased UI text.
    private const int MinimumDimension = 40;

    private static readonly object Gate = new();
    private static OcrEngine? _engine;
    private static bool _engineResolved;

    /// <summary>
    /// True when this machine has an OCR language pack available. When false the feature cannot
    /// work at all and callers should not queue anything.
    /// </summary>
    public static bool IsAvailable => Engine() is not null;

    /// <summary>
    /// Returns the recognized text, or null if nothing could be read. Never throws: a failed
    /// recognition must not disturb clipboard capture.
    /// </summary>
    public static async Task<string?> TryExtractAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        var engine = Engine();
        if (engine is null)
        {
            return null;
        }

        try
        {
            using var bitmap = await LoadBitmapAsync(imagePath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            var text = result?.Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<SoftwareBitmap> LoadBitmapAsync(string imagePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        // A decodable image always has positive dimensions; anything the decoder rejects throws
        // and is turned into null by TryExtractAsync's catch.
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;

        var maxDimension = (int)OcrEngine.MaxImageDimension;
        var scale = 1.0;
        if (width < MinimumDimension || height < MinimumDimension)
        {
            scale = Math.Max((double)MinimumDimension / width, (double)MinimumDimension / height);
        }

        if (width * scale > maxDimension || height * scale > maxDimension)
        {
            scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        // The engine only accepts Bgra8 or Gray8; anything else throws.
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
    }

    private static OcrEngine? Engine()
    {
        lock (Gate)
        {
            if (_engineResolved)
            {
                return _engine;
            }

            _engineResolved = true;
            try
            {
                // Falls back to English because a profile language without an OCR pack returns
                // null here, which would otherwise disable the feature entirely.
                _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                    ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            }
            catch
            {
                _engine = null;
            }

            return _engine;
        }
    }
}
