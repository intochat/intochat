using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Auth.Services;

// Shared challenge construction for password, Google and website handoff flows.
internal sealed class LoginService(AuthCalls calls, AuthHashing hashing, AuthModuleOptions options)
{
    private readonly PasswordHasher hasher = new(options.PasswordIterations);
    private string? dummyHash;

    public async Task<CodeIssue?> ChallengeExternalAsync(string normalizedEmail, string amrOrigin = "google")
    {
        var hash = hashing.EmailSubject(normalizedEmail);
        var entry = await calls.EmailIndex(hash, index => index.Get());
        if (entry.Status != EmailIndexStatus.Verified || entry.PrincipalId is null) { throw new InvalidOperationException("External sign-in requires a verified account."); }
        if (!(await calls.Credential(entry.PrincipalId, credential => credential.GetFlags())).HasTotp) { return null; }
        return await calls.Code(hash, CodePurpose.Login, code => code.Issue(new CodeIssueRequest(
            hash, false, false, null, ["authenticator", "recovery"], amrOrigin)));
    }

    public async Task<(PasswordCheck Check, CodeIssue Issue, string[] Methods)> BeginAsync(string email, string password)
    {
        var hash = hashing.EmailSubject(email);
        var entry = await calls.EmailIndex(hash, index => index.Get());
        var verified = entry.Status == EmailIndexStatus.Verified && entry.PrincipalId is not null;
        PasswordCheck check;
        if (verified)
        {
            check = await calls.Credential(entry.PrincipalId!, credential => credential.CheckPassword(password));
        }
        else
        {
            hasher.Verify(password, dummyHash ??= hasher.Hash("dummy authentication password"));
            check = new PasswordCheck(false, false, null);
        }
        var methods = verified && (await calls.Credential(entry.PrincipalId!, credential => credential.GetFlags())).HasTotp ? new[] { "email", "authenticator", "recovery" } : ["email"];
        // Failed attempts use a separate decoy slot so they cannot exhaust a real account's mail budget.
        var challengeHash = check.Ok ? hash : hashing.KeyHash(AuthHashing.EmailSubjectPurpose, "login-decoy:" + hash);
        var issue = await calls.Code(challengeHash, CodePurpose.Login, code => code.Issue(new CodeIssueRequest(
            challengeHash, !check.Ok, false,
            check.Ok ? new CodeDelivery(email, entry.PendingLanguage ?? "en", entry.PendingDisplayName ?? email, MailTemplateId.LoginCode, null) : null,
            methods, "pwd")));
        return (check, issue, methods);
    }
}

