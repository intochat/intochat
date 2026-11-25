using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Identity;
using Microsoft.AspNetCore.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Accounts;

// Passwords stay on the machine (R30, R62): it hashes, stores and verifies them in its own credential grain, so
// neither a password nor its hash crosses the identity client.
internal sealed partial class PrincipalPasswords(IIdentityClient identity, BrainBirths births, AuthorityNeurons neurons)
{
    private static readonly PasswordHasher<string> Passwords = new();

    private IPrincipalCredential Credential(string principalId) => neurons.Credential(principalId);

    public async Task<Member> Register(string principalId, string password, string displayName)
    {
        ValidateRegistration(principalId, password);
        var credential = Credential(principalId);
        if (await Own(credential.Hash) is { } existing)
        {
            // A registration interrupted before its brain or session was provisioned resumes with the same password.
            if (Passwords.VerifyHashedPassword(principalId, existing, password) == PasswordVerificationResult.Failed)
            { throw new InvalidOperationException("That username is unavailable."); }
            return await births.Born((await identity.CompleteRegistration(principalId))
                ?? await identity.RegisterPrincipal(principalId, displayName));
        }
        var hash = Passwords.HashPassword(principalId, password);
        if (!await Own(() => credential.TryCreate(hash))) { throw new InvalidOperationException("That username is unavailable."); }
        BrainProvision registered;
        try { registered = await identity.RegisterPrincipal(principalId, displayName); }
        catch (Exception refused) when (refused is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            // The trust side refused the username: the credential this attempt stored must not outlive it (R67).
            await Own(async () => { await credential.Remove(hash); return true; });
            throw;
        }
        return await births.Born(registered);
    }

    public async Task<Member> RegisterAndClaim(string principalId, string password, string displayName, string ephemeralBrainId, BrainClaimService claimService, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claimService);
        ValidateRegistration(principalId, password);
        if (!string.IsNullOrWhiteSpace(principalId) && await identity.IsRegistered(principalId).WaitAsync(ct))
        {
            throw new InvalidOperationException("That username is unavailable.");
        }

        var credential = Credential(principalId);
        if (await Own(credential.Hash) is { } existing)
        {
            // Only an interrupted, incomplete registration may resume with the same password; an already-registered principal is rejected above.
            if (Passwords.VerifyHashedPassword(principalId, existing, password) == PasswordVerificationResult.Failed)
            { throw new InvalidOperationException("That username is unavailable."); }
            return await claimService.ClaimAsync(ephemeralBrainId, principalId, displayName, ct);
        }
        var hash = Passwords.HashPassword(principalId, password);
        if (!await Own(() => credential.TryCreate(hash))) { throw new InvalidOperationException("That username is unavailable."); }
        try
        {
            return await claimService.ClaimAsync(ephemeralBrainId, principalId, displayName, ct);
        }
        catch (Exception refused) when (refused is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            await Own(async () => { await credential.Remove(hash); return true; });
            throw;
        }
    }

    private static void ValidateRegistration(string principalId, string password)
    {
        if (principalId is null || principalId.Length is 0 or > 80 || !Username().IsMatch(principalId))
        { throw new ArgumentException("Use a lowercase username containing letters, numbers and hyphens."); }
        if (password is null || password.Length is < 12 or > 256)
        { throw new ArgumentException("Use a password between 12 and 256 characters."); }
        // External sign-in names its principals under its prefix (IntoChat.OS.Modules.Identity.External.ExternalSignIn.PrincipalOf); the guest prefix marks a /try guest.
        if (principalId is "owner" or "integrations" || principalId.StartsWith(IntoChat.OS.Modules.Identity.External.ExternalSignIn.PrincipalPrefix, StringComparison.Ordinal)
            || principalId.StartsWith(GuestSurface.BrainPrefix, StringComparison.Ordinal))
        { throw new InvalidOperationException("That username is unavailable."); }
    }

    public async Task<Member?> Authenticate(string principalId, string password)
    {
        if (string.IsNullOrEmpty(principalId) || string.IsNullOrEmpty(password) || password.Length > 256) { return null; }
        var credential = Credential(principalId);
        if (await Own(credential.Hash) is not { } hash) { return null; }
        var result = Passwords.VerifyHashedPassword(principalId, hash, password);
        if (result == PasswordVerificationResult.Failed) { return null; }
        // A local hash for a principal another machine registered is never a sign-in (R67).
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        { await Own(async () => { await credential.Rehash(Passwords.HashPassword(principalId, password)); return true; }); }
        return await identity.CompleteRegistration(principalId) is { } provision ? await births.Born(provision) : null;
    }

    // The credential grain is the identity module's own: registration and sign-in run before any person is stamped.
    private async Task<T> Own<T>(Func<Task<T>> call)
    {
        using var own = neurons.Own();
        return await call();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Username();
}
