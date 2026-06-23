using Clip.Core;
using System.Diagnostics;

namespace Clip.Command;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                ListItems(args);
                return 0;
            }

            if (args[0].Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                return OpenItem(args);
            }

            if (args[0].Equals("reveal", StringComparison.OrdinalIgnoreCase))
            {
                return RevealItem(args);
            }

            if (args[0].Equals("import-windows-history", StringComparison.OrdinalIgnoreCase))
            {
                return ImportWindowsHistory(args);
            }

            if (args[0].Equals("configure-command-palette", StringComparison.OrdinalIgnoreCase))
            {
                return ConfigureCommandPalette();
            }

            PrintHelp();
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void ListItems(string[] args)
    {
        var store = ClipboardHistoryStore.OpenForCommandSurface();

        if (ClipboardHistoryListCommand.IsJsonRequest(args))
        {
            Console.WriteLine(ClipboardHistoryListCommand.Serialize(ClipboardHistoryListCommand.Create(store, args)));
            return;
        }

        foreach (var item in store.QueryItemSummaries())
        {
            var pin = item.IsPinned ? "PIN" : "   ";
            Console.WriteLine($"{pin} {item.Id} [{item.Kind}] {item.Preview}");
        }
    }

    private static int OpenItem(string[] args)
    {
        var id = args.Length > 1 ? args[1] : string.Empty;
        var item = ClipboardHistoryStore.OpenForCommandSurface().GetItem(id) ??
            throw new InvalidOperationException("Clipboard item not found.");
        var startInfo = ClipboardItemLaunchCommand.CreateOpenStartInfo(item, args.Length > 2 ? args[2] : null);
        if (startInfo is null)
        {
            return 2;
        }

        System.Diagnostics.Process.Start(startInfo);
        return 0;
    }

    private static int RevealItem(string[] args)
    {
        var id = args.Length > 1 ? args[1] : string.Empty;
        var item = ClipboardHistoryStore.OpenForCommandSurface().GetItem(id) ??
            throw new InvalidOperationException("Clipboard item not found.");
        var startInfo = ClipboardItemLaunchCommand.CreateRevealStartInfo(item);
        if (startInfo is null)
        {
            return 2;
        }

        System.Diagnostics.Process.Start(startInfo);
        return 0;
    }

    private static int ImportWindowsHistory(string[] args)
    {
        var maxItems = ParseMaxItems(args, defaultValue: 120);
        var helper = FindWindowsHistoryExecutable();
        if (helper is null)
        {
            Console.Error.WriteLine("Clip.WindowsHistory.exe was not found.");
            return 3;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(helper) ?? AppContext.BaseDirectory,
            },
        };
        process.StartInfo.ArgumentList.Add("import-windows-history");
        process.StartInfo.ArgumentList.Add("--max");
        process.StartInfo.ArgumentList.Add(maxItems.ToString());

        if (!process.Start())
        {
            return 3;
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.Write(output);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.Write(error);
        }

        return process.ExitCode;
    }

    private static int ConfigureCommandPalette()
    {
        var result = CommandPaletteSettings.ConfigureClipHistoryHotkey(enableExternalReloadForApply: true);
        if (!result.Available)
        {
            Console.Error.WriteLine(result.Message ?? "Command Palette settings were not found.");
            return 2;
        }

        if (CommandPaletteSettings.RequestExternalReload())
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            CommandPaletteSettings.SetExternalReloadAllowed(false);
        }

        Console.WriteLine(result.Changed
            ? $"Configured Alt+V for {CommandPaletteSettings.ClipHistoryTitle}."
            : $"Alt+V is already configured for {CommandPaletteSettings.ClipHistoryTitle}.");
        return 0;
    }

    private static string? FindWindowsHistoryExecutable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Clip.WindowsHistory.exe");
        if (File.Exists(local))
        {
            return local;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory, "Clip.WindowsHistory.exe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return null;
    }

    private static int ParseMaxItems(string[] args, int defaultValue)
    {
        for (var index = 1; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.Equals("--max", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return ParsePositiveInt(args[index + 1], defaultValue);
            }

            if (arg.StartsWith("--max=", StringComparison.OrdinalIgnoreCase))
            {
                return ParsePositiveInt(arg["--max=".Length..], defaultValue);
            }
        }

        return defaultValue;
    }

    private static int ParsePositiveInt(string value, int defaultValue) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;

    private static void PrintHelp()
    {
        Console.WriteLine("Clip.Command commands:");
        Console.WriteLine("  list [--json] [--limit <count>] [--query <text>]");
        Console.WriteLine("  open <id> [app path]");
        Console.WriteLine("  reveal <id>");
        Console.WriteLine("  import-windows-history [--max <count>]");
        Console.WriteLine("  configure-command-palette");
    }
}
