using System;
using System.Diagnostics;
using System.Security.Principal;
using Clip.Shell;
using Xunit;

namespace Clip.Tests;

/// <summary>
/// The integrity gate can silently start refusing every paste if the token read breaks, so it is
/// worth one real assertion. 0 specifically means "could not tell", which is the value that would
/// make <c>TargetRejectsSyntheticInput</c> stop blocking anything at all.
///
/// The expected level is derived rather than hardcoded: a normal desktop process runs at medium
/// (0x2000), but an elevated one runs at high (0x3000) — and CI runners are elevated, which is how
/// a hardcoded 0x2000 here failed the v1.1.13 release build while passing on every dev machine.
/// </summary>
public class PasteIntegrityGateTests
{
    [Fact]
    public void ReadsThisProcessIntegrityLevel()
    {
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var expected = elevated ? 0x3000u : 0x2000u;

        var level = MainWindow.IntegrityLevelOfProcess(Process.GetCurrentProcess().Handle);

        Assert.NotEqual(0u, level);
        Assert.Equal(expected, level);
    }
}
