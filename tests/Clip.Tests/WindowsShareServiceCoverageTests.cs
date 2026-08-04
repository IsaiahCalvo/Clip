using Clip.Shell;

namespace Clip.Tests;

public sealed class WindowsShareServiceCoverageTests
{
    [Fact]
    public void IsSupportedMatchesPlatformReport()
    {
        // DataTransferManager.IsSupported() is false on Windows Server (CI runners) and true on
        // client Windows 10+, so assert the pass-through contract rather than a fixed value.
        Assert.Equal(
            Windows.ApplicationModel.DataTransfer.DataTransferManager.IsSupported(),
            WindowsShareService.IsSupported());
    }
}
