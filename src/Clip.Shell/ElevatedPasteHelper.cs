using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Clip.Core;

namespace Clip.Shell;

/// <summary>
/// Talks to <c>Clip.Elevated.exe</c>, the companion that presses Ctrl+V when the paste target runs
/// as administrator.
///
/// Clip runs at medium integrity, and Windows drops synthetic input aimed at a higher-integrity
/// window (UIPI). The clipboard itself is not restricted, so the text is already where it needs to
/// be by the time this is called — only the keystroke is missing, and a process running elevated
/// can supply it.
///
/// Launching the helper raises a UAC prompt, once. After that it stays resident and later pastes
/// cost nothing. Declining is a normal outcome, not an error: the caller falls back to telling the
/// user to press Ctrl+V themselves, which has always worked.
/// </summary>
internal static class ElevatedPasteHelper
{
    private const string PipeName = "Clip.Elevated.Paste";
    private const string HelperFileName = "Clip.Elevated.exe";

    /// <summary>
    /// Covers only the helper starting up after the prompt is answered, not the prompt itself -
    /// Process.Start blocks on the elevation decision, and the secure desktop is up for that. This
    /// runs on the UI thread, so a generous value here would read as a frozen palette; ten seconds
    /// is far more than a process needs to open a pipe, and gives up quickly if it never does.
    /// </summary>
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Set once the user declines the prompt, so a run of pastes into an elevated app raises the
    /// prompt at most once. Restarting Clip offers it again.
    /// </summary>
    private static bool _declined;

    internal static bool Declined => _declined;

    internal static bool IsAvailable => File.Exists(HelperPath());

    /// <summary>
    /// Presses Ctrl+V through the helper, starting it if needed. Returns false when the helper is
    /// missing, the user declined the prompt, or the paste did not go through — every one of those
    /// means "tell the user to press Ctrl+V", never "retry silently".
    /// </summary>
    /// <param name="restoreFocus">
    /// Puts the paste target back in front. Called twice on purpose: once before the first attempt,
    /// and again after the UAC prompt, which takes the foreground for itself and does not
    /// necessarily hand it back to the app the user was in.
    /// </param>
    internal static bool TryPaste(Action restoreFocus)
    {
        if (_declined || !IsAvailable)
        {
            return false;
        }

        restoreFocus();
        if (TrySendPaste())
        {
            return true;
        }

        if (!TryStartHelper())
        {
            return false;
        }

        restoreFocus();
        return TrySendPaste();
    }

    private static bool TrySendPaste()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine("paste");
            return string.Equals(reader.ReadLine(), "ok", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // No helper listening yet, which is the normal first-paste case.
            return false;
        }
    }

    private static bool TryStartHelper()
    {
        try
        {
            // UseShellExecute is required for the runas verb; without it the manifest's
            // requireAdministrator produces an access-denied launch instead of a UAC prompt.
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = HelperPath(),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return false;
            }

            return WaitForPipe();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user said no. Remember it, so a run of pastes into the same
            // elevated app does not reprompt on every one.
            _declined = true;
            ShellLog.Info("elevated paste helper declined at the UAC prompt");
            return false;
        }
        catch (Exception ex)
        {
            ShellLog.Error(ex, "elevated paste helper launch failed");
            return false;
        }
    }

    /// <summary>
    /// Waits for the helper's pipe rather than for the process, because the process exists well
    /// before it is listening, and a fixed sleep would either be too short on a cold start or
    /// waste the user's time on a warm one.
    /// </summary>
    private static bool WaitForPipe()
    {
        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists($@"\\.\pipe\{PipeName}"))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        ShellLog.Info("elevated paste helper never started listening");
        return false;
    }

    private static string HelperPath() =>
        Path.Combine(AppContext.BaseDirectory, HelperFileName);
}
