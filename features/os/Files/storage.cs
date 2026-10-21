using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Integrations;
using IntoChat.OS.Microsoft.Windows;
using IntoChat.OS.Flutter.Collection;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;

// Every connection whose connector declares the storage capability, browsed through IStorage on the
// connection key. Locations are opaque per storage; breadcrumbs come from the storage's own Ancestors.
static class Store
{
    public const int MaxEntries = 40;
    public const string NotConnected = "No storage is connected. Connect a Windows PC and allow a folder, or connect Google Drive.";
    public const string Storages = "storages";

    static string Scope(IntoChatOSConnection brain)
    {
        var appKey = brain.Setting("App")!;
        return brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
    }

    public static async Task<ConnectionSummary[]> Connections(IntoChatOSConnection brain) =>
        (await brain.Get<IConnectionRequests>(Scope(brain)).ListConnections())
            .Where(item => item.Capabilities.Contains(IStorage.Capability)).OrderBy(item => item.Kind).ThenBy(item => item.Id).ToArray();

    static IStorage Of(IntoChatOSConnection brain, string connection) => brain.Get<IStorage>(connection);

    static async Task<string> KindLabel(IntoChatOSConnection brain, ConnectionSummary connection)
    {
        try
        {
            var kinds = await brain.Get<IConnectionRequests>(Scope(brain)).ListKinds();
            if (kinds.FirstOrDefault(kind => kind.Id == connection.Kind) is { } kind) { return kind.DisplayName; }
        }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        return connection.Kind;
    }

    static async Task<string> StorageLabel(IntoChatOSConnection brain, ConnectionSummary connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Account)) { return connection.Account; }
        // A Windows PC has no account; its machine name is the label people recognise.
        if (connection.Kind == "windows")
        {
            try
            {
                var name = (await brain.Get<IWindows>(connection.Neuron).Facts()).MachineName;
                if (!string.IsNullOrWhiteSpace(name)) { return name; }
            }
            catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        }
        return await KindLabel(brain, connection) + " (name unavailable) - " + connection.Id;
    }

    static async Task<CollectionItem> StorageItem(IntoChatOSConnection brain, ConnectionSummary connection) =>
        new(Id(connection.Neuron, ""), await StorageLabel(brain, connection), "folder",
            await KindLabel(brain, connection) + " - " + connection.Status, TrailingText: "Browse folders");

    public static string Id(string connection, string location) =>
        "location:" + Uri.EscapeDataString(connection) + "|" + Uri.EscapeDataString(location);

    public static (string Connection, string Location)? Parse(string value)
    {
        if (!value.StartsWith("location:", StringComparison.Ordinal)) { return null; }
        var parts = value[9..].Split('|', 2);
        return parts.Length == 2 ? (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1])) : null;
    }

    public static async Task<(string Connection, string Location)?> Resolve(IntoChatOSConnection brain, string input)
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
        if (location is null || location.Value.Location.Length == 0) { return true; }
        try { return (await Of(brain, location.Value.Connection).Ancestors(location.Value.Location))[^1].IsFolder; }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return false; }
    }

    public static async Task Browse(IntoChatOSConnection brain, string input, string? document = null)
    {
        var appKey = brain.Setting("App")!;
        var listing = brain.Get<ICollectionView>(appKey + "/listing");
        var connections = await Connections(brain);
        var location = await Resolve(brain, input);
        var roots = location is { } storage ? await Of(brain, storage.Connection).Roots() : [];
        var storageItems = new List<CollectionItem>();
        foreach (var connection in connections)
        {
            storageItems.Add(await StorageItem(brain, connection));
            if (location?.Connection == connection.Neuron)
            {
                storageItems.AddRange(roots.Select(root => new CollectionItem(Id(connection.Neuron, root.Location), root.Name, "folder", root.Location)));
            }
        }
        var storages = brain.Get<ICollectionView>(appKey + "/storages");
        await storages.Set(new(storageItems.ToArray(),
            Selection: location is { } active ? Id(active.Connection, "") : null,
            EmptyLabel: NotConnected), (await storages.Read()).Revision);
        var navigation = brain.Get<IText>(appKey + "/navigation");
        var folder = location is { } selected ? Id(selected.Connection, selected.Location) : Storages;
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
            all = await Task.WhenAll(connections.Select(item => StorageItem(brain, item)));
            breadcrumbs = [new(Storages, "Storages")];
        }
        else
        {
            var store = Of(brain, current.Connection);
            var entries = current.Location.Length == 0 ? roots : await store.List(current.Location);
            all = entries.Select(entry => new CollectionItem(Id(current.Connection, entry.Location), entry.Name, entry.IsFolder ? "folder" : "entry",
                current.Location.Length == 0 ? entry.Location : entry.IsFolder ? "Open folder" : "Preview text")).ToArray();
            var connection = connections.First(item => item.Neuron == current.Connection);
            var crumbs = new List<CollectionBreadcrumb> { new(Storages, "Storages"), new(Id(current.Connection, ""), await StorageLabel(brain, connection)) };
            if (current.Location.Length > 0)
            { crumbs.AddRange((await store.Ancestors(current.Location)).Select(entry => new CollectionBreadcrumb(Id(current.Connection, entry.Location), entry.Name))); }
            breadcrumbs = crumbs.ToArray();
        }
        var matching = all.Where(item => item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        var shown = matching.Take(MaxEntries).ToArray();
        var content = matching.Length > shown.Length ? "Showing " + shown.Length + " of " + matching.Length + ". Narrow the name filter to find other entries." : "";
        if (document is not null && Parse(document) is { } file)
        {
            var chain = await Of(brain, file.Connection).Ancestors(file.Location);
            var name = chain.Length > 0 ? chain[^1].Name : file.Location;
            breadcrumbs = [.. breadcrumbs, new(document, name)];
            var heading = "## " + name.Replace("\r", "").Replace("\n", " ") + "\n\n";
            try { content = heading + await Text(brain, document); }
            catch (Exception) when (!brain.Stopping.IsCancellationRequested)
            { content = heading + "This file could not be read as text. Choose a folder breadcrumb to continue browsing."; }
        }
        var previous = await listing.Read();
        var selection = document ?? previous.Definition.Selection;
        await listing.Set(new(shown, Selection: shown.Any(item => item.Id == selection) ? selection : null,
            EmptyLabel: connections.Length == 0 ? NotConnected : filter.Length > 0 ? "No names match this filter." : "No files or folders here.",
            Breadcrumbs: breadcrumbs), previous.Revision);
        await navigation.Set(destination);
        await brain.Get<IText>(appKey + "/content").Set(content);
    }

    // A file previews inside its parent folder's listing; the storage names the parent.
    public static async Task Preview(IntoChatOSConnection brain, string input)
    {
        if (await Resolve(brain, input) is not { } file) { await Browse(brain, Storages); return; }
        var chain = await Of(brain, file.Connection).Ancestors(file.Location);
        var parent = chain.Length > 1 ? chain[^2].Location : "";
        await Browse(brain, Id(file.Connection, parent), Id(file.Connection, file.Location));
    }

    public static async Task<string> Text(IntoChatOSConnection brain, string input)
    {
        if (await Resolve(brain, input) is not { } location) { return NotConnected; }
        var store = Of(brain, location.Connection);
        var text = await store.Read(location.Location);
        var chain = await store.Ancestors(location.Location);
        var name = chain.Length > 0 ? chain[^1].Name : location.Location;
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { return text; }
        return (chain.Length > 1 ? await OwnerState(brain, chain[^2].Name) : "") + "```csharp\n" + text + "\n```";
    }

    // A .cs file has an owning app only when its folder is named like an installed app of this brain.
    static async Task<string> OwnerState(IntoChatOSConnection brain, string folder)
    {
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
