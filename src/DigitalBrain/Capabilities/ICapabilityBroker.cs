namespace DigitalBrain.Capabilities;

/// <summary>Resolves the implementation of a capability for a brain.</summary>
public interface ICapabilityBroker
{
    /// <summary>The implementation of <typeparamref name="TCapability"/> granted to the brain.</summary>
    Task<TCapability> Resolve<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class;
}
