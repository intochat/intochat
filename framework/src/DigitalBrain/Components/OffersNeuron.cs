using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;

namespace DigitalBrain.Units;

/// <summary>Persisted state of <see cref="OffersNeuron"/>.</summary>
/// <param name="Offers">The active offers for this brain.</param>
public sealed record OffersState(Offer[] Offers)
{
    /// <summary>An empty offers state.</summary>
    public OffersState() : this([]) { }
}

/// <summary>Emitted when a brain's standing offers change.</summary>
/// <param name="Resource">The offered resource, or <see langword="null"/> when all offers for <paramref name="To"/> were revoked.</param>
/// <param name="To">The friend account affected.</param>
/// <param name="Role">The new role, or <see langword="null"/> on revocation.</param>
[Signal("component.offer-changed")]
public sealed record OfferChanged(NeuronId? Resource, string To, Role? Role) : Signal;

/// <summary>
/// Per-brain standing offers registry (<c>component.offers</c>).
/// </summary>
/// <param name="friends">Optional friendship verifier consulted before granting an offer.</param>
[Neuron("component.offers")]
public sealed class OffersNeuron(IFriendCheck? friends = null) : Neuron<OffersState>, IOffers
{
    /// <inheritdoc />
    public async Task Offer(NeuronId resource, string to, Role role, CancellationToken ct = default)
    {
        RequireOwner();
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (resource.Brain != Id.Brain)
        {
            throw new UnauthorizedAccessException(
                $"Cannot offer resource '{resource}' belonging to brain '{resource.Brain.Value}' from brain '{Id.Brain.Value}'.");
        }

        if (friends is not null && !await friends.IsFriend(Id.Brain, to, ct).ConfigureAwait(true))
        {
            throw new UnauthorizedAccessException($"Cannot offer '{resource}' to '{to}': not a confirmed friend.");
        }

        var current = State.Offers ?? [];
        var next = current
            .Where(o => !(o.Resource == resource && string.Equals(o.To, to, StringComparison.Ordinal)))
            .Append(new Offer(resource, to, role))
            .ToArray();
        await Save(new OffersState(next), new OfferChanged(resource, to, role)).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task Revoke(NeuronId resource, string to, CancellationToken ct = default)
    {
        RequireOwner();
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        var current = State.Offers ?? [];
        var next = current
            .Where(o => !(o.Resource == resource && string.Equals(o.To, to, StringComparison.Ordinal)))
            .ToArray();
        if (next.Length == current.Length) { return; }

        await Save(new OffersState(next), new OfferChanged(resource, to, null)).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task RevokeAll(string to, CancellationToken ct = default)
    {
        RequireOwner();
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        var current = State.Offers ?? [];
        var next = current
            .Where(o => !string.Equals(o.To, to, StringComparison.Ordinal))
            .ToArray();
        if (next.Length == current.Length) { return; }

        await Save(new OffersState(next), new OfferChanged(null, to, null)).ConfigureAwait(true);
    }

    /// <inheritdoc />
    [ReadOnlyCall]
    public Task<Offer[]> List(CancellationToken ct = default)
    {
        RequireOwner();
        return Task.FromResult((State.Offers ?? []).ToArray());
    }

    /// <inheritdoc />
    [ReadOnlyCall]
    public Task<Role?> RoleOf(NeuronId resource, string account, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var match = (State.Offers ?? []).FirstOrDefault(o =>
            o.Resource == resource && string.Equals(o.To, account, StringComparison.Ordinal));
        return Task.FromResult<Role?>(match?.Role);
    }

    private void RequireOwner()
        => RequireSameBrain(() => new UnauthorizedAccessException($"Only brain '{Id.Brain.Value}' may manage its offers."));
}
