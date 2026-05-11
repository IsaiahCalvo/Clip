using System.Text.RegularExpressions;

namespace Clip.Core;

public static partial class ClipboardLinkDetector
{
    private static readonly HashSet<string> CommonTopLevelDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "biz", "co", "com", "dev", "edu", "gov", "io", "net", "org", "us",
    };

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "mailto",
    };

    public static bool IsLinkOrEmail(string? text)
    {
        return TryNormalize(text, out _);
    }

    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = TrimPunctuation(text.Trim());
        if (trimmed.Contains(' ', StringComparison.Ordinal) || trimmed.Contains('\n', StringComparison.Ordinal) || trimmed.Contains('\r', StringComparison.Ordinal))
        {
            return false;
        }

        if (EmailRegex().IsMatch(trimmed))
        {
            normalized = trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? trimmed : $"mailto:{trimmed}";
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) && AllowedSchemes.Contains(absolute.Scheme) && !string.IsNullOrWhiteSpace(absolute.Host))
        {
            normalized = trimmed;
            return true;
        }

        if (DomainRegex().IsMatch(trimmed) && HasLikelyTopLevelDomain(trimmed) && Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out var domainUri) && !string.IsNullOrWhiteSpace(domainUri.Host))
        {
            normalized = $"https://{trimmed}";
            return true;
        }

        return false;
    }

    private static string TrimPunctuation(string value)
    {
        return value.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '"', '\'');
    }

    private static bool HasLikelyTopLevelDomain(string value)
    {
        var host = value.Split('/', '?', '#')[0];
        var tld = host.Split('.').LastOrDefault();
        return tld is not null && CommonTopLevelDomains.Contains(tld);
    }

    [GeneratedRegex(@"^(mailto:)?[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^(www\.)?[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+\S*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DomainRegex();
}
