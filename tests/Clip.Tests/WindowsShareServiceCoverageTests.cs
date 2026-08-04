using Clip.Shell;

namespace Clip.Tests;

public sealed class WindowsShareServiceCoverageTests
{
    [Fact]
    public void IsSupportedReportsTrueOnWindows10Plus()
    {
        // The project targets 10.0.19041; DataTransferManager has existed since Windows 10 RTM,
        // so on any machine these tests can run on the answer is true.
        Assert.True(WindowsShareService.IsSupported());
    }
}
