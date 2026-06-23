using Clip.Core;

namespace Clip.WindowsHistory;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 ||
                args[0].Equals("import-windows-history", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("import", StringComparison.OrdinalIgnoreCase))
            {
                return ImportWindowsHistory(args);
            }

            if (args[0].Equals("help", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                return 0;
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

    private static int ImportWindowsHistory(string[] args)
    {
        var maxItems = ParseMaxItems(args, defaultValue: 120);
        var store = ClipboardHistoryStore.OpenForCommandSurface();
        var imported = new ClipboardHistoryImportService(store, new WindowsClipboardHistorySource())
            .ImportAsync(maxItems)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine(imported);
        return 0;
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
        Console.WriteLine("Clip.WindowsHistory commands:");
        Console.WriteLine("  import-windows-history [--max <count>]");
    }
}
