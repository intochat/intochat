using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Tools;

// A brain's tools as capabilities: every existing instance of a kernel neuron type that is a tool
// source, asked for its live tools, each one bound to its source's key and called as Run(tool, arguments).
// Lists are clipped so one misbehaving source cannot flood search.
public sealed class ToolCapabilitySource(IGrainFactory grains, IKernelView kernel) : IRuntimeCapabilitySource
{
    internal const int MaxTools = 200;
    internal const int MaxDescription = 1000;
    private const int MaxSchema = 600;
    private static readonly string ToolSource = typeof(IToolSource).FullName!;

    public async Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        if (BrainScope.CurrentId() != brainId) { throw new UnauthorizedAccessException($"Tools are read for the caller's own brain, not '{brainId}'."); }
        List<NeuronType> capabilities = [];
        foreach (var source in (await kernel.Types(ct)).Where(type => type.Kind == TypeKind.Neuron && type.Implements.Contains(ToolSource, StringComparer.Ordinal)))
        {
            await foreach (var instance in kernel.Instances(source.Alias, ct))
            {
                ToolSet set;
                // One source that cannot answer drops out of search; the others stay.
                try { set = await grains.GetGrain<IToolSource>(GrainId.Create(instance.Type, instance.Key)).Tools(ct).WaitAsync(ct); }
                catch (Exception error) when (error is not OperationCanceledException) { continue; }
                capabilities.AddRange(set.Tools.Take(MaxTools).Select(tool => Capability(instance.Key, source.Name, set.Title, tool)));
            }
        }
        return capabilities;
    }

    internal static NeuronType Capability(string key, string contract, string title, ToolDescriptor tool)
    {
        var description = tool.Description.Length <= MaxDescription ? tool.Description : tool.Description[..MaxDescription];
        var schema = tool.InputSchema.Length <= MaxSchema ? tool.InputSchema : tool.InputSchema[..MaxSchema];
        return new NeuronType(
            $"tool:{key}/{tool.Name}", "tools", $"{title}: {tool.Title}", description,
            [$"Task<ToolResult> Run(\"{tool.Name}\", arguments) — {schema}"],
            contract, key, tool.Name);
    }
}
