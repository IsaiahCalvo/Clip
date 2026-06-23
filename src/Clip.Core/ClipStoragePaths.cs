using System.Text.Json;

namespace Clip.Core;

public static class ClipStoragePaths
{
    private const string ClipboardFolderName = "Clipboard History";

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "settings.json");

    public static string DefaultClipboardFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        ClipboardFolderName);

    public static string WebView2UserDataFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "WebView2");

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
