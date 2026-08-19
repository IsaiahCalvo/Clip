using System;
using System.Diagnostics;
using Clip.Shell;
using Xunit;

namespace Clip.Tests;

/// <summary>
/// The integrity gate can silently start refusing every paste if the token read breaks, so it is
/// worth one real assertion. A normal desktop process runs at medium integrity (0x2000); anything
/// else here means the SID walk is wrong, and 0 specifically means "could not tell", which is the
/// value that would make <c>TargetRejectsSyntheticInput</c> stop blocking anything at all.
/// </summary>
public class PasteIntegrityGateTests
{
    [Fact]
    public void ReadsMediumIntegrityForThisProcess()
    {
        var level = MainWindow.IntegrityLevelOfProcess(Process.GetCurrentProcess().Handle);

        Assert.NotEqual(0u, level);
        Assert.Equal(0x2000u, level);
    }
}
