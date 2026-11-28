using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Desktop;

/// <summary>Pure <see cref="Node"/> renderer that consumes <see cref="DesktopShellProjection"/> directly.</summary>
internal static class DesktopViewRenderer
{
    public static Node Render(DesktopShellProjection projection, Func<string, TabStripProps, Node> renderTabStrip)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(renderTabStrip);

        var tiles = projection.Tiles;
        var tilesById = new Dictionary<string, AppTile>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in tiles) { tilesById.TryAdd(tile.Id, tile); }

        Node windows = projection.Windows.Length == 0
            ? VStack([])
            : VStack([.. projection.Windows.Chunk(3).Select(row => (Node)HStack([.. row.Select(w => BuildWindowFrame(w, renderTabStrip))]))])
                .Extents([.. projection.Windows.Chunk(3).Select(_ => 0.0)]);

        var dockNodes = new List<Node>();
        var dockSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tile in tiles)
        {
            if (tile.Dock is not null && dockSeen.Add(tile.Id)) { dockNodes.Add(BuildTile($"dock-{tile.Id}", tile)); }
        }

        foreach (var pinId in projection.Pins)
        {
            if (!dockSeen.Add(pinId)) { continue; }
            var tile = tilesById.TryGetValue(pinId, out var known)
                ? known
                : new AppTile(pinId, DesktopProjectionBuilder.HumanizeTitle(pinId), null, null, null, AppLaunch.Windowed, "runtime");
            dockNodes.Add(BuildTile($"dock-pin-{pinId}", tile));
        }

        foreach (var tile in tiles)
        {
            if (string.Equals(tile.Kind, "unit", StringComparison.OrdinalIgnoreCase) && dockSeen.Add(tile.Id))
            {
                dockNodes.Add(BuildTile($"dock-unit-{tile.Id}", tile));
            }
        }

        dockNodes.Add(Button("New tab", Icons.Plus)
            .Key("newtab")
            .Small()
            .Style(OsMixStyles.FilterPill(selected: false))
            .Sends(new DesktopNewTab()));

        var catalogNodes = new Node[tiles.Length];
        for (var i = 0; i < tiles.Length; i++) { catalogNodes[i] = BuildTile($"catalog-{tiles[i].Id}", tiles[i]); }

        var dock = HStack([
            HStack(4, [.. dockNodes]),
            HStack(4)[
                StatusPill(StatusKind.Done, "Usage"),
                Text(string.Join(" · ", projection.OpenWidgets)),
                Button("You").Key("avatar").Style(OsMixStyles.SecondaryButton(small: true).Radius(999)).Sends(new DesktopOpen("friends")),
                Button("Settings").Key("more-settings").Style(OsMixStyles.SecondaryButton(small: true).Radius(999)).Sends(new DesktopSettings())
            ]
        ]).Extents(0, 360);

        var catalog = new Surface(catalogNodes, "app-catalog", Padding: 0)
        {
            Style = Mix.Style().Tone(UiTone.Elevated).Elevation(UiElevation.Subtle).Padding(12).Radius(18),
        };

        return VStack([windows, dock, catalog]).Extents(0, 68, 0);
    }

    private static AppTileNode BuildTile(string key, AppTile tile)
    {
        var command = string.Equals(tile.Id, "settings", StringComparison.OrdinalIgnoreCase)
            ? Sends(new DesktopSettings())
            : Sends(new DesktopOpen(tile.Id));
        return Kit.AppTile(
            key: key,
            appId: tile.Id,
            title: tile.Title,
            icon: tile.Icon,
            category: tile.Category,
            launch: tile.Launch,
            dock: tile.Dock,
            appKind: tile.Kind,
            description: tile.Description,
            command: command,
            automationName: tile.Title);
    }

    private static Node BuildWindowFrame(DesktopWindowProjection window, Func<string, TabStripProps, Node> renderTabStrip)
    {
        var winId = window.Id;
        var activeIndex = Math.Max(0, Array.FindIndex(window.Tabs, static t => t.Selected));
        var appId = window.AppId;

        Node[] chromeButtons =
        [
            GhostButton(window.Title)
                .Key($"focus-{winId}")
                .Style(Mix.Style()
                    .Tone(UiTone.Ghost)
                    .Typography(UiTypography.Label)
                    .Padding(10, 5)
                    .Radius(8)
                    .Motion(UiMotion.Snappy)
                    .OnHover(s => s.Tone(UiTone.Subtle))
                    .OnPress(s => s.Scale(0.98)))
                .Sends(new DesktopFocus(winId)),
            GhostButton("New tab", Icons.Plus).Key($"newtab-{winId}").Small().Sends(new DesktopNewTab(winId)),
            GhostButton(window.IsFavourite ? "Remove from favourites" : "Add to favourites", "star")
                .Key($"star-{winId}")
                .Small()
                .Style(window.IsFavourite ? OsMixStyles.FilterPill(selected: true) : OsMixStyles.GhostButton(small: true))
                .Sends(new DesktopFavourite(winId)),
            .. appId == "ino"
                ? Array.Empty<Node>()
                : [window.IsPinned
                    ? GhostButton("Unpin", "pin-off").Key($"unpin-{winId}").Small().Sends(new DesktopUnpinApp(appId)).ToNode()
                    : GhostButton("Pin", "pin").Key($"pin-{winId}").Small().Sends(new DesktopPinApp(appId, true)).ToNode()],
            GhostButton("Close tab", "x")
                .Key($"close-{winId}")
                .Small()
                .Style(s => s.OnHover(h => h.Tone(UiTone.Danger)))
                .Sends(new DesktopClose($"{winId}/{activeIndex}")),
        ];

        TabStripItem[] tabItems = [.. window.Tabs.Select((tab, idx) => new TabStripItem($"{winId}/{idx}", tab.Title))];

        return VStack(0, [
            new Row(chromeButtons) { Gap = 4, Extents = [0, .. chromeButtons.Skip(1).Select(_ => 32.0)] },
            DesktopApp.HasTabStrip(winId)
                ? renderTabStrip(DesktopApp.WindowTabsKey(winId), new TabStripProps(tabItems, AutomationName: $"{window.Title} tabs", SelectedId: $"{winId}/{activeIndex}"))
                : new Spacer(0),
            Kit.Embed(window.ViewportUri, "viewport"),
        ]).Extents(40, 38, 0);
    }
}
