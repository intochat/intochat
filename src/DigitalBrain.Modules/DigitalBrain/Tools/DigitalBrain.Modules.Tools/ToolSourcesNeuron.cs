using DigitalBrain.Framework;

namespace DigitalBrain.Tools;

[GenerateSerializer, Alias("tools.sources-state")]
public sealed class ToolSourcesState
{
    [Id(0)] public Dictionary<string, ToolSourceEntry> Sources { get; set; } = new(StringComparer.Ordinal);
}

// Keyed {brainId}/tools. Only sources keyed inside the same brain register; the lists are clipped so
// one misbehaving server cannot flood search.
[GrainType("tools.sources")]
internal sealed class ToolSourcesNeuron(
    [PersistentState("tools.sources", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ToolSourcesState> store)
    : Neuron<ToolSourcesState>(store), IToolSources
{
    internal const int MaxTools = 200;
    internal const int MaxDescription = 1000;

    public static string KeyFor(string brainId) => brainId + "/tools";

    private string Brain
    {
        get
        {
            var key = this.GetPrimaryKeyString();
            return key.EndsWith("/tools", StringComparison.Ordinal) && key.IndexOf('/') == key.Length - "/tools".Length
                ? key[..^"/tools".Length]
                : throw new ArgumentException($"A tool source registry is keyed {{brainId}}/tools, not '{key}'.");
        }
    }

    public async Task Register(ToolSourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);
        if (!entry.Key.StartsWith(Brain + "/", StringComparison.Ordinal))
        { throw new UnauthorizedAccessException($"Tool source '{entry.Key}' is not in brain '{Brain}'."); }
        var tools = entry.Tools.Take(MaxTools)
            .Select(tool => tool.Description.Length <= MaxDescription ? tool : tool with { Description = tool.Description[..MaxDescription] })
            .ToArray();
        var sources = new Dictionary<string, ToolSourceEntry>(Snapshot.Sources, StringComparer.Ordinal) { [entry.Key] = entry with { Tools = tools } };
        await Save(new ToolSourcesState { Sources = sources }, new ToolsChanged(entry.Key, tools.Length));
    }

    public async Task Unregister(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!Snapshot.Sources.ContainsKey(key)) { return; }
        var sources = new Dictionary<string, ToolSourceEntry>(Snapshot.Sources, StringComparer.Ordinal);
        sources.Remove(key);
        await Save(new ToolSourcesState { Sources = sources }, new ToolsChanged(key, 0));
    }

    public Task<ToolSourceEntry[]> Read()
        => Task.FromResult<ToolSourceEntry[]>([.. Snapshot.Sources.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal)]);
}
