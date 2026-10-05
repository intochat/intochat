namespace DigitalBrain.Framework;

// Platform-facing runtime view. Providers intersect these identities with their own membership.
public interface IActivationCensus
{
    Task<ActivationRecord[]> Activations(string grainTypeId, CancellationToken ct);
}

public sealed record ActivationRecord(string TypeId, string Key);
