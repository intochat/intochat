using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Modules.Identity.Accounts;

/// <summary>
/// Promotes an active ephemeral brain in-place from RAM into durable persistent storage when its visitor signs in or registers.
/// The ephemeral brain id is read exclusively from the caller's verified HttpOnly session cookie (never from untrusted body/query).
/// </summary>
internal sealed class BrainClaimService(
    EphemeralBrainRegistry ephemeral,
    IIdentityClient identity,
    AuthorityNeurons neurons,
    EdgeAuthority edge)
{
    /// <summary>
    /// Extracts the active ephemeral brain id from the caller's authenticated cookie principal, if present and still live in RAM.
    /// </summary>
    public string? ActiveEphemeralBrainId(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.User.Identity?.IsAuthenticated != true) { return null; }
        var principalId = DigitalBrain.Runtime.Http.SessionPrincipal.PrincipalId(http.User);
        var brainId = http.User.FindFirst(SessionClaims.Brain)?.Value ?? principalId;
        if (string.IsNullOrWhiteSpace(principalId) || string.IsNullOrWhiteSpace(brainId)) { return null; }
        if (!GuestSurface.IsGuestPrincipal(principalId)) { return null; }
        return ephemeral.Contains(brainId) ? brainId : null;
    }

    /// <summary>
    /// If the current HTTP session is bound to an active ephemeral brain, promotes that exact <c>BrainId</c> in-place
    /// into durable storage owned by <paramref name="ownerPrincipalId"/>. Returns <c>null</c> if the session is not ephemeral.
    /// </summary>
    public async Task<Member?> TryClaimFromSessionAsync(HttpContext http, string ownerPrincipalId, string displayName, CancellationToken ct)
    {
        var ephemeralBrainId = ActiveEphemeralBrainId(http);
        if (ephemeralBrainId is null) { return null; }
        return await ClaimAsync(ephemeralBrainId, ownerPrincipalId, displayName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Promotes <paramref name="ephemeralBrainId"/> in-place into durable storage for <paramref name="ownerPrincipalId"/>:
    /// 1. Binds the exact <c>BrainId</c> in the durable identity directory (carrying over shipped app grants, stripping the temporary guest grant).
    /// 2. Calls <see cref="IBrain.Claim"/> on the brain neuron, which atomically flushes the RAM <c>BrainLog</c> to encrypted disk,
    ///    saves persistent <see cref="BrainResidency.Persistent"/> state, publishes <c>brain.claimed</c>, and removes the ephemeral registry entry.
    /// </summary>
    public async Task<Member> ClaimAsync(string ephemeralBrainId, string ownerPrincipalId, string displayName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ephemeralBrainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        if (GuestSurface.IsGuestPrincipal(ownerPrincipalId))
        {
            throw new ArgumentException("An ephemeral try identity cannot claim a brain.", nameof(ownerPrincipalId));
        }

        if (!ephemeral.TryBeginClaim(ephemeralBrainId))
        {
            throw new InvalidOperationException("No active ephemeral brain is available to claim.");
        }

        try
        {
            var carriedGrants = ephemeral.SnapshotCarriedGrants(ephemeralBrainId);
            var provision = await identity.ClaimEphemeralBrain(ephemeralBrainId, ownerPrincipalId, displayName, carriedGrants)
                .WaitAsync(ct).ConfigureAwait(false);

            await edge.AsKernel(new BrainId(ephemeralBrainId), async () =>
            {
                await neurons.Brain(ephemeralBrainId).Claim(ownerPrincipalId, provision.Member.AccountId).WaitAsync(ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

            ephemeral.MarkPromoted(ephemeralBrainId, ownerPrincipalId);
            return provision.Member;
        }
        finally
        {
            ephemeral.EndClaim(ephemeralBrainId);
        }
    }
}
