using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Clip.Shell;

/// <summary>
/// Reads icons out of the favicon databases Chrome and Edge already keep on disk.
///
/// This is the zero-network path: for any site the user has actually visited, the icon is already
/// there and no request has to be made at all. Only sites that miss here fall through to fetching
/// from the site itself.
///
/// Lookups are answered from an in-memory snapshot built once per process, so a miss costs a
/// dictionary probe rather than a database round trip — a miss has to be as fast as a hit,
/// otherwise every unvisited site would stall its row before falling back.
/// </summary>
internal static class BrowserFaviconStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, byte[]>? _icons;

    /// <summary>
    /// True once the snapshot has been loaded. Loading is lazy and happens on the first lookup,
    /// which runs on the favicon worker thread rather than the UI thread.
    /// </summary>
    public static bool TryGet(string host, out byte[]? png)
    {
        var icons = Snapshot();
        png = null;

        if (icons.Count == 0)
        {
            return false;
        }

        if (icons.TryGetValue(host, out png))
        {
            return true;
        }

        // github.com and www.github.com are the same site as far as an icon is concerned.
        var alternate = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : "www." + host;

        return icons.TryGetValue(alternate, out png);
    }

    private static Dictionary<string, byte[]> Snapshot()
    {
        lock (Gate)
        {
            if (_icons is not null)
            {
                return _icons;
            }

            _icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var database in Databases())
            {
                try
                {
                    Load(database, _icons);
                }
                catch
                {
                    // A browser we cannot read is simply not a source; the site fetch still works.
                }
            }

            return _icons;
        }
    }

    private static IEnumerable<string> Databases()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(local, "Vivaldi", "User Data"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            // "Default" plus any "Profile 1", "Profile 2", ... the user has.
            foreach (var profile in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(profile, "Favicons");
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static void Load(string databasePath, Dictionary<string, byte[]> into)
    {
        // The live file is locked while the browser runs, so work from a copy. The -wal file
        // carries recent writes and has to come along or the snapshot can be stale.
        var scratch = Path.Combine(Path.GetTempPath(), "clip-favicons", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var copy = Path.Combine(scratch, "Favicons");

        try
        {
            CopyLocked(databasePath, copy);
            CopyLocked(databasePath + "-wal", copy + "-wal");

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());

            connection.Open();

            // Largest bitmap per page, newest mapping wins. page_url is a full URL, so the host
            // is pulled out in code rather than with SQL string surgery.
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.page_url, b.image_data, b.width
                FROM icon_mapping m
                JOIN favicon_bitmaps b ON b.icon_id = m.icon_id
                WHERE b.image_data IS NOT NULL AND length(b.image_data) > 0
                ORDER BY b.width ASC
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pageUrl = reader.GetString(0);
                if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                {
                    continue;
                }

                var bytes = (byte[])reader["image_data"];
                // Ascending width means the last write for a host is its largest bitmap.
                into[uri.Host] = bytes;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch
            {
                // Scratch copy; leaving it behind is harmless.
            }
        }
    }

    private static void CopyLocked(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        // FileShare.ReadWrite is required: the browser holds the file open.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }
}
