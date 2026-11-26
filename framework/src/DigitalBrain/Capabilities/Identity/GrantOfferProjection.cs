using DigitalBrain.Capabilities.Identity;

namespace DigitalBrain.Components;

/// <summary>
/// Additive convergence projections between legacy/canonical identity <see cref="Grant"/> records and component
/// <see cref="Offer"/> records.
/// </summary>
public static class GrantOfferProjection
{
    /// <summary>Returns the component <see cref="Role"/> implied by <paramref name="grant"/>.</summary>
    public static Role EffectiveRole(this Grant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return string.Equals(grant.RoleName, "editor", StringComparison.OrdinalIgnoreCase)
            ? Role.Editor
            : Role.Viewer;
    }

    /// <summary>
    /// Projects <paramref name="grant"/> to a component <see cref="Offer"/>, or <see langword="null"/> when the grant
    /// is revoked or lacks enough identity to form a resource and subject.
    /// </summary>
    public static Offer? ToOffer(this Grant grant, NeuronId? fallbackResource = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return grant.ToOffer(grant.EffectiveRole(), fallbackResource);
    }

    /// <summary>
    /// Projects <paramref name="grant"/> to a component <see cref="Offer"/> with an explicit <paramref name="role"/>,
    /// or <see langword="null"/> when the grant is revoked or lacks enough identity to form a resource and subject.
    /// </summary>
    public static Offer? ToOffer(this Grant grant, Role role, NeuronId? fallbackResource = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.Revoked)
        {
            return null;
        }

        var subject = grant.Subject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var resource = ResolveResource(grant, fallbackResource);
        return resource is { } id ? new Offer(id, subject, role) : null;
    }

    /// <summary>Creates a canonical <see cref="Grant"/> representing a standing component <see cref="Offer"/>.</summary>
    public static Grant FromOffer(Offer offer, GrantMode mode = GrantMode.Always)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var scope = offer.Role == Role.Editor ? "editor" : "viewer";
        var target = offer.Resource.ToString();
        return new Grant
        {
            Owner = "brain:" + offer.Resource.Brain.Value,
            WorkspaceId = offer.Resource.Brain.Value,
            Grantee = offer.To,
            AppId = offer.To,
            Target = target,
            TargetNeuronKey = target,
            Scope = scope,
            SemanticTypeId = scope,
            Mode = mode,
        };
    }

    private static NeuronId? ResolveResource(Grant grant, NeuronId? fallbackResource)
    {
        var candidate = !string.IsNullOrWhiteSpace(grant.TargetNeuronKey) ? grant.TargetNeuronKey : grant.Target;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var trimmed = candidate.StartsWith("neuron://", StringComparison.Ordinal)
                ? candidate["neuron://".Length..]
                : candidate;
            if (NeuronId.TryParse(trimmed, out var parsed))
            {
                return parsed;
            }
        }

        if (fallbackResource is { } fallback)
        {
            return fallback;
        }

        var brain = grant.WorkspaceId.Length > 0
            ? grant.WorkspaceId
            : grant.Owner.StartsWith("brain:", StringComparison.Ordinal) ? grant.Owner["brain:".Length..] : grant.Owner;
        var scope = grant.Scope.Length > 0 ? grant.Scope : grant.SemanticTypeId;
        if (!string.IsNullOrWhiteSpace(brain) && !string.IsNullOrWhiteSpace(scope))
        {
            return new NeuronId(new BrainId(brain), "semantic.grant", scope);
        }

        return null;
    }
}
