namespace IntoChat.OS;

/// <summary>
/// The brain's first neuron. It holds the brain's own state, publishes the brain's lifecycle
/// signals, and is reached through <see cref="IIntoChatOS.Self"/>.
/// </summary>
/// <remarks>
/// Its grain key is the brain's identity; it carries no name. Creating a brain is the platform's
/// act: <see cref="Create"/> is refused for any other caller, and is idempotent for the platform.
/// </remarks>
[Alias("brain"), Orleans.Metadata.DefaultGrainType("brain")]
public interface IBrain : INeuron
{
    /// <summary>
    /// Brings the brain into existence for <paramref name="owner"/>. Publishes <see cref="Created"/>
    /// once and awaits its handlers, so the caller receives a brain that is already provisioned.
    /// Calling it on a created brain changes nothing.
    /// </summary>
    Task Create(Owner owner);

    /// <summary>The brain's state as persisted.</summary>
    Task<State> Read();
}
