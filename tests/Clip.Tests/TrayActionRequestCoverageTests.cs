using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The request file's path is baked into the class (it is how two Clip processes find each other),
/// so these tests necessarily touch the real per-user path. They only run when no request is
/// pending — a pending file belongs to the running app and consuming it would execute or destroy a
/// real tray action — and they leave the path exactly as they found it: absent.
///
/// One test method, because every scenario shares the single well-known file.
/// </summary>
public sealed class TrayActionRequestCoverageTests
{
    private static readonly string RequestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clip",
        "tray-action.request");

    [Fact]
    public void RequestsRoundTripThroughTheSharedFile()
    {
        if (File.Exists(RequestPath))
        {
            // A real pending tray action is in flight; do not touch it. The file normally lives
            // for milliseconds, so this is vanishingly rare.
            return;
        }

        try
        {
            // Nothing pending means nothing to consume.
            Assert.Null(TrayActionRequest.Consume());

            // Whitespace is not an action and must not even create the file.
            TrayActionRequest.Save("   ");
            Assert.False(File.Exists(RequestPath));

            // The round trip: saved trimmed, consumed once, gone afterwards.
            TrayActionRequest.Save("  show-palette  ");
            Assert.Equal("show-palette", TrayActionRequest.Consume());
            Assert.False(File.Exists(RequestPath));
            Assert.Null(TrayActionRequest.Consume());

            // A file holding only whitespace consumes as nothing, and is still cleaned up.
            File.WriteAllText(RequestPath, "   ");
            Assert.Null(TrayActionRequest.Consume());
            Assert.False(File.Exists(RequestPath));

            // A file another process holds open must not throw — the caller just shows the
            // palette as if no action was requested.
            using (var gate = new FileStream(RequestPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                gate.Write("locked"u8);
                gate.Flush();
                Assert.Null(TrayActionRequest.Consume());
            }
        }
        finally
        {
            try
            {
                File.Delete(RequestPath);
            }
            catch
            {
                // Leave nothing behind; best effort.
            }
        }
    }
}
