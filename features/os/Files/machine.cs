using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Integrations;
using DigitalBrain.Microsoft.Windows;

// Shared: the brain's Windows PC and what the surface shows about it.
static class Machine
{
    public const int MaxEntries = 40;

    public const string NotConnected = "No Windows PC is connected. Connect one and allow a folder.";

    static string Scope(DigitalBrainConnection brain)
    {
        var appKey = brain.Setting("App")!;
        return brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
    }

    public static async Task<string?> Connection(DigitalBrainConnection brain)
    {
        var all = await brain.Get<IConnectionRequests>(Scope(brain)).ListConnections();
        return all.Where(item => item.Kind == "windows").OrderBy(item => item.Id).Select(item => item.Neuron).FirstOrDefault();
    }

    public static async Task<string[]> Entries(DigitalBrainConnection brain, string path)
    {
        if (await Connection(brain) is not { } connection) { return []; }
        return path.Length == 0
            ? await brain.Get<IWindows>(connection).Roots()
            : await brain.Get<IFileSystem>(WindowsKeys.Files(connection)).List(path);
    }

    // The files neuron has no type information: a path is a folder when it lists.
    public static async Task<bool> IsFolder(DigitalBrainConnection brain, string path)
    {
        try { _ = await Entries(brain, path); return true; }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { return false; }
    }

    // The folder above a path; from an allowed root it is the roots list (empty), since the parent is outside them.
    public static async Task<string> Up(DigitalBrainConnection brain, string path)
    {
        var roots = await Entries(brain, "");
        var here = path.TrimEnd('\\', '/');
        return roots.Any(root => string.Equals(root.TrimEnd('\\', '/'), here, StringComparison.OrdinalIgnoreCase)) ? "" : Parent(path);
    }

    static string Parent(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        if (cut < 0) { return ""; }
        var parent = trimmed[..cut];
        return parent.EndsWith(':') ? parent + trimmed[cut] : parent;
    }

    public static async Task<string> Text(DigitalBrainConnection brain, string path)
    {
        if (await Connection(brain) is not { } connection) { return NotConnected; }
        var text = await brain.Get<IFileSystem>(WindowsKeys.Files(connection)).Read(path);
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { return text; }
        return await OwnerState(brain, path) + "```csharp\n" + text + "\n```";
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
