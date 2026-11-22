using DigitalBrain.Access;
using DigitalBrain.Contracts;

namespace DigitalBrain.Components;

/// <summary>
/// Per-brain registry of standing <see cref="Offer"/>s granting friends access to <see cref="OfferedAttribute"/> neurons of this brain.
/// Hosted at <c>{brain}/component.offers/default</c>.
/// </summary>
[NeuronAlias("component.offers"), DefaultNeuronType("component.offers")]
public interface IOffers
{
    /// <summary>The singleton key of a brain's offers neuron.</summary>
    public const string DefaultKey = "default";

    /// <summary>Grants or updates an offer for <paramref name="to"/> on <paramref name="resource"/>.</summary>
    Task Offer(NeuronId resource, string to, Role role, CancellationToken ct = default);

    /// <summary>Revokes the offer for <paramref name="to"/> on <paramref name="resource"/>, if any.</summary>
    Task Revoke(NeuronId resource, string to, CancellationToken ct = default);

    /// <summary>Revokes all offers granted to <paramref name="to"/> in this brain.</summary>
    Task RevokeAll(string to, CancellationToken ct = default);

    /// <summary>Lists all active offers in this brain.</summary>
    [ReadOnlyCall]
    Task<Offer[]> List(CancellationToken ct = default);

    /// <summary>Returns the role granted to <paramref name="account"/> on <paramref name="resource"/>, or <see langword="null"/> if none.</summary>
    [Internal, ReadOnlyCall]
    Task<Role?> RoleOf(NeuronId resource, string account, CancellationToken ct = default);
}
