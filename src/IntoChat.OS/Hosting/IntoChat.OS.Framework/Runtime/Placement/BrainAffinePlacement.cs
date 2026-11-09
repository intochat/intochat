using DigitalBrain.Brains;
using IntoChat.OS.Framework.Modules;
using Orleans.Placement;
using Orleans.Runtime.Placement;

namespace IntoChat.OS.Framework;

// Places every grain of a brain on one silo: the silo that owns the brain's writer lock and serves its journal.
// DigitalBrain.Runtime.Orleans owns "brain.affine-placement" for its host grains; this transitional os strategy keeps a
// distinct wire alias until the Framework retires (an alias is wire-only, never persisted).
[Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking, Alias("os.brain-affine-placement")]
public sealed class BrainAffinePlacement : PlacementStrategy;

// Inherited, so it covers every grain class deriving from a marked base (Neuron).
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class BrainAffinePlacementAttribute() : PlacementAttribute(new BrainAffinePlacement());

// Every silo computes the same answer: 64-bit FNV-1a Highest Random Weight (HRW) Rendezvous Hashing of the brain id and silo address.
internal sealed class BrainAffinePlacementDirector : IPlacementDirector
{
    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var silos = context.GetCompatibleSilos(target);
        if (silos.Length == 0) { throw new OrleansException($"No silo can host {target.GrainIdentity}."); }
        if (silos.Length == 1) { return Task.FromResult(silos[0]); }

        var brain = BrainKey.BrainOf(target.GrainIdentity.Key.ToString()!);
        SiloAddress bestSilo = silos[0];
        ulong highestWeight = 0;

        foreach (var silo in silos)
        {
            var weight = ComputeWeight(brain, silo.ToParsableString());
            if (weight > highestWeight)
            {
                highestWeight = weight;
                bestSilo = silo;
            }
        }

        return Task.FromResult(bestSilo);
    }

    // 64-bit FNV-1a with SplitMix64 avalanche mixer for Highest Random Weight (HRW) Rendezvous Hashing
    internal static ulong ComputeWeight(string brainId, string siloAddress)
    {
        ulong hash = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        foreach (var c in brainId) { hash = (hash ^ c) * prime; }
        hash = (hash ^ ':') * prime;
        foreach (var c in siloAddress) { hash = (hash ^ c) * prime; }

        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        hash ^= hash >> 33;

        return hash;
    }
}
