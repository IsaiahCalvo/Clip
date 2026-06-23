using System.Text.Json;
using System.Text.Json.Nodes;
using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

internal sealed partial class ClipItemPreviewCard : FormContent
{
    private const int TextPreviewLimit = 12_000;
    private const int FactValueLimit = 220;

    private readonly ClipboardHistoryItem _fullItem;
    private readonly ClipboardHistoryListItem _listItem;
    private readonly ClipboardHistoryStore _store;
    private readonly Action _afterHistoryMutation;

    public ClipItemPreviewCard(
        ClipboardHistoryItem fullItem,
        ClipboardHistoryListItem listItem,
        ClipboardHistoryStore store,
        Action afterHistoryMutation)
    {
        _fullItem = fullItem;
        _listItem = listItem;
        _store = store;
        _afterHistoryMutation = afterHistoryMutation;
        TemplateJson = BuildTemplate(fullItem, listItem);
    }

    private ClipItemPreviewCard(string title, string message)
    {
        _fullItem = new ClipboardHistoryItem();
        _listItem = ClipboardHistoryListItem.FromHistoryItem(_fullItem);
        _store = null!;
        _afterHistoryMutation = () => { };
        TemplateJson = $$"""
{
  "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
  "type": "AdaptiveCard",
  "version": "1.6",
  "body": [
    {
      "type": "TextBlock",
      "text": {{JsonString(title)}},
      "size": "Large",
      "weight": "Bolder",
      "wrap": true,
      "style": "heading"
    },
    {
      "type": "TextBlock",
      "text": {{JsonString(message)}},
      "wrap": true
    }
  ]
}
""";
    }

    public static ClipItemPreviewCard Unavailable() =>
        new("Item unavailable", "This clipboard item no longer exists.");

    public override ICommandResult SubmitForm(string payload)
    {
        var actionId = ActionIdFrom(payload);
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return CommandResult.KeepOpen();
        }

        var action = _listItem.Actions.FirstOrDefault(candidate =>
            candidate.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
        if (action is null)
        {
            ShowError("That action is not available for this clipboard item.");
            return CommandResult.KeepOpen();
        }

        return new ClipHistoryActionCommand(action, _store, _afterHistoryMutation).Invoke();
    }

    private static string BuildTemplate(ClipboardHistoryItem fullItem, ClipboardHistoryListItem listItem)
    {
        var summary = $"{fullItem.Kind} - {SourceText(fullItem)}";
        var previewItems = BuildPreviewItems(fullItem);
        var facts = BuildFacts(fullItem);
        var actions = BuildActions(listItem);
        var actionsBlock = string.IsNullOrWhiteSpace(actions)
            ? string.Empty
            : $"""
,
  "actions": [
{actions}
  ]
""";

        return $$"""
{
  "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
  "type": "AdaptiveCard",
  "version": "1.6",
  "body": [
    {
      "type": "TextBlock",
      "text": {{JsonString(TitleFor(fullItem, listItem))}},
      "size": "Large",
      "weight": "Bolder",
      "wrap": true,
      "style": "heading"
    },
    {
      "type": "TextBlock",
      "text": {{JsonString(summary)}},
      "isSubtle": true,
      "spacing": "None",
      "wrap": true
    },
    {
      "type": "ColumnSet",
      "spacing": "Medium",
      "columns": [
        {
          "type": "Column",
          "width": "stretch",
          "items": [
{{previewItems}}
          ]
        },
        {
          "type": "Column",
          "width": "auto",
          "items": [
            {
              "type": "TextBlock",
              "text": "Information",
              "weight": "Bolder",
              "wrap": true
            },
            {
              "type": "FactSet",
              "facts": [
{{facts}}
              ]
            }
          ]
        }
      ]
    }
  ]{{actionsBlock}}
}
""";
    }

    private static string BuildPreviewItems(ClipboardHistoryItem fullItem)
    {
        if (fullItem.Kind == ClipboardItemKind.Image &&
            !string.IsNullOrWhiteSpace(fullItem.AssetPath) &&
            File.Exists(fullItem.AssetPath))
        {
            return $$"""
            {
              "type": "TextBlock",
              "text": "Preview",
              "weight": "Bolder",
              "wrap": true
            },
            {
              "type": "TextBlock",
              "text": "Image preview is shown above.",
              "wrap": true,
              "isSubtle": true
            }
""";
        }

        return $$"""
            {
              "type": "TextBlock",
              "text": "Preview",
              "weight": "Bolder",
              "wrap": true
            },
            {
              "type": "TextBlock",
              "text": {{JsonString(PreviewText(fullItem))}},
              "wrap": true,
              "fontType": "Monospace",
              "maxLines": 30
            }
""";
    }

    private static string BuildFacts(ClipboardHistoryItem fullItem)
    {
        return string.Join(
            $",{Environment.NewLine}",
            DetailRows(fullItem).Select(row =>
                $$"""
                {
                  "title": {{JsonString(row.Key + ":")}},
                  "value": {{JsonString(TrimPreservingLines(row.Value, FactValueLimit))}}
                }
"""));
    }

    private static string BuildActions(ClipboardHistoryListItem listItem)
    {
        // Curated quick actions for the inline card, in priority order. Paste is listed
        // first so it is the default action (Enter) on the preview, matching the list row
        // and the standalone app. The full action set is reachable via the preview page's
        // context commands (Ctrl+K). Delete is rendered with the destructive style.
        string[] order = { "paste", "copy", "pin", "unpin", "open", "reveal", "delete" };

        var inlineActions = order
            .Select(id => listItem.Actions.FirstOrDefault(action =>
                action.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(action => action is not null)
            .Select(action =>
            {
                var style = action!.Id.Equals("delete", StringComparison.OrdinalIgnoreCase)
                    ? $"{Environment.NewLine}      \"style\": \"destructive\","
                    : string.Empty;
                return $$"""
    {
      "type": "Action.Submit",
      "title": {{JsonString(action.Label)}},{{style}}
      "data": {
        "actionId": {{JsonString(action.Id)}}
      }
    }
""";
            });

        return string.Join($",{Environment.NewLine}", inlineActions);
    }

    private static string PreviewText(ClipboardHistoryItem fullItem)
    {
        if (fullItem.FilePaths.Count > 0)
        {
            var paths = string.Join(Environment.NewLine, fullItem.FilePaths);
            if (FilePreview.TryReadTextExcerpt(fullItem.FilePaths, TextPreviewLimit, out var excerpt))
            {
                return $"{paths}{Environment.NewLine}{Environment.NewLine}{excerpt}";
            }

            return paths;
        }

        var text = fullItem.Text ?? fullItem.Preview ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(No text preview available)";
        }

        return TrimPreservingLines(text, TextPreviewLimit);
    }

    private static IReadOnlyList<DetailRow> DetailRows(ClipboardHistoryItem fullItem)
    {
        var rows = new List<DetailRow>
        {
            new("Type", fullItem.Kind.ToString()),
            new("Source", SourceText(fullItem)),
            new("Pinned", fullItem.IsPinned ? "Yes" : "No"),
            new("Last used", fullItem.LastUsedAt.LocalDateTime.ToString("g")),
            new("Copied", fullItem.LastCopiedAt.LocalDateTime.ToString("g")),
            new("Times copied", fullItem.CopyCount.ToString("N0")),
        };

        if (fullItem.Kind is ClipboardItemKind.Text or ClipboardItemKind.Link)
        {
            rows.Add(new DetailRow("Saved format", fullItem.HasOriginalFormatting ? "Plain text + formatting" : "Plain text"));
        }

        if (fullItem.FilePaths.Count > 0)
        {
            rows.Add(new DetailRow(fullItem.FilePaths.Count == 1 ? "File path" : "Files", fullItem.FilePaths[0]));
        }

        if (fullItem.CharacterCount is > 0)
        {
            rows.Add(new DetailRow("Characters", fullItem.CharacterCount.Value.ToString("N0")));
        }

        if (fullItem.WordCount is > 0)
        {
            rows.Add(new DetailRow("Words", fullItem.WordCount.Value.ToString("N0")));
        }

        if (fullItem.Kind == ClipboardItemKind.Color)
        {
            rows.Add(new DetailRow("Hex", fullItem.Preview));
        }

        if (fullItem.ImageWidth is > 0 && fullItem.ImageHeight is > 0)
        {
            rows.Add(new DetailRow("Dimensions", $"{fullItem.ImageWidth} x {fullItem.ImageHeight}"));
        }

        if (fullItem.AssetSizeBytes is > 0)
        {
            rows.Add(new DetailRow(fullItem.Kind == ClipboardItemKind.Image ? "Image size" : "Size", FormatBytes(fullItem.AssetSizeBytes.Value)));
        }

        return rows;
    }

    private static string TitleFor(ClipboardHistoryItem fullItem, ClipboardHistoryListItem listItem)
    {
        if (!string.IsNullOrWhiteSpace(fullItem.CustomTitle))
        {
            return ClipText.TrimForDisplay(fullItem.CustomTitle, 120);
        }

        return ClipText.TrimForDisplay(listItem.Title, 120);
    }

    private static string SourceText(ClipboardHistoryItem fullItem) =>
        string.IsNullOrWhiteSpace(fullItem.SourceApplication) ? "Unknown" : fullItem.SourceApplication;

    private static string? ActionIdFrom(string payload)
    {
        try
        {
            var formInput = JsonNode.Parse(payload)?.AsObject();
            return formInput?["actionId"]?.ToString() ??
                formInput?["data"]?["actionId"]?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TrimPreservingLines(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 30)] + $"{Environment.NewLine}...[truncated]";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string JsonString(string? value) =>
        JsonSerializer.Serialize(value ?? string.Empty, ClipCommandPaletteJsonContext.Default.String);

    private static void ShowError(string message)
    {
        new ToastStatusMessage(new StatusMessage
        {
            Message = message,
            State = MessageState.Error,
        }).Show();
    }

    private readonly record struct DetailRow(string Key, string Value);
}
