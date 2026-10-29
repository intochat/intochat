namespace IntoChat.OS;

/// <summary>
/// The brain's first neuron. It holds the brain's own state, publishes the brain's lifecycle
/// signals, and is reached by <see cref="IBrain.Key(string)"/>.
/// </summary>
/// <remarks>
/// A plain neuron (06-08) with the brain's singleton key <c>{brain}/main</c> (R4-19; clean break from the bare brain key).
/// Creating a brain is the platform's act: <see cref="Create"/> is refused for any other caller, and is idempotent for the platform.
/// </remarks>
[DigitalBrain.Contracts.NeuronAlias("brain"), DigitalBrain.Contracts.DefaultNeuronType(Type)]
public interface IBrain
{
    /// <summary>The brain neuron's runtime type.</summary>
    const string Type = "brain";

    /// <summary>The os full key of <paramref name="brainId"/>'s brain neuron.</summary>
    static string Key(string brainId) => $"{brainId}/main";

    /// <summary>
    /// Brings the brain into existence for <paramref name="owner"/>. Publishes <see cref="Created"/>
    /// once and awaits its handlers, so the caller receives a brain that is already provisioned.
    /// Calling it on a created brain changes nothing.
    /// </summary>
    Task Create(Owner owner);

    /// <summary>The brain's state as persisted.</summary>
    /// <exception cref="InvalidOperationException">The brain has not been created.</exception>
    [DigitalBrain.ReadOnlyCall] Task<State> Read();
}
