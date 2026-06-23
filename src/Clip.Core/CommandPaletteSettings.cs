using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace Clip.Core;

public static class CommandPaletteSettings
{
    public const string ClipHistoryCommandId = "clip.history";
    public const string ClipHistoryTitle = "Clip History";
    private const int VirtualKeyV = 0x56;
    private const string CommandPalettePackageFamilyName = "Microsoft.CommandPalette_8wekyb3d8bbwe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        CommandPalettePackageFamilyName,
        "LocalState",
        "settings.json");

    public static CommandPaletteSettingsResult ConfigureClipHistoryHotkey(bool enableExternalReloadForApply = false)
    {
        var path = SettingsPath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new CommandPaletteSettingsResult(false, false, path, "Command Palette settings folder was not found.");
        }

        var existing = File.Exists(path) ? File.ReadAllText(path) : "{}";
        var updated = ConfigureClipHistoryHotkeyJson(existing, enableExternalReloadForApply);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return new CommandPaletteSettingsResult(true, false, path, null);
        }

        File.WriteAllText(path, updated);
        return new CommandPaletteSettingsResult(true, true, path, null);
    }

    public static string ConfigureClipHistoryHotkeyJson(string json, bool enableExternalReloadForApply = false)
    {
        var root = ParseRootObject(json);
        if (enableExternalReloadForApply)
        {
            root["AllowExternalReload"] = true;
        }

        var commandHotkeys = new JsonArray();
        if (root["CommandHotkeys"] is JsonArray existingHotkeys)
        {
            foreach (var node in existingHotkeys)
            {
                if (node is not JsonObject existingHotkey ||
                    IsClipHistoryBinding(existingHotkey) ||
                    IsAltVBinding(existingHotkey["Hotkey"] as JsonObject))
                {
                    continue;
                }

                commandHotkeys.Add(existingHotkey.DeepClone());
            }
        }

        JsonNode clipHistoryBinding = new JsonObject
        {
            ["CommandId"] = ClipHistoryCommandId,
            ["Hotkey"] = CreateAltVHotkey(),
        };
        commandHotkeys.Add(clipHistoryBinding);
        root["CommandHotkeys"] = commandHotkeys;

        return root.ToJsonString(JsonOptions);
    }

    public static bool SetExternalReloadAllowed(bool allowed)
    {
        var path = SettingsPath;
        if (!File.Exists(path))
        {
            return false;
        }

        var root = ParseRootObject(File.ReadAllText(path));
        root["AllowExternalReload"] = allowed;
        File.WriteAllText(path, root.ToJsonString(JsonOptions));
        return true;
    }

    public static bool RequestExternalReload()
    {
        try
        {
            Process.Start(new ProcessStartInfo("x-cmdpal://reload") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

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

    private static bool IsClipHistoryBinding(JsonObject hotkey)
    {
        return hotkey["CommandId"]?.GetValue<string>().Equals(ClipHistoryCommandId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsAltVBinding(JsonObject? hotkey)
    {
        return hotkey is not null &&
            BoolProperty(hotkey, "win") == false &&
            BoolProperty(hotkey, "ctrl") == false &&
            BoolProperty(hotkey, "alt") == true &&
            BoolProperty(hotkey, "shift") == false &&
            IntProperty(hotkey, "code") == VirtualKeyV;
    }

    private static JsonObject CreateAltVHotkey() => new()
    {
        ["win"] = false,
        ["ctrl"] = false,
        ["alt"] = true,
        ["shift"] = false,
        ["code"] = VirtualKeyV,
        ["key"] = "V",
    };

    private static bool? BoolProperty(JsonObject value, string name)
    {
        return value.TryGetPropertyValue(name, out var node) && node is not null && node.GetValueKind() == JsonValueKind.True
            ? true
            : value.TryGetPropertyValue(name, out node) && node is not null && node.GetValueKind() == JsonValueKind.False
                ? false
                : null;
    }

    private static int? IntProperty(JsonObject value, string name)
    {
        return value.TryGetPropertyValue(name, out var node) && node is not null && node.GetValueKind() == JsonValueKind.Number && node.GetValue<int>() is var result
            ? result
            : null;
    }
}

public readonly record struct CommandPaletteSettingsResult(bool Available, bool Changed, string Path, string? Message);
