using System.Text.Json;

namespace Clip.Core;

// Persists the most recently used "Open with" apps per file extension to
// %LocalAppData%/Clip/open-with-recent.json. Both the WPF shell picker and the
// Command Palette picker read/write this same file, so a choice made in one
// surface appears as "Recent" in the other.
public static class OpenWithRecentStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "open-with-recent.json");

    public static IReadOnlyList<AppChoice> Load(string targetPath)
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return [];
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, List<RecentApp>>>(File.ReadAllText(StorePath)) ?? [];
            return data.TryGetValue(ExtensionKey(targetPath), out var recent)
                ? recent
                    .Where(app => !string.IsNullOrWhiteSpace(app.AppUserModelId) || (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath)))
                    .Select(app => new AppChoice(app.Name, app.ExecutablePath, "Recent", IsRecent: true, AppUserModelId: app.AppUserModelId))
                    .ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(string targetPath, AppChoice app)
    {
        if (app.IsDefault || (string.IsNullOrWhiteSpace(app.ExecutablePath) && string.IsNullOrWhiteSpace(app.AppUserModelId)))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var data = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<RecentApp>>>(File.ReadAllText(StorePath)) ?? []
                : [];
            var key = ExtensionKey(targetPath);
            if (!data.TryGetValue(key, out var recent))
            {
                recent = [];
                data[key] = recent;
            }

            var appKey = app.AppUserModelId ?? app.ExecutablePath ?? string.Empty;
            recent.RemoveAll(item => item.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, new RecentApp(app.Name, app.ExecutablePath, app.AppUserModelId));
            if (recent.Count > 8)
            {
                recent.RemoveRange(8, recent.Count - 8);
            }

            File.WriteAllText(StorePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Recent-app persistence is best effort; a failure here must never block launching.
        }
    }

    private static string ExtensionKey(string targetPath)
    {
        return Directory.Exists(targetPath) ? "<folder>" : Path.GetExtension(targetPath).ToLowerInvariant();
    }

    private sealed record RecentApp(string Name, string? ExecutablePath, string? AppUserModelId)
    {
        public string AppKey => AppUserModelId ?? ExecutablePath ?? string.Empty;
    }
}
