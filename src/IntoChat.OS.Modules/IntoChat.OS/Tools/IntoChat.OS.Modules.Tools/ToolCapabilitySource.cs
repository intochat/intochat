using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Contracts;

namespace IntoChat.OS.Modules.Tools;

// A brain's tools as capabilities: every existing instance of a kernel neuron type that is a tool
// source, asked for its live tools, each one bound to its source's key and called as Run(tool, arguments).
// Lists are clipped so one misbehaving source cannot flood search.
public sealed class ToolCapabilitySource(IGrainFactory grains, IKernelView kernel) : IRuntimeCapabilitySource
{
    internal const int MaxTools = 200;
    internal const int MaxDescription = 1000;
    // A still-legacy source is a neuron contract deriving ILegacyToolSource (R4-21(6)) and is reached as that grain; plain
    // sources (IToolSource on a plain contract) join with plain-aware Instances (06-06d).
    private static readonly string ToolSource = typeof(ILegacyToolSource).FullName!;

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
                try { set = await grains.GetGrain<ILegacyToolSource>(GrainId.Create(instance.Type, instance.Key)).Tools(ct).WaitAsync(ct); }
                catch (Exception error) when (error is not OperationCanceledException) { continue; }
                capabilities.AddRange(set.Tools.Take(MaxTools).Select(tool => Capability(instance.Key, source.Name, set.Title, tool)));
            }
        }
        return capabilities;
    }

    internal static NeuronType Capability(string key, string contract, string title, ToolDescriptor tool)
    {
        var description = tool.Description.Length <= MaxDescription ? tool.Description : tool.Description[..MaxDescription];
        return new NeuronType(
            $"tool:{key}/{tool.Name}", "tools", $"{title}: {tool.Title}", description,
            [$"Task<ToolResult> Run(\"{tool.Name}\", arguments) — call this source's Tools() for the full input schema before supplying arguments."],
            contract, key, tool.Name);
    }
}
