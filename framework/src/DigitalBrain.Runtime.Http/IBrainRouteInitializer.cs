namespace DigitalBrain.Runtime.Http;

/// <summary>
/// Optional host hook invoked after <see cref="BrainRouteGroups.Decide"/> admits a caller to a brain-scoped route,
/// for idempotent on-first-access brain provisioning.
/// </summary>
public interface IBrainRouteInitializer
{
    /// <summary>Ensures the brain is initialized for <paramref name="accountId"/> and <paramref name="brainId"/>.</summary>
    ValueTask InitializeAsync(string accountId, string brainId, CancellationToken cancellationToken = default);
}
