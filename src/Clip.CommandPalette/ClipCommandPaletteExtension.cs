using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace Clip.CommandPalette;

[ComVisible(true)]
[Guid("50E70174-F5A3-46F0-B1F1-3755EBD6A8B3")]
[ComDefaultInterface(typeof(IExtension))]
public sealed partial class ClipCommandPaletteExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;
    private ClipCommandsProvider? _provider;

    public ClipCommandPaletteExtension(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent;
    }

    public object? GetProvider(ProviderType providerType)
    {
        return providerType == ProviderType.Commands ? _provider ??= new ClipCommandsProvider() : null;
    }

    public void Dispose()
    {
        _extensionDisposedEvent.Set();
    }
}
