namespace Clip.Tests;

/// <summary>
/// Deletes a test's temp folder, retrying briefly before giving up.
///
/// Windows can hold a handle open for a moment after the last writer closes it - Defender or the
/// indexer looking at a file the test just wrote - and a recursive delete that lands inside that
/// window throws IOException out of teardown, failing a test whose assertions all passed. CI hit
/// exactly that on a sidecar json and reddened a release build. Retry a few times, then give up
/// quietly: a leftover temp folder under %TEMP% is harmless, a red build is not.
/// </summary>
internal static class TestTemp
{
    internal static void Delete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(50);
        }
    }
}
