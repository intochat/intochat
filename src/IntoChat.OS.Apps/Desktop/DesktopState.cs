using System.Globalization;
using DigitalBrain;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Desktop;

/// <summary>
/// Pure DDD Aggregate state and workspace transition decisions for <see cref="DesktopApp"/>.
/// Enforces window cap (8), per-window tab cap (8), and pinned core-app invariants (<c>"ino"</c> never unpins; <c>"desktop"</c> never pins).
/// </summary>
public sealed record DesktopAppState(
    string LayoutMode,
    bool IsNarrow,
    string StylePreset,
    string BarLayout,
    int ActiveNeurons,
    string Status,
    string[] PinnedApps,
    WorkspaceWindow[] Windows,
    string? FocusedViewId,
    WorkspaceViewRef[] Pins,
    WorkspaceViewRef[] Favourites,
    string WidgetsText,
    string[]? DeployedUnits = null)
{
    public const int MaxWindows = 8;
    public const int MaxTabsPerWindow = 8;

    public DesktopAppState() : this(
        "smart-desktop",
        false,
        "intochat",
        "floating-glass",
        10,
        "Smart Desktop Ready",
        ["ino", "creator"],
        [],
        null,
        [],
        [],
        "")
    { }

    [Invariant]
    public bool WindowCapHolds => (Windows ?? []).Length <= MaxWindows;

    [Invariant]
    public bool TabCapHolds => (Windows ?? []).All(w => (w.Tabs ?? []).Length <= MaxTabsPerWindow);

    [Invariant]
    public bool DesktopNeverPinned => !(PinnedApps ?? []).Contains("desktop", StringComparer.OrdinalIgnoreCase);

    public Rule WindowLimitRule => Decision.Rule("max_windows", (Windows ?? []).Length >= MaxWindows, $"At most {MaxWindows} windows can be open simultaneously.");

    public static Rule UnpinnableAppRule(string? appId) => Decision.Rule(
        "core_dock_app",
        string.Equals(appId, "ino", StringComparison.OrdinalIgnoreCase) || string.Equals(appId, "desktop", StringComparison.OrdinalIgnoreCase),
        "Core shell surfaces ('ino', 'desktop') cannot be pinned or unpinned.");

    public DesktopAppState ToggleLayoutMode() => this with
    {
        LayoutMode = LayoutMode == "smart-desktop" ? "spatial-grid" : "smart-desktop",
    };

    public DesktopAppState AutoArrange()
    {
        var pinnedList = PinnedApps is { Length: > 0 } ? PinnedApps : ["ino", "creator"];
        return this with
        {
            LayoutMode = "whiteboard-auto-arranged",
            PinnedApps = pinnedList,
            Status = $"Whiteboard Auto-Arranged · {pinnedList.Length} pinned sticky-note app(s) locked on top ({string.Join(", ", pinnedList)})",
        };
    }

    public Decision<DesktopAppState> PinOrUnpin([Sample("photocraft", "ino")] string? rawAppId, bool pinned = true)
    {
        var appId = string.IsNullOrWhiteSpace(rawAppId) ? "photocraft" : rawAppId.Trim().ToLowerInvariant();
        if (Decision.Check("pin", UnpinnableAppRule(appId)) is { } refused)
        {
            return refused;
        }

        var existingPins = PinnedApps is { Length: > 0 } ? PinnedApps : ["ino", "creator"];
        var nextPinnedApps = pinned
            ? (existingPins.Contains(appId, StringComparer.OrdinalIgnoreCase) ? existingPins : [.. existingPins, appId])
            : existingPins.Where(p => !string.Equals(p, appId, StringComparison.OrdinalIgnoreCase)).ToArray();

        return this with { PinnedApps = nextPinnedApps };
    }

    public static WorkspaceUpdate BuildUpdate(
        WorkspaceState s,
        WorkspaceWindow[] windows,
        string? focusedViewId = null,
        WorkspaceViewRef[]? pins = null,
        WorkspaceViewRef[]? favourites = null) =>
        new([.. windows.SelectMany(w => w.Tabs)], focusedViewId ?? s.FocusedViewId, pins ?? s.Pins)
        {
            Windows = windows,
            Favourites = favourites ?? s.Favourites,
        };

    public static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) OpenWorkspaceApp(
        WorkspaceState s,
        string rawAppName,
        string? targetWindowId = null,
        bool asNewTab = false,
        string brain = "local")
    {
        var appName = rawAppName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(appName) || appName == "desktop") { return (null, []); }
        var appKey = IApp.Key(brain, appName);
        var windows = s.Windows.Where(w => w.Tabs is { Length: > 0 }).ToList();

        if (!asNewTab)
        {
            foreach (var w in windows)
            {
                if (w.Tabs.FirstOrDefault(t => string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)) is { } existingTab)
                {
                    if (w.ActiveTabId == existingTab.Id && s.FocusedViewId == existingTab.Id) { return (null, []); }
                    var focusedWindows = windows.Select(win => win.Id == w.Id ? win with { ActiveTabId = existingTab.Id } : win).ToArray();
                    return (BuildUpdate(s, focusedWindows, existingTab.Id), []);
                }
            }
        }

        var takenTabIds = windows.SelectMany(w => w.Tabs).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var tabId = appName;
        for (var n = 2; !takenTabIds.Add(tabId); n++) { tabId = $"{appName}-{n}"; }
        var newTab = new WorkspaceViewRef(tabId, appKey, "full", string.Empty);

        if (asNewTab)
        {
            var targetWin = targetWindowId is { Length: > 0 }
                ? windows.FirstOrDefault(w => w.Id == targetWindowId)
                : windows.FirstOrDefault(w => w.Tabs.Any(t => t.Id == s.FocusedViewId)) ?? windows.LastOrDefault();
            if (targetWin is not null)
            {
                if (targetWin.Tabs.Length >= MaxTabsPerWindow) { return (null, []); }
                var updatedWins = windows.Select(w => w.Id == targetWin.Id ? new WorkspaceWindow(targetWin.Id, [.. targetWin.Tabs, newTab], newTab.Id) : w).ToArray();
                return (BuildUpdate(s, updatedWins, newTab.Id), []);
            }
        }

        if (windows.Count >= MaxWindows) { return (null, []); }

        var takenWinIds = windows.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var nextWinId = Enumerable.Range(1, 99).Select(n => $"w-{n}").First(id => !takenWinIds.Contains(id));
        return (BuildUpdate(s, [.. windows, new WorkspaceWindow(nextWinId, [newTab], newTab.Id)], newTab.Id), []);
    }

    public static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) CloseWorkspaceTarget(WorkspaceState s, string target)
    {
        var slash = target.LastIndexOf('/');
        var winId = slash >= 0 ? target[..slash] : target;
        var indexStr = slash >= 0 ? target[(slash + 1)..] : string.Empty;
        var windows = s.Windows.Where(w => w.Tabs is { Length: > 0 }).ToList();
        var window = windows.FirstOrDefault(w => w.Id == winId);
        if (window is null) { return (null, []); }

        if (slash < 0 || !int.TryParse(indexStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tabIndex))
        {
            tabIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
        }
        if (tabIndex < 0 || tabIndex >= window.Tabs.Length) { return (null, []); }

        if (window.Tabs.Length <= 1 || slash < 0)
        {
            var remaining = windows.Where(w => w.Id != winId).ToArray();
            var nextFocus = remaining.Any(w => w.Tabs.Any(t => t.Id == s.FocusedViewId)) ? s.FocusedViewId : remaining.LastOrDefault()?.ActiveTabId;
            return (BuildUpdate(s, remaining, nextFocus), [winId]);
        }

        var closedTabId = window.Tabs[tabIndex].Id;
        var nextTabs = window.Tabs.Where((_, i) => i != tabIndex).ToArray();
        var nextActive = window.ActiveTabId == closedTabId ? nextTabs[Math.Max(0, tabIndex - 1)].Id : window.ActiveTabId;
        var updatedWindows = windows.Select(w => w.Id == winId ? new WorkspaceWindow(winId, nextTabs, nextActive) : w).ToArray();
        return (BuildUpdate(s, updatedWindows, s.FocusedViewId == closedTabId ? nextActive : s.FocusedViewId), []);
    }

    public static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) CloseWorkspaceApp(WorkspaceState s, string rawAppName, string brain = "local")
    {
        var normalized = rawAppName.Trim().ToLowerInvariant();
        var appKey = IApp.Key(brain, normalized);
        var fallbackKey = IApp.Key("local", normalized);
        var closedIds = new List<string>();
        var nextWindows = new List<WorkspaceWindow>();
        var changed = false;

        foreach (var w in s.Windows.Where(w => w.Tabs is { Length: > 0 }))
        {
            var keptTabs = w.Tabs.Where(t =>
                !string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t.App, fallbackKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (keptTabs.Length != w.Tabs.Length) { changed = true; }
            if (keptTabs.Length == 0) { closedIds.Add(w.Id); }
            else { nextWindows.Add(new WorkspaceWindow(w.Id, keptTabs, keptTabs.Any(t => t.Id == w.ActiveTabId) ? w.ActiveTabId : keptTabs[^1].Id)); }
        }

        if (!changed) { return (null, []); }
        var remaining = nextWindows.ToArray();
        var nextFocus = remaining.Any(w => w.Tabs.Any(t => t.Id == s.FocusedViewId)) ? s.FocusedViewId : remaining.LastOrDefault()?.ActiveTabId;
        return (BuildUpdate(s, remaining, nextFocus), closedIds);
    }

    public static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) SelectWorkspaceTab(WorkspaceState s, string target)
    {
        var trimmed = target.Trim();
        var sep = trimmed.LastIndexOf('/');
        if (sep < 0) { sep = trimmed.LastIndexOf(':'); }
        if (sep <= 0) { return (null, []); }

        var winId = trimmed[..sep];
        var tabPart = trimmed[(sep + 1)..];
        var w = s.Windows.FirstOrDefault(win => win.Id == winId);
        if (w is null) { return (null, []); }

        var chosen = int.TryParse(tabPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < w.Tabs.Length
            ? w.Tabs[idx]
            : w.Tabs.FirstOrDefault(t => t.Id == tabPart);
        if (chosen is null || (w.ActiveTabId == chosen.Id && s.FocusedViewId == chosen.Id)) { return (null, []); }

        var updatedWins = s.Windows.Select(win => win.Id == winId ? win with { ActiveTabId = chosen.Id } : win).ToArray();
        return (BuildUpdate(s, updatedWins, chosen.Id), []);
    }

    public static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) ToggleWorkspaceFavourite(WorkspaceState s, string windowId, string brain = "local")
    {
        var w = s.Windows.FirstOrDefault(win => win.Id == windowId);
        var activeTab = w?.Tabs.FirstOrDefault(t => t.Id == w.ActiveTabId) ?? w?.Tabs.LastOrDefault();
        if (activeTab is null) { return (null, []); }

        var appId = IApp.NameOf(brain, activeTab.App)
            ?? IApp.NameOf("local", activeTab.App)
            ?? activeTab.Id;
        var exists = s.Favourites.Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));
        WorkspaceViewRef[] nextFavs = exists
            ? [.. s.Favourites.Where(f => !string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase))]
            : [.. s.Favourites.Take(4), new WorkspaceViewRef(appId, activeTab.App, string.Empty, string.Empty)];
        return (BuildUpdate(s, s.Windows, favourites: nextFavs), []);
    }
}
