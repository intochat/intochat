using DigitalBrain.Client;
using DigitalBrain.Integrations;
using DigitalBrain.Microsoft.Windows;

// Shared: the brain's Windows PC and what the surface shows about it.
static class Machine
{
    public const int MaxEntries = 40;

    public static async Task<string?> Connection(DigitalBrainConnection brain)
    {
        var all = await brain.Get<IConnectionRequests>(brain.Setting("Brain")!).ListConnections();
        return all.Where(item => item.Kind == "windows").OrderBy(item => item.Id).Select(item => item.Neuron).FirstOrDefault();
    }

    public static async Task<string[]> Entries(DigitalBrainConnection brain, string path)
    {
        if (await Connection(brain) is not { } connection) { return []; }
        return path.Length == 0
            ? await brain.Get<IWindows>(connection).Roots()
            : await brain.Get<IFileSystem>(WindowsKeys.Files(connection)).List(path);
    }

    public static async Task<string> Text(DigitalBrainConnection brain, string path)
    {
        if (await Connection(brain) is not { } connection) { return ""; }
        var text = await brain.Get<IFileSystem>(WindowsKeys.Files(connection)).Read(path);
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "```csharp\n" + text + "\n```" : text;
    }
}
