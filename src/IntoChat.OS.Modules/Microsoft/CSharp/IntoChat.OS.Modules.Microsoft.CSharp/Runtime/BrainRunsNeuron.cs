using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.CSharp.Runtime.Signals;
using Orleans.Metadata;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// How the brain runtime hands a turn's run facts to its brain's runs neuron: one call per turn. Not a neuron
// contract, so no edge, script or MCP caller can resolve it; BrainRunsGuard admits only that brain's runtime.
[Alias("microsoft.csharp.runs-recorder"), DefaultGrainType(BrainRunsNeuron.GrainTypeName)]
internal interface IBrainRunsRecorder : IGrainWithStringKey
{
    // announce is false while another behavior is still running. The facts are kept either way;
    // the changed signal waits until a record that finishes the last in-flight behavior.
    Task Record(RunFact[] facts, bool announce);

    // Publishes the changed signal for facts already recorded while later behavior was still starting.
    Task Announce();
}

// A run fact and the trace it is published under: its batch's, or the one the runtime's turn ran in.
[GenerateSerializer, Alias("microsoft.csharp.run-fact")]
internal sealed record RunFact([property: Id(0)] Signal Fact, [property: Id(1)] Trace? Trace);

// Facts and their read model share the storage write. Apps read snapshots instead of copying every run.
[GrainType(GrainTypeName)]
internal sealed class BrainRunsNeuron(
    [PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<BrainRunsState> store)
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

    public Task Record(RunFact[] facts, bool announce = true)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var next = Snapshot.Apply(facts);
        (Signal Signal, Trace? Trace)[] signals = [.. facts.Select(fact => (fact.Fact, fact.Trace))];
        var behaviors = Behaviors(facts);
        var announced = Snapshot.Unannounced.Concat(behaviors).Distinct(StringComparer.Ordinal).ToArray();
        if (!announce)
        {
            var held = Same(announced, Snapshot.Unannounced) ? next : next with { Unannounced = announced };
            return ReferenceEquals(held, Snapshot) ? PublishAsync(signals) : Save(held, signals);
        }
        var cleared = Snapshot.Unannounced.Length == 0 ? next : next with { Unannounced = [] };
        if (announced.Length == 0) { return ReferenceEquals(cleared, Snapshot) ? PublishAsync(signals) : Save(cleared, signals); }
        Trace? trace = null;
        for (var index = facts.Length - 1; index >= 0 && trace is null; index--)
        {
            if (facts[index].Fact is BehaviorRan { Outcome: not RunOutcome.Skipped }) { trace = facts[index].Trace; }
        }
        var revision = ReferenceEquals(next, Snapshot) ? Snapshot.Revision : next.Revision;
        (Signal Signal, Trace? Trace)[] published = [.. signals, (new BrainRunsChanged(revision, announced), trace)];
        return ReferenceEquals(cleared, Snapshot) ? PublishAsync(published) : Save(cleared, published);
    }

    public Task Announce()
    {
        if (Snapshot.Unannounced.Length == 0) { return Task.CompletedTask; }
        var behaviors = Snapshot.Unannounced;
        return Save(Snapshot with { Unannounced = [] }, [(new BrainRunsChanged(Snapshot.Revision, behaviors), null)]);
    }

    private static bool Same(string[] left, string[] right)
        => left.Length == right.Length && left.AsSpan().SequenceEqual(right);

    private static string[] Behaviors(RunFact[] facts)
        => [.. facts.Select(fact => fact.Fact).OfType<BehaviorRan>().Where(ran => ran.Outcome != RunOutcome.Skipped)
            .Select(ran => ran.Behavior).Distinct(StringComparer.Ordinal)];
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
