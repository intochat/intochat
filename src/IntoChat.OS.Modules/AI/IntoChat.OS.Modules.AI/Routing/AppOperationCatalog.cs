using System.Collections.Concurrent;

namespace IntoChat.OS.AI.Routing;

internal sealed record MenuOperation(string App, string Name, string? Description);

// The installed apps' operations, kept as apps change, so a route does not read every app.
// Ready only after a full scan; a signal before that scan is folded in and does not hide the rest.
internal sealed class AppOperationCatalog
{
    private sealed class Menu
    {
        public Dictionary<string, MenuOperation[]> Apps { get; } = new(StringComparer.Ordinal);
        public bool Ready;
    }

    private readonly ConcurrentDictionary<string, Menu> _menus = new(StringComparer.Ordinal);

    public bool TryGet(string brain, out MenuOperation[] operations)
    {
        if (!_menus.TryGetValue(brain, out var menu)) { operations = []; return false; }
        lock (menu)
        {
            if (!menu.Ready) { operations = []; return false; }
            operations = menu.Apps.SelectMany(pair => pair.Value).ToArray();
            return true;
        }
    }

    public void Replace(string brain, IReadOnlyList<(string App, MenuOperation[] Operations)> apps)
    {
        var menu = _menus.GetOrAdd(brain, _ => new Menu());
        lock (menu)
        {
            menu.Apps.Clear();
            foreach (var (app, operations) in apps)
            {
                if (operations.Length > 0) { menu.Apps[app] = operations; }
            }
            menu.Ready = true;
        }
    }

    public void Upsert(string brain, string app, MenuOperation[] operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var menu = _menus.GetOrAdd(brain, _ => new Menu());
        lock (menu)
        {
            if (operations.Length == 0) { menu.Apps.Remove(app); }
            else { menu.Apps[app] = operations; }
        }
    }
}
