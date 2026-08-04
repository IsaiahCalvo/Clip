using System.IO;
using System.Reflection;
using Clip.Shell;
using Microsoft.Data.Sqlite;

namespace Clip.Tests;

public sealed class BrowserFaviconStoreCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clip.Tests", Guid.NewGuid().ToString("N"));

    public BrowserFaviconStoreCoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private static MethodInfo LoadMethod =>
        typeof(BrowserFaviconStore).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BrowserFaviconStore.Load not found");

    private string CreateFaviconDatabase()
    {
        var path = Path.Combine(_root, "Favicons");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE icon_mapping (page_url TEXT, icon_id INTEGER);
            CREATE TABLE favicon_bitmaps (icon_id INTEGER, image_data BLOB, width INTEGER);

            INSERT INTO icon_mapping VALUES ('https://big.example/page', 1);
            INSERT INTO favicon_bitmaps VALUES (1, x'01', 16);
            INSERT INTO favicon_bitmaps VALUES (1, x'0203', 32);

            INSERT INTO icon_mapping VALUES ('not a url', 2);
            INSERT INTO favicon_bitmaps VALUES (2, x'FF', 16);

            INSERT INTO icon_mapping VALUES ('https://www.other.example/', 3);
            INSERT INTO favicon_bitmaps VALUES (3, x'AA', 16);
            """;
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
        return path;
    }

    [Fact]
    public void LoadReadsLargestBitmapPerHostAndSkipsInvalidUrls()
    {
        var database = CreateFaviconDatabase();
        var icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        LoadMethod.Invoke(null, new object[] { database, icons });

        Assert.Equal(2, icons.Count);
        // Ascending width ordering means the 32px bitmap overwrites the 16px one.
        Assert.Equal(new byte[] { 0x02, 0x03 }, icons["big.example"]);
        Assert.Equal(new byte[] { 0xAA }, icons["www.other.example"]);
        Assert.False(icons.ContainsKey("not a url"));
    }

    [Fact]
    public void LoadThrowsForMissingDatabaseAndLeavesDictionaryEmpty()
    {
        var icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var missing = Path.Combine(_root, "does-not-exist", "Favicons");

        // CopyLocked skips missing sources, so SQLite fails to open the empty copy.
        var ex = Assert.Throws<TargetInvocationException>(() => LoadMethod.Invoke(null, new object[] { missing, icons }));

        Assert.NotNull(ex.InnerException);
        Assert.Empty(icons);
    }

    [Fact]
    public void TryGetMissesForUnknownHost()
    {
        // Machine-independent: whatever real browser databases exist, this host is not in them,
        // and neither is its www. alternate.
        var host = $"missing-{Guid.NewGuid():N}.example";

        Assert.False(BrowserFaviconStore.TryGet(host, out var png));
        Assert.Null(png);
    }
}
