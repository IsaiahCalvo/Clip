using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Clip.Shell;

/// <summary>
/// Draws a small tile carrying a site's initial, used in place of a favicon.
///
/// Deliberately makes no network call. Fetching a real favicon would mean an outbound request
/// derived from clipboard contents, to hosts the user may never have visited — which contradicts
/// what PRIVACY.md promises. This gives links distinct, recognisable marks with nothing leaving
/// the machine: every link row otherwise shows the same generic chain glyph.
/// </summary>
internal static class DomainMonogram
{
    // Picked to stay legible on both the light and dark surfaces and to stay distinguishable
    // from each other for viewers with common colour-vision deficiencies.
    private static readonly System.Windows.Media.Color[] Palette =
    [
        System.Windows.Media.Color.FromRgb(0x4C, 0x6E, 0xF5),
        System.Windows.Media.Color.FromRgb(0x12, 0x8A, 0x7D),
        System.Windows.Media.Color.FromRgb(0xC0, 0x5E, 0x24),
        System.Windows.Media.Color.FromRgb(0x8A, 0x4F, 0xC4),
        System.Windows.Media.Color.FromRgb(0x2A, 0x7A, 0xB8),
        System.Windows.Media.Color.FromRgb(0xA8, 0x45, 0x6B),
        System.Windows.Media.Color.FromRgb(0x5B, 0x7A, 0x1F),
        System.Windows.Media.Color.FromRgb(0xB0, 0x7A, 0x14),
    ];

    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static ImageSource? For(string? url, int size)
    {
        var host = HostOf(url);
        if (host is null)
        {
            return null;
        }

        var key = $"{host}|{size}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var rendered = Render(host, size);
        lock (Gate)
        {
            Cache[key] = rendered;
        }

        return rendered;
    }

    /// <summary>
    /// Returns the registrable-ish host, dropping "www." so a site and its www alias share one
    /// mark. Anything that is not an http(s) URL gets no monogram.
    /// </summary>
    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    private static ImageSource Render(string host, int size)
    {
        var letter = char.ToUpperInvariant(host[0]).ToString();
        var color = Palette[StableIndex(host) % Palette.Length];

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var radius = size * 0.24;
            context.DrawRoundedRectangle(
                new SolidColorBrush(color),
                null,
                new Rect(0, 0, size, size),
                radius,
                radius);

            var text = new FormattedText(
                letter,
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(
                    new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI"),
                    FontStyles.Normal,
                    FontWeights.SemiBold,
                    FontStretches.Normal),
                size * 0.58,
                System.Windows.Media.Brushes.White,
                96);

            context.DrawText(text, new System.Windows.Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// A stable hash so a host always gets the same colour. String.GetHashCode is randomized per
    /// process, which would make the same site change colour every launch.
    /// </summary>
    private static int StableIndex(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value.ToLowerInvariant())
            {
                hash = (hash * 31) + c;
            }

            return Math.Abs(hash);
        }
    }
}
