using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Clip.Core;

// A discovered "Open with" application. Either ExecutablePath (desktop apps) or
// AppUserModelId (packaged/Store apps) identifies the launch target; IsDefault means
// "let the shell pick" (plain shell-open of the file).
public sealed record AppChoice(
    string Name,
    string? ExecutablePath,
    string Source,
    bool IsDefault = false,
    bool IsRecent = false,
    string? AppUserModelId = null);

// Reusable, WPF-free "Open with" application discovery shared by the WPF shell picker
// and the Command Palette picker. Discovers, in priority order: the default/recommended
// file-association app, recently used apps (per extension), Start Menu shortcuts,
// registered App Paths, and Microsoft Store packaged apps. The desktop-app set is
// process-cached so the first discovery (which spawns powershell Get-StartApps and can
// take up to ~3.5s) is paid once per process.
public static class OpenWithAppDiscovery
{
    private static IReadOnlyList<AppChoice>? _desktopAppCache;

    public static IReadOnlyList<AppChoice> GetApps(string targetPath)
    {
        var apps = new List<AppChoice>
        {
            new("Default app", null, "Windows", IsDefault: true),
        };

        AddAssociatedApp(apps, Path.GetExtension(targetPath));
        apps.AddRange(OpenWithRecentStore.Load(targetPath));
        apps.AddRange(DesktopApps());

        return apps
            .Where(app => app.IsDefault ||
                !string.IsNullOrWhiteSpace(app.AppUserModelId) ||
                (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath)))
            .Where(IsUsefulOpenWithApp)
            .GroupBy(NormalizedAppName, StringComparer.OrdinalIgnoreCase)
            .Select(BestChoice)
            .OrderByDescending(app => app.IsDefault)
            .ThenByDescending(app => app.IsRecent)
            .ThenBy(app => app.Name)
            .ToList();
    }

    private static IReadOnlyList<AppChoice> DesktopApps()
    {
        return _desktopAppCache ??= PackagedApps().Concat(StartMenuApps()).Concat(AppPathRegistryApps()).ToList();
    }

    private static bool IsUsefulOpenWithApp(AppChoice app)
    {
        if (app.IsDefault || app.IsRecent || app.Source == "Recommended")
        {
            return true;
        }

        var name = app.Name.ToLowerInvariant();
        if (name is "acrobatinfo" or "7zfm" or "acrodist" or "acrobat" or "adobe acrobat distiller" or "magnifier" or "magnify" or "node.js website" or "chrome" or "dokumentation")
        {
            return false;
        }

        if (name.Contains("help") ||
            name.Contains("documentation") ||
            name.Contains("dokumentation") ||
            name.Contains("uninstall") ||
            name.Contains("support") ||
            name.Contains("update") ||
            name.Contains("feedback") ||
            name.Contains("component services") ||
            name.Contains("event viewer") ||
            name.Contains("control panel") ||
            name.Contains("command prompt") ||
            name.Contains("console") ||
            name.Contains("debugging") ||
            name.Contains("application verifier") ||
            name.Contains("defragment") ||
            name.Contains("disk cleanup") ||
            name.Contains("global flags") ||
            name.Contains("gflags") ||
            name.Contains("usb recovery") ||
            name.Contains("administrative tools") ||
            name.Contains("computer management") ||
            name.Contains("license manager") ||
            name.Contains("ghostscript") ||
            name.Contains("git bash") ||
            name.Contains("git cmd") ||
            name.Contains("git gui") ||
            name.Contains("git for windows") ||
            name.Contains("git release") ||
            name.Contains("git faq") ||
            name.Contains("idle (python") ||
            name.Contains("homepage") ||
            name.Contains("faq") ||
            name.Contains("get started") ||
            name.Contains("live captions") ||
            name.Contains("hyper-v quick create") ||
            name.Contains("install additional tools") ||
            name.Contains("gpview") ||
            name.Contains("iscsicli") ||
            name.Contains("local security policy") ||
            name.Contains("mail app wizard") ||
            name.Contains("msoadfsb") ||
            name.Contains("msoasb") ||
            name.Contains("iediagcmd") ||
            name.Equals("iexplore") ||
            name.Contains("importwizard") ||
            name.Contains("iexpress") ||
            name.Contains("diagnostic"))
        {
            return false;
        }

        return true;
    }

    private static string NormalizedAppName(AppChoice app)
    {
        if (app.IsDefault)
        {
            return "default";
        }

        var name = app.Name;
        foreach (var suffix in new[] { " app", " file manager", " x64", " wow", " (preview)" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
            }
        }

        return name.Trim();
    }

    private static AppChoice BestChoice(IEnumerable<AppChoice> choices)
    {
        return choices
            .OrderByDescending(app => app.IsDefault)
            .ThenByDescending(app => app.IsRecent)
            .ThenByDescending(app => app.Source == "Recommended")
            .ThenByDescending(app => app.Source == "Start Menu")
            .ThenByDescending(app => app.Source == "Installed app")
            .First();
    }

    private static void AddAssociatedApp(List<AppChoice> apps, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return;
        }

        var executable = AssociationQuery.GetString(extension, AssociationString.Executable, "open");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        var friendlyName = AssociationQuery.GetString(extension, AssociationString.FriendlyAppName, "open");
        apps.Add(new(string.IsNullOrWhiteSpace(friendlyName) ? Path.GetFileNameWithoutExtension(executable) : friendlyName, executable, "Recommended"));
    }

    private static IEnumerable<AppChoice> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var link in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                var target = ShortcutResolver.Resolve(link);
                if (!string.IsNullOrWhiteSpace(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                {
                    yield return new(Path.GetFileNameWithoutExtension(link), target, "Start Menu");
                }
            }
        }
    }

    private static IEnumerable<AppChoice> PackagedApps()
    {
        foreach (var app in PackagedAppDiscovery.GetStartApps())
        {
            yield return new(app.Name, null, "Store app", AppUserModelId: app.AppUserModelId);
        }
    }

    private static IEnumerable<AppChoice> AppPathRegistryApps()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null)
            {
                continue;
            }

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var appKey = key.OpenSubKey(subKeyName);
                var path = appKey?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (File.Exists(path))
                {
                    yield return new(Path.GetFileNameWithoutExtension(path), path, "Installed app");
                }
            }
        }
    }
}

// Launches a file with a discovered AppChoice. Desktop apps run the .exe with the file
// as an argument; the default app uses a plain shell-open; packaged apps activate via COM.
public static class OpenWithAppLauncher
{
    public static void OpenWith(string targetPath, AppChoice app)
    {
        if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
        {
            PackagedAppLauncher.OpenFile(app.AppUserModelId, targetPath);
            return;
        }

        if (app.IsDefault || string.IsNullOrWhiteSpace(app.ExecutablePath))
        {
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(app.ExecutablePath, Quote(targetPath))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app.ExecutablePath) ?? Environment.CurrentDirectory,
        });
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

internal static class ShortcutResolver
{
    private const int MaxPath = 260;
    private const uint SlgpRawPath = 0x4;

    public static string? Resolve(string linkPath)
    {
        IShellLinkW? link = null;
        try
        {
            // Resolve .lnk targets through the explicitly-declared IShellLinkW + IPersistFile
            // COM interfaces rather than the late-bound WScript.Shell `dynamic` object. The
            // statically-typed interop is trim-safe: the trimmer keeps these interface
            // declarations, whereas the old dynamic path relied on reflection metadata that
            // the trimmer could remove from the shipped MSIX.
            link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(linkPath, 0);

            var builder = new StringBuilder(MaxPath);
            link.GetPath(builder, builder.Capacity, IntPtr.Zero, SlgpRawPath);
            var targetPath = builder.ToString();
            return string.IsNullOrWhiteSpace(targetPath)
                ? null
                : Environment.ExpandEnvironmentVariables(targetPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }
}

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class CShellLink;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath(
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
        int cch,
        IntPtr pfd,
        uint fFlags);

    void GetIDList(out IntPtr ppidl);

    void SetIDList(IntPtr pidl);

    void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

    void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

    void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

    void GetHotkey(out short pwHotkey);

    void SetHotkey(short wHotkey);

    void GetShowCmd(out int piShowCmd);

    void SetShowCmd(int iShowCmd);

    void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

    void Resolve(IntPtr hwnd, uint fFlags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal interface IPersistFile
{
    void GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

    void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName);

    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}

internal sealed record PackagedAppInfo(string Name, string AppUserModelId);

internal static class PackagedAppDiscovery
{
    private static IReadOnlyList<PackagedAppInfo>? _cache;

    public static IReadOnlyList<PackagedAppInfo> GetStartApps()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return _cache = [];
            }

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3500);
            if (string.IsNullOrWhiteSpace(json))
            {
                return _cache = [];
            }

            var items = JsonSerializer.Deserialize(json, OpenWithJsonContext.Default.ListStartAppJson) ?? [];
            _cache = items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.AppID))
                .Select(item => new PackagedAppInfo(item.Name!, item.AppID!))
                .ToList();
            return _cache;
        }
        catch
        {
            return _cache = [];
        }
    }

    internal sealed class StartAppJson
    {
        public string? Name { get; set; }
        public string? AppID { get; set; }
    }
}

internal static class PackagedAppLauncher
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2050:COM marshalling correctness cannot be guaranteed after trimming",
        Justification = "The COM interfaces (IShellItem, IShellItemArray) used by SHCreateItemFromParsingName and " +
            "SHCreateShellItemArrayFromShellItem are explicitly declared in this assembly as [ComImport] interfaces, " +
            "so the trimmer cannot remove their members. The interop is statically typed and provably safe under trimming.")]
    public static void OpenFile(string appUserModelId, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("Target file was not found.", path);
        }

        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var shellItem);
        try
        {
            SHCreateShellItemArrayFromShellItem(shellItem, typeof(IShellItemArray).GUID, out var shellItemArray);
            try
            {
                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                var hr = manager.ActivateForFile(appUserModelId, shellItemArray, null, out _);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            finally
            {
                if (shellItemArray is not null && Marshal.IsComObject(shellItemArray))
                {
                    Marshal.FinalReleaseComObject(shellItemArray);
                }
            }
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
            {
                Marshal.FinalReleaseComObject(shellItem);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromShellItem(
        IShellItem psi,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemArray ppv);
}

[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
public class ApplicationActivationManager;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
public interface IApplicationActivationManager
{
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        uint options,
        out uint processId);

    int ActivateForFile(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IShellItemArray itemArray,
        [MarshalAs(UnmanagedType.LPWStr)] string? verb,
        out uint processId);

    int ActivateForProtocol(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        IShellItemArray itemArray,
        out uint processId);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
public interface IShellItem;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
public interface IShellItemArray;

internal enum AssociationString
{
    Executable = 2,
    FriendlyAppName = 4,
    ShellExtension = 16,
}

internal static class AssociationQuery
{
    public static string? GetString(string association, AssociationString value, string? extra)
    {
        try
        {
            uint length = 0;
            _ = AssocQueryString(0, value, association, extra, null, ref length);
            if (length == 0)
            {
                return null;
            }

            var builder = new StringBuilder((int)length);
            return AssocQueryString(0, value, association, extra, builder, ref length) == 0 ? builder.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int AssocQueryString(
        int flags,
        AssociationString str,
        string pszAssoc,
        string? pszExtra,
        StringBuilder? pszOut,
        ref uint pcchOut);
}
