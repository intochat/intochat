using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Integrations;
using IntoChat.OS.Microsoft.Windows;
using IntoChat.OS.Flutter.Collection;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;

static class Machine
{
    public const int MaxEntries = 40;
    public const string NotConnected = "No supported storage is connected. Connect a Windows PC and allow a folder. Other connectors do not expose file browsing here.";
    public const string Storages = "storages";

    static string Scope(IntoChatOSConnection brain)
    {
        var appKey = brain.Setting("App")!;
        return brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
    }

    public static async Task<ConnectionSummary[]> Connections(IntoChatOSConnection brain) =>
        (await brain.Get<IConnectionRequests>(Scope(brain)).ListConnections())
            .Where(item => item.Kind == "windows").OrderBy(item => item.Id).ToArray();

    static async Task<string> StorageLabel(IntoChatOSConnection brain, ConnectionSummary connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Account)) { return connection.Account; }
        try
        {
            var name = (await brain.Get<IWindows>(connection.Neuron).Facts()).MachineName;
            if (!string.IsNullOrWhiteSpace(name)) { return name; }
        }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        return "Windows PC (name unavailable) - " + connection.Id;
    }

    public static string Id(string connection, string path) =>
        "location:" + Uri.EscapeDataString(connection) + "|" + Uri.EscapeDataString(path.Replace('/', '\\'));

    public static (string Connection, string Path)? Parse(string value)
    {
        if (!value.StartsWith("location:", StringComparison.Ordinal)) { return null; }
        var parts = value[9..].Split('|', 2);
        return parts.Length == 2 ? (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1])) : null;
    }

    public static async Task<(string Connection, string Path)?> Resolve(IntoChatOSConnection brain, string input)
    {
        if (input.Length == 0 || input == Storages) { return null; }
        var connections = await Connections(brain);
        var parsed = Parse(input);
        if (parsed is { } location)
        {
            if (!connections.Any(item => item.Neuron == location.Connection)) { throw new InvalidOperationException("Storage is no longer connected."); }
            return location;
        }
        var current = Parse((await brain.Get<IText>(brain.Setting("App")! + "/navigation").Read()).Markdown);
        var connection = current?.Connection ?? connections.FirstOrDefault()?.Neuron;
        return connection is null ? null : (connection, input);
    }

    public static async Task<bool> IsFolder(IntoChatOSConnection brain, string id)
    {
        var location = await Resolve(brain, id);
        if (location is null || location.Value.Path.Length == 0) { return true; }
        try { _ = await brain.Get<IFileSystem>(WindowsKeys.Files(location.Value.Connection)).List(location.Value.Path); return true; }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return false; }
    }

    public static async Task Browse(IntoChatOSConnection brain, string input, string? document = null)
    {
        var appKey = brain.Setting("App")!;
        var listing = brain.Get<ICollectionView>(appKey + "/listing");
        var connections = await Connections(brain);
        var location = await Resolve(brain, input);
        var roots = location is { } storage ? await brain.Get<IWindows>(storage.Connection).Roots() : [];
        var storageItems = new List<CollectionItem>();
        foreach (var connection in connections)
        {
            storageItems.Add(new(Id(connection.Neuron, ""), await StorageLabel(brain, connection), "folder",
                "Windows PC - " + connection.Status, TrailingText: "Browse allowed folders"));
            if (location?.Connection == connection.Neuron)
            {
                storageItems.AddRange(roots.Select(root => new CollectionItem(Id(connection.Neuron, root), Label(root), "folder", root)));
            }
        }
        var storages = brain.Get<ICollectionView>(appKey + "/storages");
        await storages.Set(new(storageItems.ToArray(),
            Selection: location is { } active ? Id(active.Connection, "") : null,
            EmptyLabel: NotConnected), (await storages.Read()).Revision);
        var navigation = brain.Get<IText>(appKey + "/navigation");
        var folder = location is { } selected ? Id(selected.Connection, selected.Path) : Storages;
        var destination = document ?? folder;
        var filterField = brain.Get<ITextField>(appKey + "/filter");
        var filterState = await filterField.Read();
        var previousLocation = (await navigation.Read()).Markdown;
        if (previousLocation != destination && previousLocation != folder && filterState.Value.Length > 0)
        {
            // Filter changes are signals too; their refresh must target the new location.
            await navigation.Set(destination);
            await filterField.TrySetValue("", filterState.Version);
            filterState = await filterField.Read();
        }
        var filter = filterState.Value;
        CollectionItem[] all;
        CollectionBreadcrumb[] breadcrumbs;
        if (location is not { } current)
        {
            all = await Task.WhenAll(connections.Select(async item => new CollectionItem(Id(item.Neuron, ""), await StorageLabel(brain, item), "folder",
                "Windows PC - " + item.Status, TrailingText: "Browse allowed folders")));
            breadcrumbs = [new(Storages, "Storages")];
        }
        else
        {
            var paths = current.Path.Length == 0 ? roots : await brain.Get<IFileSystem>(WindowsKeys.Files(current.Connection)).List(current.Path);
            all = paths.Select(path => new CollectionItem(Id(current.Connection, path), Label(path), current.Path.Length == 0 ? "folder" : "entry",
                current.Path.Length == 0 ? path : "Open folder or preview text")).ToArray();
            var connection = connections.First(item => item.Neuron == current.Connection);
            var crumbs = new List<CollectionBreadcrumb> { new(Storages, "Storages"), new(Id(current.Connection, ""), await StorageLabel(brain, connection)) };
            if (current.Path.Length > 0)
            {
                var root = roots.OrderByDescending(value => value.Length).FirstOrDefault(value =>
                    string.Equals(value.TrimEnd('\\', '/'), current.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                    current.Path.StartsWith(value.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase) ||
                    current.Path.StartsWith(value.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase));
                if (root is not null)
                {
                    var path = root.TrimEnd('\\', '/');
                    crumbs.Add(new(Id(current.Connection, root), Label(root)));
                    foreach (var part in current.Path[Math.Min(root.TrimEnd('\\', '/').Length, current.Path.Length)..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        path += "\\" + part;
                        crumbs.Add(new(Id(current.Connection, path), part));
                    }
                }
                else { crumbs.Add(new(Id(current.Connection, current.Path), Label(current.Path))); }
            }
            breadcrumbs = crumbs.ToArray();
        }
        var matching = all.Where(item => item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        var shown = matching.Take(MaxEntries).ToArray();
        var content = matching.Length > shown.Length ? "Showing " + shown.Length + " of " + matching.Length + ". Narrow the name filter to find other entries." : "";
        if (document is not null && Parse(document) is { } file)
        {
            breadcrumbs = [.. breadcrumbs, new(document, Label(file.Path))];
            var heading = "## " + Label(file.Path).Replace("\r", "").Replace("\n", " ") + "\n\n";
            try { content = heading + await Text(brain, document); }
            catch (Exception) when (!brain.Stopping.IsCancellationRequested)
            { content = heading + "This file could not be read as text. Choose a folder breadcrumb to continue browsing."; }
        }
        var previous = await listing.Read();
        var selection = document ?? previous.Definition.Selection;
        await listing.Set(new(shown, Selection: shown.Any(item => item.Id == selection) ? selection : null,
            EmptyLabel: connections.Length == 0 ? NotConnected : filter.Length > 0 ? "No names match this filter." : "No files or allowed folders here.",
            Breadcrumbs: breadcrumbs), previous.Revision);
        await navigation.Set(destination);
        await brain.Get<IText>(appKey + "/content").Set(content);
    }

    public static async Task Preview(IntoChatOSConnection brain, string input)
    {
        if (await Resolve(brain, input) is not { } file) { await Browse(brain, Storages); return; }
        var separator = file.Path.LastIndexOfAny(['\\', '/']);
        var parent = separator < 0 ? "" : file.Path[..(separator == 2 && file.Path[1] == ':' ? separator + 1 : separator)];
        await Browse(brain, Id(file.Connection, parent), Id(file.Connection, file.Path));
    }

    static string Label(string path) => path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() is { Length: > 0 } label ? label : path;

    public static async Task<string> Text(IntoChatOSConnection brain, string input)
    {
        if (await Resolve(brain, input) is not { } location) { return NotConnected; }
        var text = await brain.Get<IFileSystem>(WindowsKeys.Files(location.Connection)).Read(location.Path);
        if (!location.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { return text; }
        return await OwnerState(brain, location.Path) + "```csharp\n" + text + "\n```";
    }

    // A .cs file has an owning app only when its folder is named like an installed app of this brain.
    static async Task<string> OwnerState(IntoChatOSConnection brain, string path)
    {
        var parts = path.Split('\\', '/');
        if (parts.Length < 2) { return ""; }
        var folder = parts[^2];
        var scope = Scope(brain);
        await foreach (var instance in brain.Kernel.Instances<IApp>(brain.Stopping))
        {
            if (IApp.NameOf(scope, instance.Key) is not { } name || !string.Equals(name, folder, StringComparison.OrdinalIgnoreCase)) { continue; }
            var app = await brain.Get<IApp>(instance.Key).Read();
            var recent = app.Activity.TakeLast(3).Select(line => "- " + line);
            return "**" + app.Manifest.Title + "** (" + name + ") v" + app.Version + " - " + app.Status + "\n" +
                string.Join("\n", recent) + "\n\n";
        }
        return "";
    }
}
