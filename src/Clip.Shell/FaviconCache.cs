using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clip.Shell;

/// <summary>
/// Fetches and caches a site's real icon for link items, the way Raycast does: request the page,
/// read the icon out of its HTML head, fall back to /favicon.ico.
///
/// One request per host, cached on disk forever after, including failures — so a dead host is not
/// retried on every render. Requests go to the site itself and carry no cookies and no path or
/// query from the copied URL, only the host.
/// </summary>
internal static class FaviconCache
{
    private const int MaxBytes = 512 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<string, ImageSource?> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<Action<ImageSource?>>> Waiting = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Clip/1.0 (+https://github.com/Kal-Voe/Clip)");
        return client;
    });

    private static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "Cache",
        "link-icons");

    public static string? HostOf(string? url)
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
            string.IsNullOrWhiteSpace(uri.Host) ||
            !uri.Host.Contains('.'))
        {
            return null;
        }

        // Skip anything that is not a public host. Fetching these would poke at the user's own
        // network rather than a website.
        if (IPAddress.TryParse(uri.Host, out var ip) &&
            (IPAddress.IsLoopback(ip) || IsPrivate(ip)))
        {
            return null;
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? null : uri.Host;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 &&
            (bytes[0] == 10 ||
             (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
             (bytes[0] == 192 && bytes[1] == 168) ||
             (bytes[0] == 169 && bytes[1] == 254));
    }

    /// <summary>
    /// Builds the browser-icon snapshot ahead of time on a background thread. Without this the
    /// first link row pays the one-off cost of reading the browsers' favicon databases.
    /// </summary>
    public static void Warm()
    {
        Task.Run(() =>
        {
            try
            {
                BrowserFaviconStore.TryGet("example.com", out _);
            }
            catch
            {
                // Warming is an optimisation; a failure here just means the first lookup pays.
            }
        });
    }

    /// <summary>Cache-only lookup so the UI thread never blocks on the network.</summary>
    public static bool TryGetCached(string host, out ImageSource? icon)
    {
        if (Memory.TryGetValue(host, out icon))
        {
            return true;
        }

        var path = CachePath(host);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            // A zero-length file is a remembered failure.
            icon = new FileInfo(path).Length == 0 ? null : Decode(File.ReadAllBytes(path));
        }
        catch
        {
            icon = null;
        }

        Memory[host] = icon;
        return true;
    }

    public static void FetchAsync(string host, Action<ImageSource?> onResolved)
    {
        // Many rows share one host, so a list of twenty GitHub links must make one request — but
        // every one of those rows still needs the answer. Collect the callbacks and notify all of
        // them, otherwise only the first row ever swaps its icon.
        var callbacks = Waiting.GetOrAdd(host, _ => new List<Action<ImageSource?>>());
        lock (callbacks)
        {
            callbacks.Add(onResolved);
        }

        if (InFlight.ContainsKey(host))
        {
            return;
        }

        var task = Task.Run(async () =>
        {
            ImageSource? icon = null;
            try
            {
                var bytes = await DownloadAsync(host).ConfigureAwait(false);
                icon = bytes is null ? null : Decode(bytes);
                Persist(host, icon is null ? Array.Empty<byte>() : bytes!);
            }
            catch
            {
                Persist(host, Array.Empty<byte>());
            }

            Memory[host] = icon;
            InFlight.TryRemove(host, out _);

            if (Waiting.TryRemove(host, out var pending))
            {
                Action<ImageSource?>[] snapshot;
                lock (pending)
                {
                    snapshot = pending.ToArray();
                }

                foreach (var callback in snapshot)
                {
                    try
                    {
                        callback(icon);
                    }
                    catch
                    {
                        // One dead row must not stop the others updating.
                    }
                }
            }
        });

        InFlight[host] = task;
    }

    private static async Task<byte[]?> DownloadAsync(string host)
    {
        // Zero-network path first. For any site the user has actually visited, the browser
        // already downloaded this icon, so there is nothing to ask the site for.
        if (BrowserFaviconStore.TryGet(host, out var local) && local is { Length: > 0 })
        {
            return local;
        }

        foreach (var candidate in await CandidatesAsync(host).ConfigureAwait(false))
        {
            var bytes = await GetAsync(candidate).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                return bytes;
            }
        }

        return null;
    }

    private static async Task<List<Uri>> CandidatesAsync(string host)
    {
        var candidates = new List<Uri>();
        var root = new Uri($"https://{host}/");

        try
        {
            using var response = await Client.Value
                .GetAsync(root, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var html = await ReadCappedAsync(response).ConfigureAwait(false);
                foreach (var href in IconHrefs(html))
                {
                    if (Uri.TryCreate(response.RequestMessage?.RequestUri ?? root, href, out var resolved))
                    {
                        candidates.Add(resolved);
                    }
                }
            }
        }
        catch
        {
            // Fall through to the conventional locations.
        }

        candidates.Add(new Uri(root, "/favicon.ico"));
        candidates.Add(new Uri(root, "/apple-touch-icon.png"));
        return candidates;
    }

    private static IEnumerable<string> IconHrefs(string html)
    {
        var head = html.Length > 60_000 ? html[..60_000] : html;
        var matches = Regex.Matches(
            head,
            "<link[^>]+>",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        var found = new List<(int Size, string Href)>();
        foreach (Match tag in matches)
        {
            var rel = Attribute(tag.Value, "rel");
            if (rel is null || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = Attribute(tag.Value, "href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            // Prefer the largest declared size; apple-touch-icon is usually the crispest.
            var sizes = Attribute(tag.Value, "sizes");
            var size = 0;
            if (sizes is not null)
            {
                var digits = Regex.Match(sizes, @"(\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (digits.Success)
                {
                    int.TryParse(digits.Groups[1].Value, out size);
                }
            }

            if (rel.Contains("apple-touch", StringComparison.OrdinalIgnoreCase))
            {
                size = Math.Max(size, 180);
            }

            found.Add((size, href!));
        }

        return found.OrderByDescending(entry => entry.Size).Select(entry => entry.Href);
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(
            tag,
            name + @"\s*=\s*[""']([^""']*)[""']",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<byte[]?> GetAsync(Uri uri)
    {
        try
        {
            using var response = await Client.Value
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes)
                {
                    return null;
                }
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > 200_000)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        // SVG has no BitmapDecoder; rasterize it through the SVG library already referenced here.
        if (LooksLikeSvg(bytes))
        {
            try
            {
                var document = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(Encoding.UTF8.GetString(bytes));
                using var raster = document.Draw(64, 64);
                using var memory = new MemoryStream();
                raster.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                return DecodeBitmap(memory.ToArray());
            }
            catch
            {
                return null;
            }
        }

        return DecodeBitmap(bytes);
    }

    private static ImageSource? DecodeBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            // .ico files carry several sizes; take the biggest.
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            if (frame is null)
            {
                return null;
            }

            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart();
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    private static void Persist(string host, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllBytes(CachePath(host), bytes);
        }
        catch
        {
            // Cache is an optimisation; failing to write it is not fatal.
        }
    }

    /// <summary>
    /// Hashed filename so the cache directory is not a plaintext list of sites the user copied.
    /// </summary>
    private static string CachePath(string host)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant())));
        return Path.Combine(CacheDirectory, hash[..32] + ".bin");
    }

    public static void Clear()
    {
        Memory.Clear();
        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
