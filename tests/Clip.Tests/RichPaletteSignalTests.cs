using Clip.Watcher;

namespace Clip.Tests;

public sealed class RichPaletteSignalTests
{
    [Fact]
    public void TrySignalRichPaletteSignalsExistingEvent()
    {
        var eventName = $@"Local\ClipShellShowPaletteTest_{Guid.NewGuid():N}";
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        Assert.True(Program.TrySignalRichPalette(eventName));
        Assert.True(signal.WaitOne(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TrySignalRichPaletteReturnsFalseWhenEventDoesNotExist()
    {
        var eventName = $@"Local\ClipShellShowPaletteMissing_{Guid.NewGuid():N}";

        Assert.False(Program.TrySignalRichPalette(eventName));
    }

    [Fact]
    public void TrySignalWatcherPaletteSignalsExistingEvent()
    {
        var eventName = $@"Local\ClipWatcherShowPaletteTest_{Guid.NewGuid():N}";
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        Assert.True(Program.TrySignalWatcherPalette(eventName));
        Assert.True(signal.WaitOne(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TrySignalWatcherPaletteReturnsFalseWhenEventDoesNotExist()
    {
        var eventName = $@"Local\ClipWatcherShowPaletteMissing_{Guid.NewGuid():N}";

        Assert.False(Program.TrySignalWatcherPalette(eventName));
    }
}
