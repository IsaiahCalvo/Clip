using System.ComponentModel;
using Clip.Core;

namespace Clip.Tests;

// Coverage-focused tests for the "Open with" plumbing: the associated-app early-outs in
// GetApps, the launcher error paths (which throw before any process/window is created),
// the shortcut-resolver catch path, association-query fallback, and the Get-StartApps
// process cache. Nothing here launches an app or mutates OS state.
[Collection("OpenWithStatics")]
public sealed class OpenWithAppDiscoveryCoverageTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public OpenWithAppDiscoveryCoverageTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void GetAppsHandlesTargetWithoutExtension()
    {
        // No extension -> AddAssociatedApp returns immediately; the synthetic default row remains.
        var apps = OpenWithAppDiscovery.GetApps(Path.Combine(_tempRoot, "README"));

        Assert.Contains(apps, app => app.IsDefault);
    }

    [Fact]
    public void GetAppsHandlesUnregisteredExtension()
    {
        // An extension no machine has an association for exercises the no-executable
        // fallback of AddAssociatedApp; the synthetic default row must always survive.
        var apps = OpenWithAppDiscovery.GetApps(Path.Combine(_tempRoot, "file.zz" + Guid.NewGuid().ToString("N")));

        Assert.Contains(apps, app => app.IsDefault);
    }

    [Fact]
    public void OpenWithPackagedAppThrowsFileNotFoundForMissingTarget()
    {
        var missing = Path.Combine(_tempRoot, "gone.png");
        var app = new AppChoice("Fake Store App", null, "Store app", AppUserModelId: "Fake.Package_abc123!App");

        Assert.Throws<FileNotFoundException>(() => OpenWithAppLauncher.OpenWith(missing, app));
    }

    [Fact]
    public void OpenWithDefaultAppThrowsWin32ForMissingTarget()
    {
        var missing = Path.Combine(_tempRoot, "gone.txt");
        var app = new AppChoice("Default app", null, "Windows", IsDefault: true);

        // Shell-open of a nonexistent file fails before any window can appear.
        Assert.Throws<Win32Exception>(() => OpenWithAppLauncher.OpenWith(missing, app));
    }

    [Fact]
    public void OpenWithExecutableThrowsWin32ForMissingExecutable()
    {
        var target = Path.Combine(_tempRoot, "doc.txt");
        File.WriteAllText(target, "content");
        var app = new AppChoice("Ghost Editor", Path.Combine(_tempRoot, "ghost-editor.exe"), "Installed app");

        Assert.Throws<Win32Exception>(() => OpenWithAppLauncher.OpenWith(target, app));
    }

    [Fact]
    public void ShortcutResolverReturnsNullForMissingLink()
    {
        Assert.Null(ShortcutResolver.Resolve(Path.Combine(_tempRoot, "missing.lnk")));
    }

    [Fact]
    public void AssociationQueryReturnsNullWhenQueryYieldsNoLength()
    {
        // An out-of-range ASSOCSTR makes AssocQueryString fail deterministically,
        // leaving the required length at zero -> the null early-out.
        Assert.Null(AssociationQuery.GetString(".txt", (AssociationString)int.MaxValue, "open"));
    }

    [Fact]
    public void PackagedAppDiscoveryCachesStartAppsPerProcess()
    {
        var first = PackagedAppDiscovery.GetStartApps();
        var second = PackagedAppDiscovery.GetStartApps();

        Assert.Same(first, second);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
        }
    }
}
