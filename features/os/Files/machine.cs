using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Integrations;
using DigitalBrain.Microsoft.Windows;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

static class Machine
{
    public const int MaxEntries = 40;
    public const string NotConnected = "No supported storage is connected. Connect a Windows PC and allow a folder. Other connectors do not expose file browsing here.";
    public const string Storages = "storages";

    static string Scope(DigitalBrainConnection brain)
    {
        var appKey = brain.Setting("App")!;
        return brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
    }

    public static async Task<ConnectionSummary[]> Connections(DigitalBrainConnection brain) =>
        (await brain.Get<IConnectionRequests>(Scope(brain)).ListConnections())
            .Where(item => item.Kind == "windows").OrderBy(item => item.Id).ToArray();

    static async Task<string> StorageLabel(DigitalBrainConnection brain, ConnectionSummary connection)
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
        "location:" + Uri.EscapeDataString(connection) + "|" + Uri.EscapeDataString(path);

    public static (string Connection, string Path)? Parse(string value)
    {
        if (!value.StartsWith("location:", StringComparison.Ordinal)) { return null; }
        var parts = value[9..].Split('|', 2);
        return parts.Length == 2 ? (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1])) : null;
    }

    public static async Task<(string Connection, string Path)?> Resolve(DigitalBrainConnection brain, string input)
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

    public static async Task<bool> IsFolder(DigitalBrainConnection brain, string id)
    {
        var location = await Resolve(brain, id);
        if (location is null || location.Value.Path.Length == 0) { return true; }
        try { _ = await brain.Get<IFileSystem>(WindowsKeys.Files(location.Value.Connection)).List(location.Value.Path); return true; }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return false; }
    }

    public static async Task Browse(DigitalBrainConnection brain, string input)
    {
        var appKey = brain.Setting("App")!;
        var listing = brain.Get<ICollectionView>(appKey + "/listing");
        var connections = await Connections(brain);
        var location = await Resolve(brain, input);
        var filter = (await brain.Get<ITextField>(appKey + "/filter").Read()).Value;
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
            var roots = await brain.Get<IWindows>(current.Connection).Roots();
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
        var matching = all.Where(item => item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var shown = matching.Take(MaxEntries).ToArray();
        var previous = await listing.Read();
        await listing.Set(new(shown, Selection: shown.Any(item => item.Id == previous.Definition.Selection) ? previous.Definition.Selection : null,
            EmptyLabel: connections.Length == 0 ? NotConnected : filter.Length > 0 ? "No names match this filter." : "No files or allowed folders here.",
            Breadcrumbs: breadcrumbs), previous.Revision);
        await brain.Get<IText>(appKey + "/navigation").Set(location is { } selected ? Id(selected.Connection, selected.Path) : Storages);
        await brain.Get<IText>(appKey + "/content").Set(matching.Length > shown.Length ? "Showing " + shown.Length + " of " + matching.Length + ". Narrow the name filter to find other entries." : "");
    }

    static string Label(string path) => path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault() is { Length: > 0 } label ? label : path;

    public static async Task<string> Text(DigitalBrainConnection brain, string input)
    {
        if (await Resolve(brain, input) is not { } location) { return NotConnected; }
        var text = await brain.Get<IFileSystem>(WindowsKeys.Files(location.Connection)).Read(location.Path);
        if (!location.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { return text; }
        return await OwnerState(brain, location.Path) + "```csharp\n" + text + "\n```";
    }

    // A .cs file has an owning app only when its folder is named like an installed app of this brain.
    static async Task<string> OwnerState(DigitalBrainConnection brain, string path)
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
