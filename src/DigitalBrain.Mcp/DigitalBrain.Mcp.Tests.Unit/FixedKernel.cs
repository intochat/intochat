using DigitalBrain.Framework;

namespace DigitalBrain.Mcp.Tests.Unit;

// A kernel with no silo behind it: its types are the compiled layer of the given contract assemblies
// and it has no instances. For facts about a surface that reads types before it reaches a neuron.
public sealed class FixedKernel(params System.Reflection.Assembly[] assemblies) : IKernelView
{
    public TypeEntry[] Entries { get; } = new CompiledTypes(assemblies).Entries;

    public Task<TypeEntry[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(Entries);

    public IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : INeuron => AsyncEnumerable.Empty<Instance>();

    public IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<Instance>();

    public Task<string[]> Brains(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);

    public Task<Instance[]> Running(CancellationToken cancellationToken = default) => Task.FromResult<Instance[]>([]);
}
