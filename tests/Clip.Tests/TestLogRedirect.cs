using System.Runtime.CompilerServices;

namespace Clip.Tests;

// Runs before any test (and before Clip.Watcher.Program's static fields initialize), so the
// suite's deliberate failure-path tests log into a temp folder instead of the user's real
// %LocalAppData%\Clip\debug.log. Without this, corrupt-PDF fixtures show up in the production
// log looking like real preview failures.
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void RedirectClipLogsToTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), "Clip.Tests", "logs");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CLIP_LOG_ROOT", root);
    }
}
