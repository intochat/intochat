namespace DigitalBrain.Capabilities.Secrets;

/// <summary>
/// secrets@1: the named secrets of a brain. Values go in through <see cref="Set"/> and come out only through
/// <see cref="Resolve"/>; everything else carries a <see cref="SecretRef"/>, which never holds the value.
/// </summary>
/// <remarks>
/// There is no caller parameter (D-15): the implementation reads the caller the runtime stamped on the call, so an app
/// cannot name another brain's caller. A caller that may not touch a secret gets <see cref="SecretDenied"/>.
/// </remarks>
[Capability("secrets", 1)]
public interface ISecrets
{
    /// <summary>Stores <paramref name="value"/> under <paramref name="name"/>, replacing any earlier value.</summary>
    /// <returns>A set reference to the stored secret.</returns>
    Task<SecretRef> Set(string name, string label, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes the secret; removing an unknown name is a no-op.</summary>
    Task Remove(string name, CancellationToken cancellationToken = default);

    /// <summary>Returns the value behind <paramref name="secret"/>.</summary>
    /// <exception cref="SecretNotFound">The reference is unset or the secret was removed.</exception>
    /// <exception cref="SecretDenied">The runtime caller may not read the secret.</exception>
    Task<string> Resolve(SecretRef secret, CancellationToken cancellationToken = default);
}
