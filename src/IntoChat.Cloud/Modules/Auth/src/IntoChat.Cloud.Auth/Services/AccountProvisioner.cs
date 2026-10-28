using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Organisations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Services;

// Turns a verified address into a principal with its own account, brain and personal organisation. Every step is
// idempotent, so a retry after a partial failure completes the same registration (no second principal or brain).
internal sealed class AccountProvisioner(IServiceProvider services, AuthCalls calls, TimeProvider clock, AuthHashing hashing, CodeMailer mailer)
{
    public async Task<Member?> EnsureVerifiedEmailAsync(string email, string displayName, string language = "en")
    {
        return await CompleteExternalAsync(hashing.EmailSubject(email), email, displayName, language, null);
    }

    public Task<Member?> CompleteGoogleSignUpAsync(string email, string displayName, string googleSub, string language = "en") =>
        CompleteExternalAsync(hashing.EmailSubject(email), email, displayName, language, googleSub);

    private async Task<Member?> CompleteExternalAsync(string emailHash, string email, string displayName, string language, string? googleSub)
    {
        var entry = await calls.EmailIndex(emailHash, index => index.PrepareExternal(email, displayName, language));
        if (entry?.PrincipalId is not { } principalId) { return null; }
        var returning = entry.Status == EmailIndexStatus.Verified;
        if (googleSub is not null && !await calls.EmailIndex(emailHash, index => index.LinkGoogle(principalId, googleSub))) { return null; }
        var member = await RegisterAsync(principalId, displayName);
        if (!await calls.Credential(principalId, credential => credential.SetEmailHash(emailHash))) { return null; }
        if (!await calls.EmailIndex(emailHash, index => index.MarkVerified(principalId, member.AccountId, member.BrainId))) { return null; }
        await EnsurePersonalAsync(member, displayName, email, language);
        if (!returning) { await WelcomeOnceAsync(principalId, email, language, displayName); }
        return member;
    }

    // Completes the email sign-up recorded under this address hash; null when nothing is pending or verified there.
    public async Task<Member?> CompleteSignUpAsync(string emailHash)
    {
        var entry = await calls.EmailIndex(emailHash, index => index.Get());
        if (entry.Status is not (EmailIndexStatus.Pending or EmailIndexStatus.Verified) || entry.PrincipalId is not { } principalId || entry.Email is null)
        {
            return null;
        }

        var displayName = entry.PendingDisplayName ?? entry.Email;
        var member = await RegisterAsync(principalId, displayName);
        if (entry.PendingPasswordHash is { } passwordHash)
        {
            await calls.Credential(principalId, credential => credential.SetPassword(passwordHash));
        }

        if (!await calls.Credential(principalId, credential => credential.SetEmailHash(emailHash))) { return null; }
        if (!await calls.EmailIndex(emailHash, index => index.MarkVerified(principalId, member.AccountId, member.BrainId))) { return null; }
        await EnsurePersonalAsync(member, displayName, entry.Email, entry.PendingLanguage ?? "en");
        if (entry.Status == EmailIndexStatus.Pending) { await WelcomeOnceAsync(principalId, entry.Email, entry.PendingLanguage ?? "en", displayName); }
        return member;
    }

    // The profile neuron grants the claim to exactly one caller per principal, so a retry, a second provider
    // sign-in or a concurrent first sign-in can never send the welcome twice.
    private async Task WelcomeOnceAsync(string principalId, string email, string language, string displayName)
    {
        if (await calls.Profile(principalId, profile => profile.ClaimWelcome()))
        {
            await mailer.SendWelcomeAsync(email, language, displayName);
        }
    }

    // The principal's default membership; registers and provisions its brain the first time (order as PrincipalPasswords).
    public async Task<Member> RegisterAsync(string principalId, string displayName)
    {
        var provisioning = services.GetRequiredKeyedService<IIdentity>(Capability.Identity.V1Id);
        var provision = await provisioning.CompleteRegistration(principalId);
        if (provision is null)
        {
            try { provision = await provisioning.RegisterPrincipal(principalId, displayName); }
            catch (InvalidOperationException)
            {
                // Another request may have registered this same verified principal after our initial read.
                // Only resume a membership the authority confirms; unrelated registration failures stay failures.
                provision = await provisioning.CompleteRegistration(principalId);
                if (provision is null) { throw; }
            }
        }
        return provision.Member;
    }

    public Task EnsurePersonalAsync(Member member, string displayName, string email, string language) =>
        calls.Directory(member.PrincipalId, directory => directory.EnsurePersonal(new PersonalOrganisationRequest(
            member.PrincipalId,
            member.AccountId,
            member.BrainId,
            displayName,
            email,
            language is "ru" ? "ru" : "en",
            clock.GetUtcNow())));
}
