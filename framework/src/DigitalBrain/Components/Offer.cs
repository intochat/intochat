namespace DigitalBrain.Components;

/// <summary>The access role granted to a friend for an offered neuron.</summary>
public enum Role
{
    /// <summary>May invoke only <see cref="DigitalBrain.Contracts.ReadOnlyCallAttribute"/> operations.</summary>
    Viewer,

    /// <summary>May invoke both read-only and mutating shared operations.</summary>
    Editor,
}

/// <summary>A standing offer granting <paramref name="To"/> access to <paramref name="Resource"/> with <paramref name="Role"/>.</summary>
/// <param name="Resource">The offered neuron.</param>
/// <param name="To">The account identifier of the friend.</param>
/// <param name="Role">The granted role.</param>
public sealed record Offer(NeuronId Resource, string To, Role Role);

/// <summary>
/// Marks a <see cref="DigitalBrain.Access.ShareableAttribute"/> contract as gated by per-resource <see cref="IOffers"/>.
/// Foreign callers are admitted only when an offer exists for their account, and <see cref="Role.Viewer"/> callers may
/// invoke only <see cref="DigitalBrain.Contracts.ReadOnlyCallAttribute"/> operations.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class OfferedAttribute : Attribute;

/// <summary>Optional friendship check consulted by <see cref="IOffers.Offer"/> before granting an offer.</summary>
public interface IFriendCheck
{
    /// <summary>Returns <see langword="true"/> when <paramref name="account"/> is a confirmed friend of <paramref name="owner"/>.</summary>
    Task<bool> IsFriend(BrainId owner, string account, CancellationToken ct = default);
}
