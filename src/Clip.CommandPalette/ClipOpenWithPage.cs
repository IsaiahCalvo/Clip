using Clip.Core;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Clip.CommandPalette;

// Secondary "Open with…" picker pushed from a clipboard item. Lists the apps discovered by the
// shared Clip.Core OpenWithAppDiscovery (default / recommended / recent / Start Menu / Store)
// and lets the Command Palette host filter them natively as the user types.
//
// Discovery's first call can take up to ~3.5s (it spawns powershell Get-StartApps for packaged
// apps), so we never run it on the constructor or the GetItems thread: a single "Loading apps…"
// row is shown immediately, discovery runs on a background task, and RaiseItemsChanged() repaints
// once the results are cached. The desktop-app set is process-static in Core, so later opens are
// instant within a palette session.
internal sealed partial class ClipOpenWithPage : ListPage
{
    private readonly ClipboardHistoryListItem _item;
    private readonly ClipboardHistoryStore _store;
    private readonly string _targetPath;
    private readonly object _gate = new();
    private IListItem[]? _appItems;
    private bool _discoveryStarted;

    public ClipOpenWithPage(ClipboardHistoryListItem item, ClipboardHistoryStore store, string targetPath)
    {
        _item = item;
        _store = store;
        _targetPath = targetPath;
        Id = "com.clip.commandpalette.openwith";
        Name = "Open With";
        Title = "Open with " + FileName(targetPath);
        Icon = new IconInfo("\uE8A7");
        ShowDetails = false;
    }

    public override IListItem[] GetItems()
    {
        lock (_gate)
        {
            if (_appItems is not null)
            {
                return _appItems;
            }

            if (!_discoveryStarted)
            {
                _discoveryStarted = true;
                _ = Task.Run(DiscoverAsync);
            }
        }

        return
        [
            new ListItem(new NoOpCommand())
            {
                Title = "Loading apps…",
                Subtitle = "Finding apps that can open this item.",
            },
        ];
    }

    private void DiscoverAsync()
    {
        IListItem[] items;
        try
        {
            var apps = OpenWithAppDiscovery.GetApps(_targetPath);
            items = apps.Count == 0
                ?
                [
                    new ListItem(new NoOpCommand())
                    {
                        Title = "No apps found",
                        Subtitle = "Windows did not report any apps for this item.",
                    },
                ]
                : apps.Select(MapApp).ToArray();
        }
        catch (Exception ex)
        {
            items =
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "Could not list apps",
                    Subtitle = ex.Message,
                },
            ];
        }

        lock (_gate)
        {
            _appItems = items;
        }

        RaiseItemsChanged(items.Length);
    }

    private IListItem MapApp(AppChoice app) =>
        new ListItem(new ClipOpenWithLaunchCommand(_item, _store, app, _targetPath))
        {
            Title = app.Name,
            Subtitle = SourceLabel(app),
            Icon = new IconInfo(IconForSource(app)),
            // The host orders sections by first appearance; recommended apps come first because
            // GetApps already sorts default/recent to the front.
            Section = app.IsDefault || app.IsRecent || app.Source == "Recommended" ? "Recommended" : "All apps",
        };

    private static string SourceLabel(AppChoice app)
    {
        if (app.IsDefault)
        {
            return "Default app";
        }

        if (app.IsRecent)
        {
            return "Recently used";
        }

        return app.Source;
    }

    // Segoe Fluent glyphs keyed by app source. Real per-app bitmaps would pull System.Drawing /
    // WinForms into the lightweight palette process, so glyphs are used instead.
    public static string IconForSource(AppChoice app)
    {
        if (app.IsDefault)
        {
            return "\uE8A7";
        }

        if (app.IsRecent)
        {
            return "\uE823";
        }

        return app.Source switch
        {
            "Recommended" => "\uE735",
            "Store app" => "\uE719",
            "Start Menu" => "\uE71D",
            _ => "\uECAA",
        };
    }

    private static string FileName(string targetPath)
    {
        try
        {
            var name = Path.GetFileName(targetPath.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(name) ? targetPath : name;
        }
        catch
        {
            return targetPath;
        }
    }
}
