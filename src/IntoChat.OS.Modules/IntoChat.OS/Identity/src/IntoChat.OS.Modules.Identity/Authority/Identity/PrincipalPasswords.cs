using System.Text.RegularExpressions;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.AspNetCore.Identity;

namespace IntoChat.OS.Authority.Identity;

// Passwords stay on the machine (R30, R62): it hashes, stores and verifies them in its own credential grain, so
// neither a password nor its hash crosses the identity client.
internal sealed partial class PrincipalPasswords(IIdentityClient identity, BrainBirths births, AuthorityNeurons neurons)
{
    private static readonly PasswordHasher<string> Passwords = new();

    private IPrincipalCredential Credential(string principalId) => neurons.Credential(principalId);

    public async Task<Member> Register(string principalId, string password, string displayName)
    {
        if (principalId is null || principalId.Length is 0 or > 80 || !Username().IsMatch(principalId))
        { throw new ArgumentException("Use a lowercase username containing letters, numbers and hyphens."); }
        if (password is null || password.Length is < 12 or > 256)
        { throw new ArgumentException("Use a password between 12 and 256 characters."); }
        // External sign-in names its principals under this prefix (External.ExternalSignIn.PrincipalOf).
        if (principalId is "owner" or "integrations" || principalId.StartsWith(External.ExternalSignIn.PrincipalPrefix, StringComparison.Ordinal)) { throw new InvalidOperationException("That username is unavailable."); }
        var credential = Credential(principalId);
        if (await credential.Hash() is { } existing)
        {
            // A registration interrupted before its brain or session was provisioned resumes with the same password.
            if (Passwords.VerifyHashedPassword(principalId, existing, password) == PasswordVerificationResult.Failed)
            { throw new InvalidOperationException("That username is unavailable."); }
            return await births.Born((await identity.CompleteRegistration(principalId))
                ?? await identity.RegisterPrincipal(principalId, displayName));
        }
        var hash = Passwords.HashPassword(principalId, password);
        if (!await credential.TryCreate(hash)) { throw new InvalidOperationException("That username is unavailable."); }
        BrainProvision registered;
        try { registered = await identity.RegisterPrincipal(principalId, displayName); }
        catch (Exception refused) when (refused is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            // The trust side refused the username: the credential this attempt stored must not outlive it (R67).
            await credential.Remove(hash);
            throw;
        }
        return await births.Born(registered);
    }

    public async Task<Member?> Authenticate(string principalId, string password)
    {
        if (string.IsNullOrEmpty(principalId) || string.IsNullOrEmpty(password) || password.Length > 256) { return null; }
        var credential = Credential(principalId);
        if (await credential.Hash() is not { } hash) { return null; }
        var result = Passwords.VerifyHashedPassword(principalId, hash, password);
        if (result == PasswordVerificationResult.Failed) { return null; }
        // A local hash for a principal another machine registered is never a sign-in (R67).
        if (result == PasswordVerificationResult.SuccessRehashNeeded) { await credential.Rehash(Passwords.HashPassword(principalId, password)); }
        return await identity.CompleteRegistration(principalId) is { } provision ? await births.Born(provision) : null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Username();
}
