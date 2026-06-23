using Clip.Core;
using Microsoft.Win32;

namespace Clip.Tests;

public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _valueName = "Clip.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void DefaultStartupPreferenceIsEnabled()
    {
        Assert.True(StartupRegistration.DefaultEnabled);
    }

    [Fact]
    public void SetEnabledWritesAndRemovesStartupValue()
    {
        var fakeExe = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"), "Clip.Shell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeExe)!);
        File.WriteAllText(fakeExe, "");

        StartupRegistration.SetEnabled(true, _valueName, fakeExe);

        Assert.True(StartupRegistration.IsEnabled(_valueName));
        Assert.Equal($"\"{fakeExe}\"", StartupRegistration.CurrentValue(_valueName));

        StartupRegistration.SetEnabled(false, _valueName, fakeExe);

        Assert.False(StartupRegistration.IsEnabled(_valueName));
        Assert.Null(StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void SetEnabledPrefersWatcherHostWhenAvailable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
        var shellExe = Path.Combine(folder, "Clip.exe");
        var watcherExe = Path.Combine(folder, "Clip.Watcher.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(shellExe, "");
        File.WriteAllText(watcherExe, "");

        StartupRegistration.SetEnabled(true, _valueName, shellExe);

        Assert.Equal($"\"{watcherExe}\" watch", StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void SetEnabledAddsWatcherArgumentsWhenCurrentExecutableIsWatcher()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
        var watcherExe = Path.Combine(folder, "Clip.Watcher.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(watcherExe, "");

        StartupRegistration.SetEnabled(true, _valueName, watcherExe);

        Assert.Equal($"\"{watcherExe}\" watch", StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateToLightweightHostRewritesLegacyShellStartupValue()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
        var shellExe = Path.Combine(folder, "Clip.exe");
        var watcherExe = Path.Combine(folder, "Clip.Watcher.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(shellExe, "");
        File.WriteAllText(watcherExe, "");
        WriteStartupValue($"\"{shellExe}\"");

        var migrated = StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, shellExe);

        Assert.True(migrated);
        Assert.Equal($"\"{watcherExe}\" watch", StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateToLightweightHostLeavesDisabledStartupAlone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
        var shellExe = Path.Combine(folder, "Clip.exe");
        var watcherExe = Path.Combine(folder, "Clip.Watcher.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(shellExe, "");
        File.WriteAllText(watcherExe, "");

        var migrated = StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, shellExe);

        Assert.False(migrated);
        Assert.Null(StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateToLightweightHostLeavesUnknownStartupValueAlone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));
        var shellExe = Path.Combine(folder, "Clip.exe");
        var watcherExe = Path.Combine(folder, "Clip.Watcher.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(shellExe, "");
        File.WriteAllText(watcherExe, "");
        var unknown = "\"C:\\Tools\\OtherApp.exe\"";
        WriteStartupValue(unknown);

        var migrated = StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, shellExe);

        Assert.False(migrated);
        Assert.Equal(unknown, StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateToLightweightHostLeavesExistingWatcherValueAlone()
    {
        var installedFolder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"), "Installed");
        var repoFolder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"), "Repo");
        var installedWatcherExe = Path.Combine(installedFolder, "Clip.Watcher.exe");
        var repoShellExe = Path.Combine(repoFolder, "Clip.exe");
        var repoWatcherExe = Path.Combine(repoFolder, "Clip.Watcher.exe");
        Directory.CreateDirectory(installedFolder);
        Directory.CreateDirectory(repoFolder);
        File.WriteAllText(installedWatcherExe, "");
        File.WriteAllText(repoShellExe, "");
        File.WriteAllText(repoWatcherExe, "");
        var installedStartup = $"\"{installedWatcherExe}\" watch";
        WriteStartupValue(installedStartup);

        var migrated = StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, repoShellExe);

        Assert.False(migrated);
        Assert.Equal(installedStartup, StartupRegistration.CurrentValue(_valueName));
    }

    [Fact]
    public void MigrateToLightweightHostUsesLegacyStartupFolderInsteadOfCurrentProcessFolder()
    {
        var installedFolder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"), "Installed");
        var repoFolder = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"), "Repo");
        var installedShellExe = Path.Combine(installedFolder, "Clip.exe");
        var installedWatcherExe = Path.Combine(installedFolder, "Clip.Watcher.exe");
        var repoShellExe = Path.Combine(repoFolder, "Clip.exe");
        var repoWatcherExe = Path.Combine(repoFolder, "Clip.Watcher.exe");
        Directory.CreateDirectory(installedFolder);
        Directory.CreateDirectory(repoFolder);
        File.WriteAllText(installedShellExe, "");
        File.WriteAllText(installedWatcherExe, "");
        File.WriteAllText(repoShellExe, "");
        File.WriteAllText(repoWatcherExe, "");
        WriteStartupValue($"\"{installedShellExe}\"");

        var migrated = StartupRegistration.MigrateToLightweightHostIfNeeded(_valueName, repoShellExe);

        Assert.True(migrated);
        Assert.Equal($"\"{installedWatcherExe}\" watch", StartupRegistration.CurrentValue(_valueName));
    }

    private void WriteStartupValue(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key!.SetValue(_valueName, value, RegistryValueKind.String);
    }

    public void Dispose()
    {
        try
        {
            StartupRegistration.SetEnabled(false, _valueName, "unused");
        }
        catch
        {
        }
    }
}
