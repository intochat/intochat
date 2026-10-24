namespace IntoChat.OS.Flutter.Workspace;

internal static class WorkspaceWindows
{
    public static WorkspaceWindow[] Derive(WorkspaceViewRef[] openViews)
        => [.. openViews.Select(view => new WorkspaceWindow("w-" + view.Id, [view], view.Id))];

    // Keeps each window's tabs that are still open, refreshed from OpenViews, and gives every open view
    // no window holds a window of its own.
    public static WorkspaceWindow[] Reconcile(WorkspaceWindow[] windows, WorkspaceViewRef[] openViews)
    {
        var open = openViews.ToDictionary(view => view.Id, StringComparer.Ordinal);
        var kept = windows
            .Select(window => window with { Tabs = [.. window.Tabs.Where(tab => open.ContainsKey(tab.Id)).Select(tab => open[tab.Id])] })
            .Where(window => window.Tabs.Length > 0)
            .Select(window => window.Tabs.Any(tab => tab.Id == window.ActiveTabId) ? window : window with { ActiveTabId = window.Tabs[^1].Id })
            .ToArray();
        var held = kept.SelectMany(window => window.Tabs).Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
        var ids = kept.Select(window => window.Id).ToHashSet(StringComparer.Ordinal);
        var added = Derive([.. openViews.Where(view => !held.Contains(view.Id))]).Select(window =>
        {
            var id = window.Id;
            for (var n = 2; !ids.Add(id); n++) { id = window.Id + "-" + n; }
            return window with { Id = id };
        });
        return [.. kept, .. added];
    }
}
