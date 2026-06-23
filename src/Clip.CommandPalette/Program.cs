using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace Clip.CommandPalette;

public static class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("-RegisterProcessAsComServer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var extensionDisposedEvent = new ManualResetEvent(false);
        var server = new Shmuelie.WinRTServer.ComServer();
        var extensionInstance = new ClipCommandPaletteExtension(extensionDisposedEvent);

        try
        {
            server.RegisterClass<ClipCommandPaletteExtension, IExtension>(() => extensionInstance);
            server.Start();
            extensionDisposedEvent.WaitOne();
        }
        finally
        {
            server.Stop();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
