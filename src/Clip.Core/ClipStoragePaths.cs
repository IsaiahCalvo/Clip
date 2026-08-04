using System.Text.Json;

namespace Clip.Core;

public static class ClipStoragePaths
{
    private const string ClipboardFolderName = "Clipboard History";

    // Test seam: redirects the whole %LocalAppData%\Clip tree for the current async context so
    // tests can exercise the real load/save paths against a temp directory. AsyncLocal keeps
    // parallel test classes from seeing each other's override. Never set in production.
    internal static readonly AsyncLocal<string?> RootOverride = new();

    internal static string Root => RootOverride.Value ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip");

    public static string SettingsPath => Path.Combine(Root, "settings.json");

    public static string DefaultClipboardFolderPath => Path.Combine(Root, ClipboardFolderName);

    public static string WebView2UserDataFolderPath => Path.Combine(Root, "WebView2");

    public static string EffectiveClipboardFolderPath()
    {
        var configured = ConfiguredClipboardFolderPath();
        return string.IsNullOrWhiteSpace(configured) ? DefaultClipboardFolderPath : configured;
    }

    private static string? ConfiguredClipboardFolderPath()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return document.RootElement.TryGetProperty("ClipboardFolderPath", out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch
        {
            return null;
        }
    }
}
