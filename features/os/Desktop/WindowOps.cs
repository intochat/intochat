using IntoChat.OS.Modules.Flutter.Workspace;

// One window, tab or favourite operation: an update to store, a refusal toast, or neither (a no-op).
public sealed record WindowChange(WorkspaceUpdate? Update, string? Refusal);

// Pure window, tab and favourite rules. No I/O: the scripts store the update and toast the refusal.
public static class WindowOps
{
    public const int MaxWindows = 8;
    public const int MaxTabsPerWindow = 8;
    public const int MaxFavouriteApps = 5;
    public const int MaxFavouriteViews = 5;

    public const string WindowsRefusal = "You can have at most 8 windows. Close one to open another.";
    public const string TabsRefusal = "A window holds at most 8 tabs.";
    public const string AppsRefusal = "Favourites hold at most 5 apps.";
    public const string ViewsRefusal = "Favourites hold at most 5 views.";

    private static readonly WindowChange NoOp = new(null, null);

    // `visible` is the desktop's tab visibility (hidden apps never show); windows with no visible tab
    // do not count toward the window cap and a window's active tab is its visible one. Null: all visible.
    public static WindowChange OpenApp(WorkspaceState s, string name, WorkspaceViewRef view, Func<WorkspaceViewRef, bool>? visible = null)
    {
        foreach (var window in s.Windows)
        {
            if (window.Tabs.FirstOrDefault(tab => tab.App == view.App) is { } tab)
            {
                return Change(s, Replace(s.Windows, window with { ActiveTabId = tab.Id }), tab.Id);
            }
        }
        return NewWindow(s, name, view, visible);
    }

    public static WindowChange NewTab(WorkspaceState s, string? windowId, string name, WorkspaceViewRef view, Func<WorkspaceViewRef, bool>? visible = null)
    {
        visible ??= _ => true;
        var target = windowId is { } id
            ? s.Windows.FirstOrDefault(window => window.Id == id)
            : s.Windows.FirstOrDefault(window => window.Tabs.Any(tab => tab.Id == s.FocusedViewId)) ?? s.Windows.LastOrDefault(window => window.Tabs.Any(visible));
        if (target is null) { return windowId is null || s.Windows.Length == 0 ? NewWindow(s, name, view, visible) : NoOp; }
        if (target.Tabs.Length >= MaxTabsPerWindow) { return new(null, TabsRefusal); }
        var tab = view with { Id = TabId(s, name) };
        return Change(s, Replace(s.Windows, new WorkspaceWindow(target.Id, [.. target.Tabs, tab], tab.Id)), tab.Id);
    }

    public static WindowChange SelectTab(WorkspaceState s, string windowId, string tabId)
    {
        var window = s.Windows.FirstOrDefault(w => w.Id == windowId);
        if (window is null || window.Tabs.All(tab => tab.Id != tabId)) { return NoOp; }
        if (window.ActiveTabId == tabId && s.FocusedViewId == tabId) { return NoOp; }
        return Change(s, Replace(s.Windows, window with { ActiveTabId = tabId }), tabId);
    }

    public static WindowChange Focus(WorkspaceState s, string windowId) =>
        s.Windows.FirstOrDefault(w => w.Id == windowId) is { } window ? Change(s, s.Windows, window.ActiveTabId) : NoOp;

    public static WindowChange CloseTab(WorkspaceState s, string windowId, string tabId)
    {
        var window = s.Windows.FirstOrDefault(w => w.Id == windowId);
        var index = window is null ? -1 : Array.FindIndex(window.Tabs, tab => tab.Id == tabId);
        if (window is null || index < 0) { return NoOp; }
        WorkspaceWindow[] windows;
        if (window.Tabs.Length == 1)
        {
            windows = [.. s.Windows.Where(w => w.Id != windowId)];
            var focus = s.FocusedViewId != tabId && windows.Any(w => w.ActiveTabId == s.FocusedViewId)
                ? s.FocusedViewId
                : windows.LastOrDefault()?.ActiveTabId;
            return Change(s, windows, focus);
        }
        WorkspaceViewRef[] tabs = [.. window.Tabs.Where((_, i) => i != index)];
        var active = window.ActiveTabId == tabId ? tabs[Math.Max(0, index - 1)].Id : window.ActiveTabId;
        windows = Replace(s.Windows, new WorkspaceWindow(window.Id, tabs, active));
        // Focus follows the window when it held the closed tab; otherwise it stays where it was.
        var focused = s.FocusedViewId == tabId ? active : s.FocusedViewId;
        return Change(s, windows, focused);
    }

    public static WindowChange ToggleFavourite(WorkspaceState s, string windowId, Func<string, string> appName, Func<WorkspaceViewRef, bool>? visible = null)
    {
        var window = s.Windows.FirstOrDefault(w => w.Id == windowId);
        var tab = window is null ? null : ActiveTab(window, visible ?? (_ => true));
        if (tab is null) { return NoOp; }
        var favourite = IsApp(tab) ? new WorkspaceViewRef("", tab.App, "", "") : new WorkspaceViewRef("", tab.App, tab.Variant, tab.Context);
        WorkspaceViewRef[] favourites;
        if (s.Favourites.Any(f => Same(f, favourite)))
        {
            favourites = [.. s.Favourites.Where(f => !Same(f, favourite))];
        }
        else
        {
            var isApp = favourite.Variant == "";
            var count = s.Favourites.Count(f => isApp ? f.Variant == "" : f.Variant != "");
            if (isApp && count >= MaxFavouriteApps) { return new(null, AppsRefusal); }
            if (!isApp && count >= MaxFavouriteViews) { return new(null, ViewsRefusal); }
            var name = appName(tab.App);
            var id = isApp ? (string.IsNullOrWhiteSpace(name) ? tab.App : name) : "v-" + tab.Id;
            favourites = [.. s.Favourites, favourite with { Id = Unique(id, s.Favourites.Select(f => f.Id)) }];
        }
        return new(Update(s, s.Windows, s.FocusedViewId) with { Favourites = favourites }, null);
    }

    // The name if no tab holds it, else name-2, name-3, ... unique across every window.
    public static string TabId(WorkspaceState s, string name) => Unique(name, s.Windows.SelectMany(window => window.Tabs).Select(tab => tab.Id));

    // The server holds view ids to 128 chars: the base is cut so that it plus its -n suffix fits.
    private const int MaxId = 128;

    private static string Unique(string name, IEnumerable<string> ids)
    {
        var taken = ids.ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? "" : "-" + n;
            var id = name[..Math.Min(name.Length, MaxId - suffix.Length)] + suffix;
            if (!taken.Contains(id)) { return id; }
        }
    }

    public static bool IsFavourite(WorkspaceState s, WorkspaceViewRef tab)
    {
        var key = IsApp(tab) ? new WorkspaceViewRef("", tab.App, "", "") : tab;
        return s.Favourites.Any(f => Same(f, key));
    }

    // The tab the desktop shows for a window: its active tab if visible, else its last visible tab.
    public static WorkspaceViewRef? ActiveTab(WorkspaceWindow window, Func<WorkspaceViewRef, bool> visible)
        => window.Tabs.Where(visible).ToArray() is { Length: > 0 } tabs ? tabs.FirstOrDefault(tab => tab.Id == window.ActiveTabId) ?? tabs[^1] : null;

    private static WindowChange NewWindow(WorkspaceState s, string name, WorkspaceViewRef view, Func<WorkspaceViewRef, bool>? visible)
    {
        visible ??= _ => true;
        if (s.Windows.Count(window => window.Tabs.Any(visible)) >= MaxWindows) { return new(null, WindowsRefusal); }
        var tab = view with { Id = TabId(s, name) };
        var windows = s.Windows;
        WorkspaceViewRef[] tabs = [tab];
        // The stored cap counts every window: at it, the hidden-only windows fold into the new one (unseen).
        if (windows.Length >= MaxWindows)
        {
            var hidden = windows.Where(window => !window.Tabs.Any(visible)).ToArray();
            tabs = [.. hidden.SelectMany(window => window.Tabs), tab];
            if (tabs.Length > MaxTabsPerWindow) { return new(null, WindowsRefusal); }
            windows = [.. windows.Except(hidden)];
        }
        var id = WindowId(s with { Windows = windows });
        return Change(s, [.. windows, new WorkspaceWindow(id, tabs, tab.Id)], tab.Id);
    }

    // w-<n>, the smallest unused positive integer, as Task 1 derives them.
    private static string WindowId(WorkspaceState s)
    {
        var taken = s.Windows.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            if (!taken.Contains("w-" + n)) { return "w-" + n; }
        }
    }

    private static bool IsApp(WorkspaceViewRef tab) => tab.Variant == "full" && tab.Context == "";

    private static bool Same(WorkspaceViewRef a, WorkspaceViewRef b) => a.App == b.App && a.Variant == b.Variant && a.Context == b.Context;

    private static WorkspaceWindow[] Replace(WorkspaceWindow[] windows, WorkspaceWindow next) => [.. windows.Select(w => w.Id == next.Id ? next : w)];

    private static WindowChange Change(WorkspaceState s, WorkspaceWindow[] windows, string? focus) => new(Update(s, windows, focus), null);

    private static WorkspaceUpdate Update(WorkspaceState s, WorkspaceWindow[] windows, string? focus) =>
        new([.. windows.SelectMany(window => window.Tabs)], focus, s.Pins) { Windows = windows };
}
