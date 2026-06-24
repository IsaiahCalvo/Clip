using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clip.Core;

public static class ClipboardHistoryListCommand
{
    public const int DefaultLimit = 25;

    // Upper bound on a single list query. Raised from 100 to cover the standalone app's
    // largest finite history limit (1000) so a Command Palette user with a big history can
    // page all the way to the bottom. Unlimited histories are still capped here per query;
    // the page grows incrementally toward this ceiling rather than loading everything at once.
    public const int MaximumLimit = 1000;

    /// <summary>
    /// Resolves the effective list limit for a surface that pages incrementally, clamping the
    /// requested page size to the user's configured history limit (the F1
    /// <see cref="ClipSharedSettings"/> HistoryLimit; <c>null</c> means Unlimited) and then to
    /// <see cref="MaximumLimit"/>. Never returns less than 1.
    /// </summary>
    public static int ResolveLimit(int? historyLimit, int requested)
    {
        var ceiling = historyLimit is null
            ? MaximumLimit
            : Math.Clamp(historyLimit.Value, 1, MaximumLimit);
        return Math.Clamp(requested, 1, ceiling);
    }

    public static bool IsJsonRequest(string[] args) =>
        args.Skip(1).Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase));

    public static ClipboardHistoryListResult Create(ClipboardHistoryStore store, string[] args)
    {
        var options = ClipboardHistoryListOptions.Parse(args);
        return Create(store, options.Query, options.Limit);
    }

    public static ClipboardHistoryListResult Create(ClipboardHistoryStore store, string? query = null, int limit = DefaultLimit)
    {
        var stopwatch = Stopwatch.StartNew();
        limit = NormalizeLimit(limit);
        var items = store.QueryItemSummaries(query, limit);
        var source = string.IsNullOrWhiteSpace(query) ? "recent-summary" : "summary-search";
        var listItems = items.Select(ClipboardHistoryListItem.FromHistoryItem).ToList();
        stopwatch.Stop();

        return new ClipboardHistoryListResult(
            SchemaVersion: 1,
            Source: source,
            Query: string.IsNullOrWhiteSpace(query) ? null : query,
            Limit: limit,
            Count: items.Count,
            ElapsedMs: (int)stopwatch.ElapsedMilliseconds,
            Items: listItems);
    }

    public static string Serialize(ClipboardHistoryListResult result) =>
        JsonSerializer.Serialize(result, ClipboardHistoryListJsonContext.Default.ClipboardHistoryListResult);

    private sealed record ClipboardHistoryListOptions(string? Query, int Limit)
    {
        public static ClipboardHistoryListOptions Parse(string[] args)
        {
            var queryParts = new List<string>();
            var limit = DefaultLimit;

            for (var index = 1; index < args.Length; index++)
            {
                var arg = args[index];
                if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (arg.Equals("--limit", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                {
                    limit = ParseLimit(args[++index]);
                    continue;
                }

                if (arg.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase))
                {
                    limit = ParseLimit(arg["--limit=".Length..]);
                    continue;
                }

                if (arg.Equals("--query", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                {
                    queryParts.Add(args[++index]);
                    continue;
                }

                if (arg.StartsWith("--query=", StringComparison.OrdinalIgnoreCase))
                {
                    queryParts.Add(arg["--query=".Length..]);
                    continue;
                }

                queryParts.Add(arg);
            }

            var query = string.Join(' ', queryParts.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
            return new ClipboardHistoryListOptions(string.IsNullOrWhiteSpace(query) ? null : query, limit);
        }

        private static int ParseLimit(string value) =>
            int.TryParse(value, out var parsed) ? NormalizeLimit(parsed) : DefaultLimit;
    }

    private static int NormalizeLimit(int limit) => Math.Clamp(limit, 1, MaximumLimit);
}

public sealed record ClipboardHistoryListResult(
    int SchemaVersion,
    string Source,
    string? Query,
    int Limit,
    int Count,
    int ElapsedMs,
    IReadOnlyList<ClipboardHistoryListItem> Items);

public sealed record ClipboardHistoryListItem(
    string Id,
    string Kind,
    string Title,
    string Preview,
    IReadOnlyList<string> FilePaths,
    bool IsPinned,
    int PinOrder,
    bool HasOriginalFormatting,
    string? SourceApplication,
    long? AssetSizeBytes,
    int? CharacterCount,
    int? WordCount,
    DateTimeOffset LastUsedAt,
    DateTimeOffset LastCopiedAt,
    int CopyCount,
    int? ImageWidth,
    int? ImageHeight,
    string? DefaultActionId,
    IReadOnlyList<ClipboardHistoryListAction> Actions)
{
    public string? AssetPath { get; init; }

    // The path an "Open with…" picker should target for this row, derived from the lightweight
    // list item alone (no store hit) so the list-render path stays fast. Returns true only for
    // items that actually point at a file/folder on disk — Image (asset), Files (first path), and
    // path-like Text. Links are intentionally excluded (a URL has no app picker), matching the
    // standalone shell's "Open with" gating. The launch command re-resolves the authoritative
    // target via ClipboardItemLaunchCommand.GetOpenTarget at invoke time.
    public bool TryGetOpenWithTarget(out string targetPath)
    {
        targetPath = string.Empty;

        // Only items that expose an "open" action are openable at all.
        if (!Actions.Any(action => string.Equals(action.Id, "open", StringComparison.Ordinal)))
        {
            return false;
        }

        var candidate = Kind switch
        {
            nameof(ClipboardItemKind.Image) => AssetPath,
            nameof(ClipboardItemKind.Files) => FilePaths.Count > 0 ? FilePaths[0] : null,
            nameof(ClipboardItemKind.Text) => Preview,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        targetPath = candidate;
        return true;
    }

    public static ClipboardHistoryListItem FromHistoryItem(ClipboardHistoryItem item)
    {
        var title = string.IsNullOrWhiteSpace(item.CustomTitle) ? item.Preview : item.CustomTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = item.Kind.ToString();
        }

        var actions = ClipboardHistoryListAction.ForHistoryItem(item);

        return new ClipboardHistoryListItem(
            Id: item.Id,
            Kind: item.Kind.ToString(),
            Title: title,
            Preview: item.Preview,
            FilePaths: item.FilePaths,
            IsPinned: item.IsPinned,
            PinOrder: item.PinOrder,
            HasOriginalFormatting: item.HasOriginalFormatting,
            SourceApplication: item.SourceApplication,
            AssetSizeBytes: item.AssetSizeBytes,
            CharacterCount: item.CharacterCount,
            WordCount: item.WordCount,
            LastUsedAt: item.LastUsedAt,
            LastCopiedAt: item.LastCopiedAt,
            CopyCount: item.CopyCount,
            ImageWidth: item.ImageWidth,
            ImageHeight: item.ImageHeight,
            DefaultActionId: actions.FirstOrDefault()?.Id,
            Actions: actions)
        {
            AssetPath = item.AssetPath,
        };
    }
}

public sealed record ClipboardHistoryListAction(
    string Id,
    string Label,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool RequiresFullItem)
{
    public static IReadOnlyList<ClipboardHistoryListAction> ForHistoryItem(ClipboardHistoryItem item)
    {
        var actions = new List<ClipboardHistoryListAction>();

        if (CanPaste(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "paste",
                Label: "Paste",
                Executable: "Clip.Watcher.exe",
                Arguments: ["paste", item.Id],
                RequiresFullItem: true));
        }

        if (CanPastePlain(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "paste-plain",
                Label: "Paste as Plain Text",
                Executable: "Clip.Watcher.exe",
                Arguments: ["paste", item.Id],
                RequiresFullItem: true));
        }

        if (CanCopy(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "copy",
                Label: "Copy",
                Executable: "Clip.Watcher.exe",
                Arguments: ["copy", item.Id],
                RequiresFullItem: true));
        }

        if (CanAppend(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "append",
                Label: "Append to Clipboard",
                Executable: "Clip.Watcher.exe",
                Arguments: ["append", item.Id],
                RequiresFullItem: true));
        }

        actions.Add(new ClipboardHistoryListAction(
            Id: "rename",
            Label: "Rename",
            Executable: "Clip.Watcher.exe",
            Arguments: ["rename", item.Id],
            RequiresFullItem: false));

        if (item.Kind == ClipboardItemKind.Text)
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "edit-text",
                Label: "Edit Text",
                Executable: "Clip.Watcher.exe",
                Arguments: ["edit", item.Id],
                RequiresFullItem: true));
        }

        if (item.IsPinned)
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "unpin",
                Label: "Unpin",
                Executable: "Clip.Watcher.exe",
                Arguments: ["unpin", item.Id],
                RequiresFullItem: false));
            actions.Add(new ClipboardHistoryListAction(
                Id: "move-pin-up",
                Label: "Move Pin Up",
                Executable: "Clip.Watcher.exe",
                Arguments: ["up", item.Id],
                RequiresFullItem: false));
            actions.Add(new ClipboardHistoryListAction(
                Id: "move-pin-down",
                Label: "Move Pin Down",
                Executable: "Clip.Watcher.exe",
                Arguments: ["down", item.Id],
                RequiresFullItem: false));
        }
        else
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "pin",
                Label: "Pin",
                Executable: "Clip.Watcher.exe",
                Arguments: ["pin", item.Id],
                RequiresFullItem: false));
        }

        actions.Add(new ClipboardHistoryListAction(
            Id: "delete",
            Label: "Delete",
            Executable: "Clip.Watcher.exe",
            Arguments: ["delete", item.Id],
            RequiresFullItem: false));

        if (CanSaveAsFile(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "save-as-file",
                Label: "Save as File",
                Executable: "Clip.Watcher.exe",
                Arguments: ["save", item.Id],
                RequiresFullItem: true));
        }

        if (item.Kind == ClipboardItemKind.Files && item.FilePaths.Count > 0)
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "copy-path",
                Label: "Copy path",
                Executable: "Clip.Watcher.exe",
                Arguments: ["copy-path", item.Id],
                RequiresFullItem: false));
        }

        if (CanOpen(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "open",
                Label: "Open",
                // Watcher services the "open" verb itself (Program.cs case "open");
                // no separate Clip.Command helper is shipped anymore.
                Executable: "Clip.Watcher.exe",
                Arguments: ["open", item.Id],
                RequiresFullItem: true));
        }

        if (CanReveal(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "reveal",
                Label: "Show in File Explorer",
                // Watcher services the "reveal" verb itself (Program.cs case "reveal").
                Executable: "Clip.Watcher.exe",
                Arguments: ["reveal", item.Id],
                RequiresFullItem: true));
        }

        if (CanShare(item))
        {
            actions.Add(new ClipboardHistoryListAction(
                Id: "share",
                Label: "Share",
                Executable: string.Empty,
                Arguments: ["share", item.Id],
                RequiresFullItem: true));
        }

        return actions;
    }

    private static bool CanPaste(ClipboardHistoryItem item) => CanCopy(item);

    private static bool CanPastePlain(ClipboardHistoryItem item) =>
        item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color &&
        CanCopy(item);

    private static bool CanAppend(ClipboardHistoryItem item)
    {
        if (item.Kind is not (ClipboardItemKind.Text or ClipboardItemKind.Link))
        {
            return false;
        }

        return !string.IsNullOrEmpty(item.Text) || !string.IsNullOrEmpty(item.Preview);
    }

    private static bool CanShare(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            return !string.IsNullOrEmpty(item.Text) || !string.IsNullOrEmpty(item.Preview);
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            return !string.IsNullOrWhiteSpace(item.AssetPath);
        }

        return item.Kind == ClipboardItemKind.Files && item.FilePaths.Count > 0;
    }

    private static bool CanCopy(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            return !string.IsNullOrEmpty(item.Text) || !string.IsNullOrEmpty(item.Preview);
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            return !string.IsNullOrWhiteSpace(item.AssetPath);
        }

        return item.Kind == ClipboardItemKind.Files && item.FilePaths.Count > 0;
    }

    private static bool CanSaveAsFile(ClipboardHistoryItem item)
    {
        if (item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link or ClipboardItemKind.Color)
        {
            return true;
        }

        return item.Kind == ClipboardItemKind.Image && !string.IsNullOrWhiteSpace(item.AssetPath);
    }

    private static bool CanOpen(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Link)
        {
            return ClipboardLinkDetector.TryNormalize(TextPayload(item), out _);
        }

        if (item.Kind == ClipboardItemKind.Image)
        {
            return !string.IsNullOrWhiteSpace(item.AssetPath);
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            return item.FilePaths.Count > 0;
        }

        return item.Kind == ClipboardItemKind.Text && LooksLikePathText(TextPayload(item));
    }

    private static bool CanReveal(ClipboardHistoryItem item)
    {
        if (item.Kind == ClipboardItemKind.Image)
        {
            return !string.IsNullOrWhiteSpace(item.AssetPath);
        }

        if (item.Kind == ClipboardItemKind.Files)
        {
            return item.FilePaths.Count > 0;
        }

        return item.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link &&
            LooksLikePathText(TextPayload(item));
    }

    private static string TextPayload(ClipboardHistoryItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ? item.Text : item.Preview;

    private static bool LooksLikePathText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().Trim('"');
        if (!HasWindowsPathPrefix(trimmed))
        {
            return false;
        }

        foreach (var rawLine in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries))
        {
            var path = rawLine.Trim().Trim('"');
            if (HasWindowsPathPrefix(path) && Path.IsPathFullyQualified(path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasWindowsPathPrefix(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length >= 3 &&
            char.IsLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/'));
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClipboardHistoryListResult))]
[JsonSerializable(typeof(ClipboardHistoryListAction))]
internal partial class ClipboardHistoryListJsonContext : JsonSerializerContext
{
}
