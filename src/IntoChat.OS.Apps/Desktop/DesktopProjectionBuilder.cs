using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.UI;
using AppRef = IntoChat.OS.Host.Primitives.AppRef;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Desktop;

/// <summary>Single pure source of truth for tile resolution and <see cref="DesktopShellProjection"/> assembly.</summary>
internal static class DesktopProjectionBuilder
{
    public static AppTile[] ResolveTiles(DesktopAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var list = new List<AppTile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (state.Tiles is { Length: > 0 } liveTiles)
        {
            foreach (var tile in liveTiles)
            {
                if (tile is not null && !string.IsNullOrWhiteSpace(tile.Id) && !ShippedApps.Services.Contains(tile.Id) && seen.Add(tile.Id))
                {
                    list.Add(tile);
                }
            }
        }
        else
        {
            foreach (var manifest in ComponentManifests.All)
            {
                if (manifest.Face is not { } face || !manifest.Id.Value.StartsWith("app.", StringComparison.Ordinal)) { continue; }
                var appId = manifest.Id.Value["app.".Length..];
                if (string.IsNullOrWhiteSpace(appId) || ShippedApps.Services.Contains(appId) || !seen.Add(appId)) { continue; }

                list.Add(new AppTile(
                    Id: appId,
                    Title: string.IsNullOrWhiteSpace(face.Title) ? HumanizeTitle(appId) : face.Title,
                    Icon: string.IsNullOrWhiteSpace(face.Icon) ? null : face.Icon,
                    Category: face.Category,
                    Dock: face.Dock,
                    Launch: face.Maximized ? AppLaunch.Maximized : AppLaunch.Windowed,
                    Kind: "compiled",
                    Description: string.IsNullOrWhiteSpace(face.Description) ? null : face.Description));
            }
        }

        foreach (var unitAlias in state.DeployedUnits ?? [])
        {
            if (string.IsNullOrWhiteSpace(unitAlias) || ShippedApps.Services.Contains(unitAlias) || !seen.Add(unitAlias)) { continue; }
            list.Add(new AppTile(unitAlias, HumanizeTitle(unitAlias), "zap", "Micro-App", null, AppLaunch.Windowed, "unit"));
        }

        return [.. list
            .OrderBy(static t => t.Dock ?? int.MaxValue)
            .ThenBy(static t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static t => t.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public static string HumanizeTitle(string appId)
        => string.IsNullOrWhiteSpace(appId) ? "App" : char.ToUpperInvariant(appId[0]) + appId[1..];

    public static DesktopShellProjection Build(DesktopAppState state, string? brain = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var tiles = ResolveTiles(state);
        var tilesById = new Dictionary<string, AppTile>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in tiles) { tilesById.TryAdd(tile.Id, tile); }

        string TitleFor(string id)
            => tilesById.TryGetValue(id, out var known) && !string.IsNullOrWhiteSpace(known.Title)
                ? known.Title
                : HumanizeTitle(id);

        string AppIdFor(WorkspaceViewRef tab)
            => brain is { Length: > 0 } b ? AppRef.NameOf(b, tab.App) ?? tab.Id : tab.Id;

        var activeWindows = (state.Windows ?? []).Where(static w => w.Tabs is { Length: > 0 }).ToArray();
        var windows = new DesktopWindowProjection[activeWindows.Length];
        for (var i = 0; i < activeWindows.Length; i++)
        {
            var window = activeWindows[i];
            var activeIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
            var activeTab = window.Tabs[activeIndex];
            var appId = AppIdFor(activeTab);
            var isPinned = (state.Pins ?? []).Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase));
            var isFav = (state.Favourites ?? []).Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));
            var viewportUri = brain is { Length: > 0 }
                ? AppRef.Of(new BrainId(brain), DesktopApp.AppName).ChildKey($"window/{window.Id}")
                : $"apps/desktop/window/{window.Id}";

            var tabs = new DesktopTabProjection[window.Tabs.Length];
            for (var j = 0; j < window.Tabs.Length; j++)
            {
                var tab = window.Tabs[j];
                var tabAppId = AppIdFor(tab);
                tabs[j] = new DesktopTabProjection(tab.Id, tabAppId, TitleFor(tabAppId), Selected: j == activeIndex);
            }

            windows[i] = new DesktopWindowProjection(window.Id, appId, TitleFor(appId), viewportUri, tabs, activeTab.Id, isPinned, isFav);
        }

        var pins = (state.Pins ?? [])
            .Select(static p => p.Id?.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id) && !ShippedApps.Services.Contains(id!))
            .Select(static id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DesktopShellProjection(
            Windows: windows,
            FocusedViewId: state.FocusedViewId,
            Pins: pins,
            Tiles: tiles,
            OpenWidgets: DesktopAppState.ParseWidgets(state.WidgetsText),
            StylePreset: state.StylePreset,
            BarLayout: state.BarLayout,
            LayoutMode: state.LayoutMode,
            IsNarrow: state.IsNarrow);
    }
}
