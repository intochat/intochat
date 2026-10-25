using Orleans.Placement;
using Orleans.Runtime.Placement;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// Places every grain of a brain on one silo: the silo that owns the brain's writer lock and serves its journal.
[Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking, Alias("brain.affine-placement")]
public sealed class BrainAffinePlacement : PlacementStrategy;

// Inherited, so it covers every grain class deriving from a marked base (Neuron).
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class BrainAffinePlacementAttribute() : PlacementAttribute(new BrainAffinePlacement());

// Every silo computes the same answer: a stable hash of the brain id over the sorted compatible silos.
internal sealed class BrainAffinePlacementDirector : IPlacementDirector
{
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var silos = context.GetCompatibleSilos(target);
        if (silos.Length == 0) { throw new OrleansException($"No silo can host {target.GrainIdentity}."); }
        var sorted = silos.Order().ToArray();
        var brain = BrainKey.BrainOf(target.GrainIdentity.Key.ToString()!);
        return Task.FromResult(sorted[(int)(Fnv1a(brain) % (uint)sorted.Length)]);
    }

    // FNV-1a over UTF-16 code units: process-independent, unlike string.GetHashCode.
    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value) { hash = (hash ^ c) * 16777619u; }
        return hash;
    }
}
