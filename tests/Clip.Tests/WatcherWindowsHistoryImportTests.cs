using Clip.Watcher;

namespace Clip.Tests;

public sealed class WatcherWindowsHistoryImportTests
{
    [Fact]
    public void ParseImportCountUsesLastNumericLine()
    {
        var count = Program.ParseImportCount("starting\r\n3\r\n");

        Assert.Equal(3, count);
    }

    [Fact]
    public void ParseImportCountReturnsZeroWhenHelperHasNoCount()
    {
        var count = Program.ParseImportCount("Windows history import skipped");

        Assert.Equal(0, count);
    }
}
