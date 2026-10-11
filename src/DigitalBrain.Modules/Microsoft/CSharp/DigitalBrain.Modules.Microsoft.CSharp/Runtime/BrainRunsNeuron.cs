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

// Holds no state: what it publishes is journaled under it, and a subscription catches up from that journal.
[GrainType(GrainTypeName)]
internal sealed class BrainRunsNeuron : Neuron, IBrainRuns, IBrainRunsRecorder
{
    internal const string GrainTypeName = "microsoft.csharp.runs";

    public Task Record(RunFact[] facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return PublishAsync([.. facts.Select(fact => (fact.Fact, fact.Trace))]);
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
