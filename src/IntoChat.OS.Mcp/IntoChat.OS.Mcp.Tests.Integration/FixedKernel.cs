using DigitalBrain;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Mcp.Tests.Integration;

// A kernel with no silo behind it: its types are the compiled layer of the given contract assemblies
// and it has no instances. For facts about a surface that reads types before it reaches a neuron.
public sealed class FixedKernel(params System.Reflection.Assembly[] assemblies) : IBrainObserver
{
    public BrainType[] Entries { get; } = new CompiledTypes(assemblies).Entries;

    public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(Entries);

    public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken = default) where T : class => AsyncEnumerable.Empty<BrainInstance>();

    public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<BrainInstance>();

    public Task<string[]> Brains(CancellationToken cancellationToken = default) => Task.FromResult<string[]>([]);
}
