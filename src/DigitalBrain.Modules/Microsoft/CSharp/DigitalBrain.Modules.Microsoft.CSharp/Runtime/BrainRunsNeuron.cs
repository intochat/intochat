using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Runtime;

// How the brain runtime hands a turn's run facts to its brain's runs neuron: one call per turn. Not a neuron
// contract, so no edge, script or MCP caller can resolve it; BrainRunsGuard admits only that brain's runtime.
[Alias("microsoft.csharp.runs-recorder"), DefaultGrainType(BrainRunsNeuron.GrainTypeName)]
internal interface IBrainRunsRecorder : IGrainWithStringKey
{
    [OneWay] Task Record(RunFact[] facts);
}

// A run fact and the trace it is published under: its batch's, or the one the runtime's turn ran in.
[GenerateSerializer, Alias("microsoft.csharp.run-fact")]
internal sealed record RunFact([property: Id(0)] Signal Fact, [property: Id(1)] Trace? Trace);

// Facts and their read model share the storage write. Apps read snapshots instead of copying every run.
[GrainType(GrainTypeName)]
internal sealed class BrainRunsNeuron(
    [PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<BrainRunsState> store)
    : Neuron<BrainRunsState>(store), IBrainRuns, IBrainRunsRecorder
{
    internal const string GrainTypeName = "microsoft.csharp.runs";

    public Task<BrainRunsSnapshot> Read(string? excludedOwner = null)
    {
        var prefix = BrainKey.BrainOf(this.GetPrimaryKeyString()) + "/apps/";
        if (excludedOwner is not null && (!excludedOwner.StartsWith(prefix, StringComparison.Ordinal)
            || excludedOwner.Length == prefix.Length || excludedOwner[prefix.Length..].Contains('/')))
        { throw new ArgumentException("Exclude an app owner in this brain.", nameof(excludedOwner)); }
        return Task.FromResult(new BrainRunsSnapshot(Snapshot.Revision,
            [.. Snapshot.Recent.Where(item => item.Owner != excludedOwner).TakeLast(200)], Snapshot.Statistics));
    }

    public Task Record(RunFact[] facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var next = Snapshot.Apply(facts);
        (Signal Signal, Trace? Trace)[] signals = [.. facts.Select(fact => (fact.Fact, fact.Trace))];
        return ReferenceEquals(next, Snapshot) ? PublishAsync(signals) : Save(next, signals);
    }
}

// Nobody publishes runs as someone else: the grain the kernel runs a Record for must be the runtime of the
// runs neuron's own brain.
internal sealed class BrainRunsGuard : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod.DeclaringType == typeof(IBrainRunsRecorder) && !Admits(context.SourceId, context.TargetId))
        { throw new UnauthorizedAccessException("Only a brain's own runtime records its synapse runs."); }
        await context.Invoke();
    }

    internal static bool Admits(GrainId? source, GrainId target)
        => source is { } caller && caller.Type.ToString() == BrainRuntimeNeuron.GrainTypeName
            && BrainKey.BrainOf(caller.Key.ToString()!) == BrainKey.BrainOf(target.Key.ToString()!);
}
