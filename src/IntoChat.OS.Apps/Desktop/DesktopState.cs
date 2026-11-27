using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Desktop;

/// <summary>
/// Pure DDD Aggregate state and workspace transition decisions for <see cref="DesktopApp"/>.
/// Enforces layout validity, window cap (8), per-window tab cap (8), unique window IDs / valid focus,
/// bounded dock pins/favourites, and core-app pin invariants (<c>"ino"</c> never unpins; <c>"desktop"</c> never pins).
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
    string[]? DeployedUnits = null,
    AppTile[]? Tiles = null,
    string? SpecialistError = null)
{
    public const int MaxWindows = 8;
    public const int MaxTabsPerWindow = 8;
    public const int MaxPins = 16;
    public const int MaxFavourites = 8;

    public static readonly string[] SupportedLayoutModes =
    [
        "smart-desktop",
        "spatial-grid",
        "whiteboard-auto-arranged",
        "desktop",
        "classic",
    ];

    public DesktopAppState() : this(
        "smart-desktop",
        false,
        "intochat",
        "floating-glass",
        10,
        "Smart Desktop Ready",
        ["ino"],
        [],
        null,
        [],
        [],
        "")
    { }

    // ── Domain Invariants ────────────────────────────────────────────────────

    [Invariant("LayoutMode is always a supported desktop layout mode."), JsonIgnore]
    public bool ValidLayoutMode => SupportedLayoutModes.Contains(LayoutMode, StringComparer.Ordinal);

    [Invariant("At most 8 windows can be open simultaneously."), JsonIgnore]
    public bool WindowCapHolds => (Windows ?? []).Length <= MaxWindows;

    [Invariant("Every open window holds at most 8 tabs."), JsonIgnore]
    public bool TabCapHolds => (Windows ?? []).All(static w => (w.Tabs ?? []).Length <= MaxTabsPerWindow);

    [Invariant("The desktop surface itself is never pinned to the dock."), JsonIgnore]
    public bool DesktopNeverPinned =>
        !(PinnedApps ?? []).Contains("desktop", StringComparer.OrdinalIgnoreCase)
        && !(Pins ?? []).Any(static p => string.Equals(p.Id, "desktop", StringComparison.OrdinalIgnoreCase));

    [Invariant("Open windows carry unique non-empty IDs and FocusedViewId (when set) references an open tab."), JsonIgnore]
    public bool UniqueWindowIdsAndValidFocus
    {
        get
        {
            var wins = Windows ?? [];
            if (wins.Any(static w => w is null || string.IsNullOrWhiteSpace(w.Id)))
            {
                return false;
            }

            if (wins.Select(static w => w.Id).Distinct(StringComparer.Ordinal).Count() != wins.Length)
            {
                return false;
            }

            return FocusedViewId is null
                || wins.Length == 0
                || wins.Any(w => (w.Tabs ?? []).Any(t => string.Equals(t.Id, FocusedViewId, StringComparison.Ordinal)));
        }
    }

    [Invariant("Pinned apps and favourites stay within dock bounds with non-empty identifiers."), JsonIgnore]
    public bool BoundedPinsAndFavourites =>
        (Pins ?? []).Length <= MaxPins
        && (PinnedApps ?? []).Length <= MaxPins
        && (Favourites ?? []).Length <= MaxFavourites
        && (PinnedApps ?? []).All(static id => !string.IsNullOrWhiteSpace(id));

    // ── Declarative Domain Rules ─────────────────────────────────────────────

    public static Rule SupportedLayoutRule(string? mode) => Decision.Rule(
        "invalid_layout",
        mode,
        static m => string.IsNullOrWhiteSpace(m) || !SupportedLayoutModes.Contains(m.Trim().ToLowerInvariant(), StringComparer.Ordinal),
        "Desktop layout mode must be smart-desktop, spatial-grid, whiteboard-auto-arranged, desktop, or classic.");

    public static Rule OpenableAppRule(string? appId) => Decision.Rule(
        "invalid_app",
        appId,
        static id => string.IsNullOrWhiteSpace(id) || string.Equals(id.Trim(), "desktop", StringComparison.OrdinalIgnoreCase),
        "Specify a non-desktop application identifier to open in a window.");

    public Rule WindowLimitRule => WindowLimitInRule(Windows);

    public static Rule WindowLimitInRule(IReadOnlyList<WorkspaceWindow>? windows) => Decision.Rule(
        "max_windows",
        windows,
        static wins => (wins ?? []).Count(static w => w.Tabs is { Length: > 0 }) >= MaxWindows,
        $"At most {MaxWindows} windows can be open simultaneously.");

    public static Rule UnpinnableAppRule(string? appId) => Decision.Rule(
        "core_dock_app",
        appId,
        static id => string.IsNullOrWhiteSpace(id)
            || string.Equals(id.Trim(), "ino", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id.Trim(), "desktop", StringComparison.OrdinalIgnoreCase),
        "Core shell surfaces ('ino', 'desktop') cannot be pinned or unpinned.");

    public Rule PinLimitRule(string? appId) => Decision.Rule(
        "max_pins",
        (Existing: PinnedApps ?? [], Candidate: appId?.Trim() ?? string.Empty),
        static x => x.Existing.Length >= MaxPins && !x.Existing.Contains(x.Candidate, StringComparer.OrdinalIgnoreCase),
        $"At most {MaxPins} apps can be pinned to the dock.");

    public Rule WindowExistsRule(string? windowId) => WindowExistsInRule(Windows, windowId);

    public static Rule WindowExistsInRule(IReadOnlyList<WorkspaceWindow>? windows, string? windowId) => Decision.Rule(
        "window_not_found",
        (Windows: windows ?? [], WindowId: windowId?.Trim() ?? string.Empty),
        static x => string.IsNullOrEmpty(x.WindowId)
            || !x.Windows.Any(w => w.Tabs is { Length: > 0 } && string.Equals(w.Id, x.WindowId, StringComparison.Ordinal)),
        "Target window does not exist.");

    public Rule TabLimitRule(string? windowId) => TabLimitInRule(Windows, windowId);

    public static Rule TabLimitInRule(IReadOnlyList<WorkspaceWindow>? windows, string? windowId) => Decision.Rule(
        "max_tabs_per_window",
        (Windows: windows ?? [], WindowId: windowId?.Trim() ?? string.Empty),
        static x => x.Windows.FirstOrDefault(w => string.Equals(w.Id, x.WindowId, StringComparison.Ordinal)) is { Tabs.Length: >= MaxTabsPerWindow },
        $"At most {MaxTabsPerWindow} tabs can be open in a single window.");

    public Rule TabTargetExistsRule(string? target) => TabTargetExistsInRule(Windows, target);

    public static Rule TabTargetExistsInRule(IReadOnlyList<WorkspaceWindow>? windows, string? target) => Decision.Rule(
        "tab_not_found",
        (Windows: windows ?? [], Target: target?.Trim() ?? string.Empty),
        static x => !TryResolveTabTarget(x.Windows, x.Target, out _, out _),
        "Target window tab does not exist.");

    public Rule CloseTargetExistsRule(string? target) => CloseTargetExistsInRule(Windows, target);

    public static Rule CloseTargetExistsInRule(IReadOnlyList<WorkspaceWindow>? windows, string? target) => Decision.Rule(
        "window_not_found",
        (Windows: windows ?? [], Target: target?.Trim() ?? string.Empty),
        static x => !TryResolveCloseTarget(x.Windows, x.Target, out _, out _),
        "Target window or tab to close does not exist.");

    public static Rule ValidWidgetRule(string? widget) => Decision.Rule(
        "invalid_widget",
        widget,
        static w => string.IsNullOrWhiteSpace(w),
        "Widget identifier is required.");

    public Rule WidgetOpenRule(string? widget) => WidgetOpenInRule(ParseWidgets(WidgetsText), widget);

    public static Rule WidgetOpenInRule(IReadOnlyList<string>? openWidgets, string? widget) => Decision.Rule(
        "widget_not_open",
        (Widgets: openWidgets ?? [], Id: NormalizeWidgetId(widget)),
        static x => string.IsNullOrEmpty(x.Id) || !x.Widgets.Contains(x.Id, StringComparer.Ordinal),
        "Widget is not currently open.");

    // ── Aggregate Commands & Transitions ─────────────────────────────────────

    public DesktopAppState ToggleLayoutMode() => this with
    {
        LayoutMode = LayoutMode == "smart-desktop" ? "spatial-grid" : "smart-desktop",
    };

    public Decision<DesktopAppState> SelectLayoutMode(
        [Sample("smart-desktop", "spatial-grid", "invalid-layout")] string mode)
    {
        if (Decision.Check("layout", SupportedLayoutRule(mode)) is { } refused)
        {
            return refused;
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return Decision.Accept(this with
        {
            LayoutMode = normalized,
            Status = IsNarrow ? "Smart Desktop (Compact)" : $"Smart Desktop ({normalized})",
        });
    }

    public DesktopAppState AutoArrange()
    {
        var pinnedList = PinnedApps is { Length: > 0 } ? PinnedApps : ["ino"];
        return this with
        {
            LayoutMode = "whiteboard-auto-arranged",
            PinnedApps = pinnedList,
            Status = $"Whiteboard Auto-Arranged · {pinnedList.Length} pinned sticky-note app(s) locked on top ({string.Join(", ", pinnedList)})",
        };
    }

    public Decision<DesktopAppState> OpenWindow([Sample("calculator", "desktop")] string appId)
    {
        if (Decision.Check("open", OpenableAppRule(appId)) is { } badApp)
        {
            return badApp;
        }

        var normalized = appId.Trim().ToLowerInvariant();
        var appKey = $"apps/{normalized}";
        var windows = (Windows ?? []).Where(static w => w.Tabs is { Length: > 0 }).ToList();

        foreach (var existingWin in windows)
        {
            if (existingWin.Tabs.FirstOrDefault(t =>
                    string.Equals(t.Id, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)) is { } existingTab)
            {
                var focusedWins = windows
                    .Select(w => w.Id == existingWin.Id ? w with { ActiveTabId = existingTab.Id } : w)
                    .ToArray();
                return Decision.Accept(this with
                {
                    Windows = focusedWins,
                    FocusedViewId = existingTab.Id,
                });
            }
        }

        if (Decision.Check("open", WindowLimitInRule(windows)) is { } capped)
        {
            return capped;
        }

        var takenWinIds = windows.Select(static w => w.Id).ToHashSet(StringComparer.Ordinal);
        var nextWinId = Enumerable.Range(1, 99).Select(static n => $"w-{n}").First(id => !takenWinIds.Contains(id));
        var tab = new WorkspaceViewRef(normalized, appKey, "full", string.Empty);
        WorkspaceWindow[] nextWindows = [.. windows, new WorkspaceWindow(nextWinId, [tab], tab.Id)];
        return Decision.Accept(this with
        {
            Windows = nextWindows,
            FocusedViewId = tab.Id,
        });
    }

    public Decision<DesktopAppState> FocusWindow([Sample("w-1", "w-missing")] string windowId)
    {
        if (Decision.Check("focus", WindowExistsRule(windowId)) is { } refused)
        {
            return refused;
        }

        var trimmed = windowId.Trim();
        var win = Windows.First(w => string.Equals(w.Id, trimmed, StringComparison.Ordinal));
        return Decision.Accept(this with { FocusedViewId = win.ActiveTabId });
    }

    public Decision<DesktopAppState> AddWindowTab(
        [Sample("w-1", "w-missing")] string windowId,
        [Sample("ino")] string appId = "ino")
    {
        if (Decision.Check("newtab", OpenableAppRule(appId), WindowExistsRule(windowId), TabLimitRule(windowId)) is { } refused)
        {
            return refused;
        }

        var cleanWinId = windowId.Trim();
        var cleanApp = appId.Trim().ToLowerInvariant();
        var windows = (Windows ?? []).Where(static w => w.Tabs is { Length: > 0 }).ToList();
        var takenTabIds = windows.SelectMany(static w => w.Tabs).Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        var tabId = cleanApp;
        for (var n = 2; !takenTabIds.Add(tabId); n++)
        {
            tabId = $"{cleanApp}-{n}";
        }

        var newTab = new WorkspaceViewRef(tabId, $"apps/{cleanApp}", "full", string.Empty);
        var nextWindows = windows
            .Select(w => w.Id == cleanWinId ? new WorkspaceWindow(w.Id, [.. w.Tabs, newTab], newTab.Id) : w)
            .ToArray();
        return Decision.Accept(this with
        {
            Windows = nextWindows,
            FocusedViewId = newTab.Id,
        });
    }

    public Decision<DesktopAppState> SelectWindowTab([Sample("w-1/0", "w-1/99")] string target)
    {
        if (Decision.Check("tab", TabTargetExistsRule(target)) is { } refused)
        {
            return refused;
        }

        TryResolveTabTarget(Windows, target.Trim(), out var win, out var chosen);
        var nextWindows = Windows
            .Select(w => w.Id == win!.Id ? w with { ActiveTabId = chosen!.Id } : w)
            .ToArray();
        return Decision.Accept(this with
        {
            Windows = nextWindows,
            FocusedViewId = chosen!.Id,
        });
    }

    public Decision<DesktopAppState> ToggleWindowFavourite([Sample("w-1", "w-missing")] string windowId)
    {
        if (Decision.Check("favourite", WindowExistsRule(windowId)) is { } refused)
        {
            return refused;
        }

        var win = Windows.First(w => string.Equals(w.Id, windowId.Trim(), StringComparison.Ordinal));
        var activeTab = win.Tabs.FirstOrDefault(t => t.Id == win.ActiveTabId) ?? win.Tabs[^1];
        var currentFavs = Favourites ?? [];
        var exists = currentFavs.Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));
        WorkspaceViewRef[] nextFavs = exists
            ? [.. currentFavs.Where(f => !string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase))]
            : [.. currentFavs.Take(4), new WorkspaceViewRef(activeTab.Id, activeTab.App, string.Empty, string.Empty)];
        return Decision.Accept(this with { Favourites = nextFavs });
    }

    public Decision<DesktopAppState> CloseWindowTarget([Sample("w-1/0", "w-missing")] string target)
    {
        if (Decision.Check("close", CloseTargetExistsRule(target)) is { } refused)
        {
            return refused;
        }

        var trimmed = target.Trim();
        var slash = trimmed.LastIndexOf('/');
        TryResolveCloseTarget(Windows, trimmed, out var window, out var tabIndex);
        var windows = (Windows ?? []).Where(static w => w.Tabs is { Length: > 0 }).ToList();

        if (window!.Tabs.Length <= 1 || slash < 0)
        {
            var remaining = windows.Where(w => w.Id != window.Id).ToArray();
            var nextFocus = remaining.Any(w => w.Tabs.Any(t => t.Id == FocusedViewId))
                ? FocusedViewId
                : remaining.LastOrDefault()?.ActiveTabId;
            return Decision.Accept(this with { Windows = remaining, FocusedViewId = nextFocus });
        }

        var closedTabId = window.Tabs[tabIndex].Id;
        var nextTabs = window.Tabs.Where((_, i) => i != tabIndex).ToArray();
        var nextActive = window.ActiveTabId == closedTabId ? nextTabs[Math.Max(0, tabIndex - 1)].Id : window.ActiveTabId;
        var updatedWindows = windows
            .Select(w => w.Id == window.Id ? new WorkspaceWindow(window.Id, nextTabs, nextActive) : w)
            .ToArray();
        return Decision.Accept(this with
        {
            Windows = updatedWindows,
            FocusedViewId = FocusedViewId == closedTabId ? nextActive : FocusedViewId,
        });
    }

    public Decision<DesktopAppState> PinApp([Sample("photocraft", "ino", "desktop")] string appId)
    {
        var normalized = appId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Decision.Check("pin", UnpinnableAppRule(normalized), PinLimitRule(normalized)) is { } refused)
        {
            return refused;
        }

        var existingPins = PinnedApps is { Length: > 0 } ? PinnedApps : ["ino"];
        var nextPinnedApps = existingPins.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? existingPins
            : [.. existingPins, normalized];
        var currentRefs = Pins ?? [];
        var nextRefs = currentRefs.Any(p => string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase))
            ? currentRefs
            : [.. currentRefs, new WorkspaceViewRef(normalized, $"apps/{normalized}", "full", string.Empty)];
        return Decision.Accept(this with { PinnedApps = nextPinnedApps, Pins = nextRefs });
    }

    public Decision<DesktopAppState> UnpinApp([Sample("photocraft", "ino")] string appId)
    {
        var normalized = appId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Decision.Check("unpin", UnpinnableAppRule(normalized)) is { } refused)
        {
            return refused;
        }

        var existingPins = PinnedApps is { Length: > 0 } ? PinnedApps : ["ino"];
        var nextPinnedApps = existingPins
            .Where(p => !string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nextRefs = (Pins ?? [])
            .Where(p => !string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Decision.Accept(this with { PinnedApps = nextPinnedApps, Pins = nextRefs });
    }

    [IgnoreExplore]
    public Decision<DesktopAppState> PinOrUnpin(string? rawAppId, bool pinned = true)
    {
        var appId = string.IsNullOrWhiteSpace(rawAppId) ? "photocraft" : rawAppId.Trim().ToLowerInvariant();
        if (Decision.Check("pin", UnpinnableAppRule(appId)) is { } refused)
        {
            return refused;
        }

        if (pinned && Decision.Check("pin", PinLimitRule(appId)) is { } capped)
        {
            return capped;
        }

        var existingPins = PinnedApps is { Length: > 0 } ? PinnedApps : ["ino"];
        var nextPinnedApps = pinned
            ? (existingPins.Contains(appId, StringComparer.OrdinalIgnoreCase) ? existingPins : [.. existingPins, appId])
            : existingPins.Where(p => !string.Equals(p, appId, StringComparison.OrdinalIgnoreCase)).ToArray();

        return Decision.Accept(this with { PinnedApps = nextPinnedApps });
    }

    public Decision<DesktopAppState> OpenWidget([Sample("clock", "")] string widget)
    {
        if (Decision.Check("widget-open", ValidWidgetRule(widget)) is { } refused)
        {
            return refused;
        }

        var id = NormalizeWidgetId(widget)!;
        var current = ParseWidgets(WidgetsText);
        if (current.Contains(id, StringComparer.Ordinal))
        {
            return Decision.Accept(this);
        }

        string[] updated = [.. current, id];
        return Decision.Accept(this with { WidgetsText = string.Join(" · ", updated) });
    }

    public Decision<DesktopAppState> CloseWidget([Sample("clock", "weather")] string widget)
    {
        if (Decision.Check("widget-close", ValidWidgetRule(widget), WidgetOpenRule(widget)) is { } refused)
        {
            return refused;
        }

        var id = NormalizeWidgetId(widget)!;
        var updated = ParseWidgets(WidgetsText).Where(w => !string.Equals(w, id, StringComparison.Ordinal)).ToArray();
        return Decision.Accept(this with { WidgetsText = string.Join(" · ", updated) });
    }

    // ── Normalization & Workspace Helpers ────────────────────────────────────

    public static string NormalizeLayoutMode(string? mode, string fallback = "smart-desktop")
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            var candidate = mode.Trim().ToLowerInvariant();
            if (SupportedLayoutModes.Contains(candidate, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        return SupportedLayoutModes.Contains(fallback, StringComparer.Ordinal) ? fallback : "smart-desktop";
    }

    public static string? NormalizeWidgetId(string? widget)
        => string.IsNullOrWhiteSpace(widget) ? null : widget.Trim().ToLowerInvariant();

    public static string[] ParseWidgets(string? widgetsText) =>
        string.IsNullOrWhiteSpace(widgetsText)
            ? []
            : widgetsText
                .Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static bool TryResolveTabTarget(
        IReadOnlyList<WorkspaceWindow>? windows,
        string? target,
        out WorkspaceWindow? window,
        out WorkspaceViewRef? chosenTab)
    {
        window = null;
        chosenTab = null;
        if (windows is null || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var trimmed = target.Trim();
        var sep = trimmed.LastIndexOf('/');
        if (sep < 0)
        {
            sep = trimmed.LastIndexOf(':');
        }

        if (sep <= 0)
        {
            return false;
        }

        var winId = trimmed[..sep];
        var tabPart = trimmed[(sep + 1)..];
        var found = windows.FirstOrDefault(win => win.Tabs is { Length: > 0 } && string.Equals(win.Id, winId, StringComparison.Ordinal));
        if (found is null)
        {
            return false;
        }

        var chosen = int.TryParse(tabPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < found.Tabs.Length
            ? found.Tabs[idx]
            : found.Tabs.FirstOrDefault(t => string.Equals(t.Id, tabPart, StringComparison.Ordinal));
        if (chosen is null)
        {
            return false;
        }

        window = found;
        chosenTab = chosen;
        return true;
    }

    private static bool TryResolveCloseTarget(
        IReadOnlyList<WorkspaceWindow>? windows,
        string? target,
        out WorkspaceWindow? window,
        out int tabIndex)
    {
        window = null;
        tabIndex = -1;
        if (windows is null || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var trimmed = target.Trim();
        var slash = trimmed.LastIndexOf('/');
        var winId = slash >= 0 ? trimmed[..slash] : trimmed;
        var indexStr = slash >= 0 ? trimmed[(slash + 1)..] : string.Empty;
        var found = windows.FirstOrDefault(w => w.Tabs is { Length: > 0 } && string.Equals(w.Id, winId, StringComparison.Ordinal));
        if (found is null)
        {
            return false;
        }

        if (slash < 0 || !int.TryParse(indexStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resolved))
        {
            resolved = Math.Max(0, Array.FindIndex(found.Tabs, t => t.Id == found.ActiveTabId));
        }

        if (resolved < 0 || resolved >= found.Tabs.Length)
        {
            return false;
        }

        window = found;
        tabIndex = resolved;
        return true;
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
        string? brain = null)
    {
        var appName = rawAppName.Trim().ToLowerInvariant();
        if (!BrainId.IsValid(brain) || appName == "desktop" || !IntoChat.OS.Host.Primitives.AppRef.TryOf(new BrainId(brain), appName, out var appRef)) { return (null, []); }
        var appKey = appRef.Key;
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

    public static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) CloseWorkspaceApp(WorkspaceState s, string rawAppName, string? brain = null)
    {
        if (!BrainId.IsValid(brain)) { return (null, []); }
        var normalized = rawAppName.Trim().ToLowerInvariant();
        if (!IntoChat.OS.Host.Primitives.AppRef.TryOf(new BrainId(brain), normalized, out var appRef)) { return (null, []); }
        var appKey = appRef.Key;
        var closedIds = new List<string>();
        var nextWindows = new List<WorkspaceWindow>();
        var changed = false;

        foreach (var w in s.Windows.Where(w => w.Tabs is { Length: > 0 }))
        {
            var keptTabs = w.Tabs.Where(t =>
                !string.Equals(t.App, appKey, StringComparison.OrdinalIgnoreCase)).ToArray();
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

    public static (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds) ToggleWorkspaceFavourite(WorkspaceState s, string windowId, string? brain = null)
    {
        if (string.IsNullOrWhiteSpace(brain)) { return (null, []); }
        var w = s.Windows.FirstOrDefault(win => win.Id == windowId);
        var activeTab = w?.Tabs.FirstOrDefault(t => t.Id == w.ActiveTabId) ?? w?.Tabs.LastOrDefault();
        if (activeTab is null) { return (null, []); }

        var appId = IntoChat.OS.Host.Primitives.AppRef.NameOf(brain, activeTab.App)
            ?? activeTab.Id;
        var exists = s.Favourites.Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));
        WorkspaceViewRef[] nextFavs = exists
            ? [.. s.Favourites.Where(f => !string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase))]
            : [.. s.Favourites.Take(4), new WorkspaceViewRef(appId, activeTab.App, string.Empty, string.Empty)];
        return (BuildUpdate(s, s.Windows, favourites: nextFavs), []);
    }

    public override string ToString()
        => $"{LayoutMode} · {(Windows ?? []).Length} win · {(PinnedApps ?? []).Length} pin";
}
