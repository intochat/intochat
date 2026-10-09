using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Tools;

// A brain's registered tools as capabilities: each one bound to its source's key, called as Run(tool, arguments).
internal sealed class ToolCapabilitySource(IGrainFactory grains) : IRuntimeCapabilitySource
{
    private const int MaxSchema = 600;

    public async Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var entries = await grains.GetGrain<IToolSources>(ToolSourcesNeuron.KeyFor(brainId)).Read().WaitAsync(ct);
        return [.. entries.SelectMany(entry => entry.Tools.Select(tool => Capability(entry, tool)))];
    }

    internal static NeuronType Capability(ToolSourceEntry entry, ToolDescriptor tool)
    {
        var schema = tool.InputSchema.Length <= MaxSchema ? tool.InputSchema : tool.InputSchema[..MaxSchema];
        return new NeuronType(
            $"tool:{entry.Key}/{tool.Name}", "tools", $"{entry.Title}: {tool.Title}", tool.Description,
            [$"Task<ToolResult> Run(\"{tool.Name}\", arguments) — {schema}"],
            entry.Contract, entry.Key, tool.Name);
    }
}
