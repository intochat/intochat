using System.Text.RegularExpressions;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.AspNetCore.Identity;

namespace DigitalBrain.Platform.Identity;

// Passwords stay on the machine (R30): it hashes on registration and verifies on sign-in against the hash the
// trust side keeps, so no plaintext credential ever crosses the identity client.
internal sealed partial class PrincipalPasswords(IIdentityClient identity, BrainBirths births)
{
    private static readonly PasswordHasher<string> Passwords = new();

    public async Task<Member> Register(string principalId, string password, string displayName)
    {
        if (principalId is null || principalId.Length is 0 or > 80 || !Username().IsMatch(principalId))
        { throw new ArgumentException("Use a lowercase username containing letters, numbers and hyphens."); }
        if (password is null || password.Length is < 12 or > 256)
        { throw new ArgumentException("Use a password between 12 and 256 characters."); }
        if (principalId is "owner" or "integrations") { throw new InvalidOperationException("That username is unavailable."); }
        if (await identity.Credential(principalId) is { } existing)
        {
            // A registration interrupted before its brain was provisioned resumes with the same password.
            if (existing.RegistrationComplete || Passwords.VerifyHashedPassword(principalId, existing.PasswordHash, password) == PasswordVerificationResult.Failed)
            { throw new InvalidOperationException("That username is unavailable."); }
            return await births.Born((await identity.CompleteRegistration(principalId))!);
        }
        return await births.Born(await identity.RegisterPrincipal(principalId, Passwords.HashPassword(principalId, password), displayName));
    }

    public async Task<Member?> Authenticate(string principalId, string password)
    {
        if (string.IsNullOrEmpty(principalId) || string.IsNullOrEmpty(password) || password.Length > 256) { return null; }
        if (await identity.Credential(principalId) is not { } credential) { return null; }
        var result = Passwords.VerifyHashedPassword(principalId, credential.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed) { return null; }
        if (result == PasswordVerificationResult.SuccessRehashNeeded) { await identity.SetPasswordHash(principalId, Passwords.HashPassword(principalId, password)); }
        return await identity.CompleteRegistration(principalId) is { } provision ? await births.Born(provision) : null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Username();
}
