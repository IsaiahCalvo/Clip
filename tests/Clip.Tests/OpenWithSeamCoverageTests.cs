using Clip.Core;
using Microsoft.Win32;

namespace Clip.Tests;

[CollectionDefinition("OpenWithStatics")]
public sealed class OpenWithStaticsCollection;

// Serialized with the other "Open with" test classes because these tests reset the
// process-wide Get-StartApps cache and swap its query seam.
[Collection("OpenWithStatics")]
public sealed class OpenWithSeamCoverageTests : IDisposable
{
    private readonly string _sandboxPath = @"Software\ClipTests\" + Guid.NewGuid().ToString("N");
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public OpenWithSeamCoverageTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_sandboxPath, throwOnMissingSubKey: false);
        }
        catch
        {
        }

        try
        {
            TestTemp.Delete(_tempRoot);
        }
        catch
        {
        }
    }

    [Fact]
    public void PackagedAppDiscoveryReturnsEmptyForBlankQueryOutput()
    {
        PackagedAppDiscovery.ResetCacheForTests();
        PackagedAppDiscovery.QueryOverride = () => "   ";
        try
        {
            Assert.Empty(PackagedAppDiscovery.GetStartApps());
        }
        finally
        {
            PackagedAppDiscovery.QueryOverride = null;
            PackagedAppDiscovery.ResetCacheForTests();
        }
    }

    [Fact]
    public void PackagedAppDiscoveryReturnsEmptyWhenQueryThrows()
    {
        PackagedAppDiscovery.ResetCacheForTests();
        PackagedAppDiscovery.QueryOverride = () => throw new InvalidOperationException("boom");
        try
        {
            Assert.Empty(PackagedAppDiscovery.GetStartApps());
        }
        finally
        {
            PackagedAppDiscovery.QueryOverride = null;
            PackagedAppDiscovery.ResetCacheForTests();
        }
    }

    [Fact]
    public void PackagedAppDiscoveryFiltersEntriesWithoutNameOrId()
    {
        PackagedAppDiscovery.ResetCacheForTests();
        PackagedAppDiscovery.QueryOverride = () =>
            "[{\"Name\":\"Good\",\"AppID\":\"G!App\"},{\"Name\":null,\"AppID\":\"x\"},{\"Name\":\"NoId\"}]";
        try
        {
            var app = Assert.Single(PackagedAppDiscovery.GetStartApps());
            Assert.Equal("Good", app.Name);
            Assert.Equal("G!App", app.AppUserModelId);
        }
        finally
        {
            PackagedAppDiscovery.QueryOverride = null;
            PackagedAppDiscovery.ResetCacheForTests();
        }
    }

    [Fact]
    public void AppPathRegistryAppsSkipsRootsWithoutAppPathsKey()
    {
        using var sandbox = Registry.CurrentUser.CreateSubKey(_sandboxPath)!;

        Assert.Empty(OpenWithAppDiscovery.AppPathRegistryApps(sandbox).ToList());
    }

    [Fact]
    public void GetAppsHandlesExtensionWhoseAssociationHasNoExecutable()
    {
        var apps = OpenWithAppDiscovery.GetApps(Path.Combine(_tempRoot, "x.zzqclip"));

        Assert.Contains(apps, app => app.IsDefault);
    }

    [Fact]
    public void GetAppsSkipsAssociationWhoseExecutableIsMissing()
    {
        // Register a throwaway extension in HKCU\Software\Classes whose open command points at
        // an executable that does not exist, so AddAssociatedApp takes the missing-executable return.
        var extension = ".zzqcov" + Guid.NewGuid().ToString("N")[..8];
        var progId = "ClipTests" + Guid.NewGuid().ToString("N")[..8];
        var ghostExe = Path.Combine(_tempRoot, "ghost-open-with.exe");
        try
        {
            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension))
            {
                extKey!.SetValue(null, progId);
            }

            using (var commandKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId + @"\shell\open\command"))
            {
                commandKey!.SetValue(null, $"\"{ghostExe}\" \"%1\"");
            }

            var apps = OpenWithAppDiscovery.GetApps(Path.Combine(_tempRoot, "sample" + extension));

            Assert.Contains(apps, app => app.IsDefault);
            Assert.DoesNotContain(apps, app => string.Equals(app.ExecutablePath, ghostExe, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + extension, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + progId, throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void PackagedAppLauncherThrowsForUnknownAppUserModelId()
    {
        var file = Path.Combine(_tempRoot, "target.txt");
        File.WriteAllText(file, "x");

        // The COM activation manager rejects an unknown AUMID with a failing HRESULT,
        // exercising the interop body and both finally blocks without launching anything.
        Assert.ThrowsAny<Exception>(() => PackagedAppLauncher.OpenFile("Clip.Tests.NoSuchPackage_zzzz!App", file));
    }
}
