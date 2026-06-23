using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clip.Core;

public enum ClipSharedOpenMode
{
    Standalone = 0,
    CommandPalette = 1,
}

public enum ClipSharedAppIcon
{
    Light = 0,
    Dark = 1,
}

public readonly record struct ClipSharedSettingsSnapshot(
    ClipSharedOpenMode OpenMode,
    ClipSharedAppIcon AppIcon,
    bool CheckForUpdatesOnStartup);

public static class ClipSharedSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static ClipSharedSettingsSnapshot Load()
    {
        if (!File.Exists(ClipStoragePaths.SettingsPath))
        {
            return DefaultSnapshot();
        }

        try
        {
            return LoadFromJson(File.ReadAllText(ClipStoragePaths.SettingsPath));
        }
        catch
        {
            return DefaultSnapshot();
        }
    }

    public static ClipSharedSettingsSnapshot LoadFromJson(string json)
    {
        var root = ParseRootObject(json);
        return new ClipSharedSettingsSnapshot(
            OpenMode: EnumValue(root, "OpenMode", ClipSharedOpenMode.Standalone),
            AppIcon: EnumValue(root, "AppIcon", ClipSharedAppIcon.Light),
            CheckForUpdatesOnStartup: BoolValue(root, "CheckForUpdatesOnStartup", true));
    }

    public static void SetOpenMode(ClipSharedOpenMode mode)
    {
        Update(json => SetOpenModeJson(json, mode));
    }

    public static string SetOpenModeJson(string json, ClipSharedOpenMode mode)
    {
        var root = ParseRootObject(json);
        root["OpenMode"] = (int)mode;
        return root.ToJsonString(JsonOptions);
    }

    public static void SetAppIcon(ClipSharedAppIcon icon)
    {
        Update(json => SetAppIconJson(json, icon));
    }

    public static string SetAppIconJson(string json, ClipSharedAppIcon icon)
    {
        var root = ParseRootObject(json);
        root["AppIcon"] = (int)icon;
        return root.ToJsonString(JsonOptions);
    }

    public static void SetCheckForUpdatesOnStartup(bool enabled)
    {
        Update(json => SetCheckForUpdatesOnStartupJson(json, enabled));
    }

    public static string SetCheckForUpdatesOnStartupJson(string json, bool enabled)
    {
        var root = ParseRootObject(json);
        root["CheckForUpdatesOnStartup"] = enabled;
        return root.ToJsonString(JsonOptions);
    }

    private static void Update(Func<string, string> updateJson)
    {
        var path = ClipStoragePaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "{}";
        File.WriteAllText(path, updateJson(existing));
    }

    private static ClipSharedSettingsSnapshot DefaultSnapshot() =>
        new(ClipSharedOpenMode.Standalone, ClipSharedAppIcon.Light, true);

    private static JsonObject ParseRootObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static TEnum EnumValue<TEnum>(JsonObject root, string name, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.Number && node.GetValue<int>() is var numeric)
        {
            return Enum.IsDefined(typeof(TEnum), numeric) ? (TEnum)Enum.ToObject(typeof(TEnum), numeric) : defaultValue;
        }

        return node.GetValueKind() == JsonValueKind.String &&
            Enum.TryParse<TEnum>(node.GetValue<string>(), ignoreCase: true, out var parsed)
                ? parsed
                : defaultValue;
    }

    private static bool BoolValue(JsonObject root, string name, bool defaultValue)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null)
        {
            return defaultValue;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }
}
