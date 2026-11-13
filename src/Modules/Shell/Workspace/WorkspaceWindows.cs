namespace IntoChat.OS.Modules.Flutter.Workspace;

// Derived windows always satisfy Apply's rules: ids are w-<n>, at most MaxWindows windows of at most MaxTabs
// tabs. Tab ids are view ids, which CheckViews already bounds and makes unique, so tabs need no mapping.
internal static class WorkspaceWindows
{
    public const int MaxWindows = 8, MaxTabs = 8;

    public static WorkspaceWindow[] Derive(WorkspaceViewRef[] openViews, string? focusedViewId = null)
        => Reconcile([], openViews, focusedViewId);

    // Keeps each window's tabs that are still open, refreshed from OpenViews, bounds window/tab count via MRU eviction
    // preserving the focused view, and places every open view no window holds.
    public static WorkspaceWindow[] Reconcile(WorkspaceWindow[] windows, WorkspaceViewRef[] openViews, string? focusedViewId = null)
    {
        var open = (openViews ?? []).Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Id))
            .ToDictionary(view => view.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var kept = (windows ?? [])
            .Where(window => window is not null)
            .Select(window =>
            {
                var validTabs = (window.Tabs ?? [])
                    .Where(tab => tab is not null && open.ContainsKey(tab.Id) && seen.Add(tab.Id))
                    .Select(tab => open[tab.Id])
                    .ToList();

                // Enforce bounded tab list: at most MaxTabs via MRU eviction of oldest unfocused tab
                while (validTabs.Count > MaxTabs)
                {
                    // Evict oldest unfocused tab (neither focusedViewId nor activeTabId if possible, else not focusedViewId)
                    var evictIdx = validTabs.FindIndex(t => t.Id != focusedViewId && t.Id != window.ActiveTabId);
                    if (evictIdx < 0)
                    {
                        evictIdx = validTabs.FindIndex(t => t.Id != focusedViewId);
                    }
                    if (evictIdx < 0)
                    {
                        evictIdx = 0;
                    }
                    validTabs.RemoveAt(evictIdx);
                }

                var nextActive = validTabs.Any(tab => tab.Id == focusedViewId)
                    ? focusedViewId!
                    : (validTabs.Any(tab => tab.Id == window.ActiveTabId) ? window.ActiveTabId : (validTabs.Count > 0 ? validTabs[^1].Id : string.Empty));

                return window with
                {
                    Tabs = [.. validTabs],
                    ActiveTabId = nextActive,
                };
            })
            .Where(window => window.Tabs.Length > 0)
            .ToList();

        // Enforce maximum window limit: at most MaxWindows via MRU eviction of oldest unfocused window
        while (kept.Count > MaxWindows)
        {
            var evictIdx = kept.FindIndex(w => !w.Tabs.Any(t => t.Id == focusedViewId));
            if (evictIdx < 0)
            {
                evictIdx = 0;
            }
            kept.RemoveAt(evictIdx);
        }

        var held = kept.SelectMany(window => window.Tabs).Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
        var unplaced = (openViews ?? []).Where(view => view is not null && !held.Contains(view.Id)).ToList();
        return Place(kept, unplaced, focusedViewId);
    }

    // A new single-tab window per view while there is room for windows, then into the first window with room.
    // If the 8x8 cap is exceeded, evicts the oldest unfocused tab from an unfocused window (MRU eviction).
    private static WorkspaceWindow[] Place(List<WorkspaceWindow> windows, List<WorkspaceViewRef> views, string? focusedViewId)
    {
        var ids = windows.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        var next = 1;

        foreach (var view in views)
        {
            if (windows.Count < MaxWindows)
            {
                while (!ids.Add("w-" + next)) { next++; }
                var winId = "w-" + next;
                windows.Add(new(winId, [view], view.Id));
                continue;
            }

            var at = windows.FindIndex(window => window.Tabs.Length < MaxTabs);
            if (at >= 0)
            {
                var targetWin = windows[at];
                var nextActive = view.Id == focusedViewId ? view.Id : targetWin.ActiveTabId;
                windows[at] = targetWin with
                {
                    Tabs = [.. targetWin.Tabs, view],
                    ActiveTabId = nextActive,
                };
                continue;
            }

            // Cap exceeded: all 8 windows have MaxTabs (8 tabs). Evict oldest unfocused tab from oldest unfocused window.
            var unfocusedWinIdx = windows.FindIndex(w => !w.Tabs.Any(t => t.Id == focusedViewId));
            if (unfocusedWinIdx < 0)
            {
                unfocusedWinIdx = 0;
            }

            var win = windows[unfocusedWinIdx];
            var evictTabIdx = Array.FindIndex(win.Tabs, t => t.Id != focusedViewId && t.Id != win.ActiveTabId);
            if (evictTabIdx < 0)
            {
                evictTabIdx = Array.FindIndex(win.Tabs, t => t.Id != focusedViewId);
            }
            if (evictTabIdx < 0)
            {
                evictTabIdx = 0;
            }

            var nextTabs = win.Tabs.Where((_, i) => i != evictTabIdx).Append(view).ToArray();
            var nextWinActive = view.Id == focusedViewId ? view.Id : (nextTabs.Any(t => t.Id == win.ActiveTabId) ? win.ActiveTabId : view.Id);
            windows[unfocusedWinIdx] = win with
            {
                Tabs = nextTabs,
                ActiveTabId = nextWinActive,
            };
        }

        return [.. windows];
    }
}
