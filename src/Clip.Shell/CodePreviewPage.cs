using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Clip.Shell;

/// <summary>
/// Renders a source file as a scrollable, selectable, syntax-coloured page.
///
/// Highlighting is done here rather than by pulling in a JavaScript library, because the preview
/// must work offline and Clip ships no web assets. The rules are deliberately simple — comments,
/// strings, numbers, keywords — which covers what a preview is for: recognising a file at a
/// glance. It is not a code editor.
/// </summary>
internal static class CodePreviewPage
{
    private const int MaxCharacters = 400_000;

    private static readonly Dictionary<string, string[]> KeywordsByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = ["def", "class", "import", "from", "return", "if", "elif", "else", "for", "while", "try", "except", "finally", "with", "as", "lambda", "yield", "async", "await", "pass", "raise", "not", "and", "or", "in", "is", "None", "True", "False", "self", "global", "del"],
        ["shell"] = ["if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "function", "return", "export", "local", "source", "echo", "exit", "set", "unset", "read", "shift"],
        ["powershell"] = ["function", "param", "if", "else", "elseif", "foreach", "for", "while", "do", "switch", "return", "try", "catch", "finally", "throw", "begin", "process", "end", "filter", "class"],
        ["csharp"] = ["using", "namespace", "class", "struct", "record", "interface", "enum", "public", "private", "protected", "internal", "static", "readonly", "const", "void", "var", "new", "return", "if", "else", "for", "foreach", "while", "do", "switch", "case", "break", "continue", "try", "catch", "finally", "throw", "async", "await", "true", "false", "null", "this", "base", "override", "virtual", "abstract", "sealed", "partial"],
        ["javascript"] = ["function", "const", "let", "var", "return", "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "try", "catch", "finally", "throw", "class", "extends", "new", "async", "await", "import", "export", "from", "default", "true", "false", "null", "undefined", "this", "typeof", "of", "in"],
        ["sql"] = ["select", "from", "where", "join", "inner", "left", "right", "outer", "group", "order", "by", "having", "insert", "into", "values", "update", "set", "delete", "create", "table", "alter", "drop", "index", "view", "and", "or", "not", "null", "as", "on", "distinct", "union"],
    };

    public static string Build(string filePath, string backgroundHex, string textHex, string mutedHex, string accentHex)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var language = LanguageFor(extension);
        var raw = ReadCapped(filePath);
        var highlighted = Highlight(raw, language);
        var name = WebUtility.HtmlEncode(Path.GetFileName(filePath));

        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
              :root { color-scheme: dark; }
              html, body { margin: 0; height: 100%; background: {{backgroundHex}}; }
              body {
                color: {{textHex}};
                font-family: "Cascadia Mono", Consolas, "Courier New", monospace;
                font-size: 12.5px;
                line-height: 1.55;
              }
              .head {
                position: sticky;
                top: 0;
                padding: 8px 14px;
                background: {{backgroundHex}};
                border-bottom: 1px solid rgba(255,255,255,.08);
                font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
                font-size: 11.5px;
                color: {{mutedHex}};
                display: flex;
                justify-content: space-between;
                gap: 12px;
              }
              pre { margin: 0; padding: 12px 14px 24px; white-space: pre; overflow-x: auto; }
              /* Line numbers stay out of the selection so copying gives clean code. */
              .ln { counter-reset: line; }
              .ln > span.row { counter-increment: line; display: block; }
              .ln > span.row::before {
                content: counter(line);
                display: inline-block;
                width: 3.2em;
                margin-right: 1em;
                text-align: right;
                color: {{mutedHex}};
                opacity: .55;
                user-select: none;
              }
              .c  { color: #7FA97F; font-style: italic; }
              .s  { color: #D9A05B; }
              .n  { color: #9CC7E8; }
              .k  { color: {{accentHex}}; font-weight: 600; }
            </style>
            </head>
            <body>
              <div class="head"><span>{{name}}</span><span>{{language}}</span></div>
              <pre class="ln">{{highlighted}}</pre>
            </body>
            </html>
            """;
    }

    public static bool IsCodeFile(string extension) => LanguageFor(extension) != "text";

    private static string ReadCapped(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[MaxCharacters];
        var read = reader.ReadBlock(buffer, 0, MaxCharacters);
        var text = new string(buffer, 0, read);
        return read == MaxCharacters ? text + "\n\n… preview truncated …" : text;
    }

    private static string LanguageFor(string extension) => extension switch
    {
        ".py" or ".pyw" => "python",
        ".sh" or ".bash" or ".zsh" => "shell",
        ".ps1" or ".psm1" or ".psd1" => "powershell",
        ".cs" => "csharp",
        ".js" or ".ts" or ".jsx" or ".tsx" or ".mjs" or ".cjs" => "javascript",
        ".sql" => "sql",
        ".json" => "json",
        ".xml" or ".xaml" or ".csproj" or ".svg" => "xml",
        ".yml" or ".yaml" => "yaml",
        ".css" or ".scss" => "css",
        ".c" or ".h" or ".cpp" or ".hpp" or ".java" or ".go" or ".rs" or ".rb" or ".php" or ".swift" or ".kt" => "code",
        ".bat" or ".cmd" => "shell",
        ".ini" or ".toml" or ".conf" or ".env" => "config",
        _ => "text",
    };

    /// <summary>
    /// Escapes first, then colours. Doing it the other way round would let file contents inject
    /// markup into the page.
    /// </summary>
    private static string Highlight(string source, string language)
    {
        var escaped = WebUtility.HtmlEncode(source);
        var keywords = KeywordsByLanguage.TryGetValue(language, out var list) ? list : [];

        var builder = new StringBuilder();
        foreach (var line in escaped.Replace("\r\n", "\n").Split('\n'))
        {
            builder.Append("<span class=\"row\">").Append(HighlightLine(line, keywords, language)).Append("</span>");
        }

        return builder.ToString();
    }

    private static readonly Dictionary<string, Regex> PatternCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PatternGate = new();

    private static string HighlightLine(string line, string[] keywords, string language)
    {
        if (line.Length == 0)
        {
            return "&nbsp;";
        }

        // A whole-line comment is coloured as one span; anything inside it is left alone.
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith("//") || trimmed.StartsWith("--") || trimmed.StartsWith("&lt;!--") || trimmed.StartsWith('*'))
        {
            return $"<span class=\"c\">{line}</span>";
        }

        // One pass, never re-scanning. Running separate replacements in sequence meant later
        // passes matched the markup earlier passes had inserted — "class" is a Python keyword, so
        // the keyword pass happily rewrote the class="s" attributes of its own spans.
        return PatternFor(language, keywords).Replace(line, static match =>
        {
            if (match.Groups["str"].Success) return $"<span class=\"s\">{match.Value}</span>";
            if (match.Groups["kw"].Success) return $"<span class=\"k\">{match.Value}</span>";
            if (match.Groups["num"].Success) return $"<span class=\"n\">{match.Value}</span>";
            return match.Value;
        });
    }

    private static Regex PatternFor(string language, string[] keywords)
    {
        lock (PatternGate)
        {
            if (PatternCache.TryGetValue(language, out var cached))
            {
                return cached;
            }

            var keywordAlternation = keywords.Length == 0
                ? null
                : string.Join("|", Array.ConvertAll(keywords, Regex.Escape));

            // Strings first so a keyword inside a string stays a string. Entities appear because
            // the text is HTML-escaped before it gets here.
            var parts = new List<string>
            {
                @"(?<str>&quot;.*?&quot;|&#39;.*?&#39;)",
            };

            if (keywordAlternation is not null)
            {
                parts.Add($@"(?<kw>\b(?:{keywordAlternation})\b)");
            }

            parts.Add(@"(?<num>\b\d+(?:\.\d+)?\b)");

            var regex = new Regex(string.Join("|", parts), RegexOptions.Compiled, TimeSpan.FromSeconds(2));
            PatternCache[language] = regex;
            return regex;
        }
    }
}
