using System.Collections.Concurrent;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Mail.Templates;
using DigitalBrain.Capabilities.Identity;

namespace IntoChat.Cloud.Auth.Services;

// Lobby screens call this. The member is returned to the lobby neuron and the session cookie is written later, at handoff.
public sealed record CloudSignInResult(string Screen, string? Error, Member? Member);

// The kernel's lobby adapter calls this. CloudSignIn stays internal.
public interface ICloudSignIn
{
    Task<CloudSignInResult> Submit(string screen, string visitorId, string? email, string? secret, string? purpose, bool consent, CancellationToken cancellationToken = default);
}

internal sealed class CloudSignIn(LoginService login, AuthCalls calls, AuthHashing hashing, AccountProvisioner provisioner, AuthModuleOptions options, TimeProvider clock) : ICloudSignIn
{
    private readonly ConcurrentDictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly PasswordHasher hasher = new(options.PasswordIterations);

    private sealed record Pending(string CodeId, CodePurpose Purpose, string Email);

    public async Task<CloudSignInResult> Submit(string screen, string visitorId, string? email, string? secret, string? purpose, bool consent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visitorId);
        cancellationToken.ThrowIfCancellationRequested();
        _ = purpose;
        return screen switch
        {
            "signup" => await SignUp(visitorId, email, secret, consent),
            "code" or "reset-code" => await Code(visitorId, secret),
            "factor" => await Factor(visitorId, secret),
            "reset" => await Reset(visitorId, email),
            _ => await Login(visitorId, email, secret),
        };
    }

    private async Task<CloudSignInResult> Login(string visitorId, string? email, string? password)
    {
        if (EmailNormalizer.Normalize(email) is not { } normalized || string.IsNullOrEmpty(password) || password.Length > PasswordHasher.MaxLength)
        {
            return new CloudSignInResult("login", "invalid", null);
        }

        var (check, issue, _) = await login.BeginAsync(normalized, password);
        if (!check.Ok) { return new CloudSignInResult("login", check.Locked ? "locked" : "wrong", null); }
        if (issue.Outcome == CodeIssueOutcome.RateLimited || issue.CodeId is null)
        {
            return new CloudSignInResult("login", "unavailable", null);
        }

        pending[visitorId] = new Pending(issue.CodeId, CodePurpose.Login, normalized);
        return new CloudSignInResult("code", null, null);
    }

    private async Task<CloudSignInResult> SignUp(string visitorId, string? email, string? password, bool consent)
    {
        if (!consent) { return new CloudSignInResult("signup", "consent", null); }
        if (EmailNormalizer.Normalize(email) is not { } normalized) { return new CloudSignInResult("signup", "invalid", null); }
        if (!PasswordHasher.IsValidLength(password)) { return new CloudSignInResult("signup", "invalid", null); }
        var hash = hashing.EmailSubject(normalized);
        var passwordHash = hasher.Hash(password!);
        var entry = await calls.EmailIndex(hash, index => index.Get());
        var verified = entry.Status is EmailIndexStatus.Verified or EmailIndexStatus.ChangePending;
        var display = normalized[..normalized.IndexOf('@')];
        if (!verified)
        {
            await calls.EmailIndex(hash, index => index.SetPending(new PendingSignUp(normalized, passwordHash, display, "en", clock.GetUtcNow())));
        }

        var delivery = new CodeDelivery(normalized, "en", display, MailTemplateId.SignupCode, verified ? MailTemplateId.AccountAlreadyExists : null);
        var issue = await calls.Code(hash, CodePurpose.Signup, code => code.Issue(new CodeIssueRequest(hash, verified, true, delivery, ["email"], "pwd")));
        if (issue.Outcome == CodeIssueOutcome.RateLimited || issue.CodeId is null)
        {
            return new CloudSignInResult("signup", "unavailable", null);
        }

        pending[visitorId] = new Pending(issue.CodeId, CodePurpose.Signup, normalized);
        return new CloudSignInResult("code", null, null);
    }

    private async Task<CloudSignInResult> Code(string visitorId, string? secret)
    {
        if (!pending.TryGetValue(visitorId, out var row) || string.IsNullOrWhiteSpace(secret))
        {
            return new CloudSignInResult("code", "wrong", null);
        }

        var verdictTask = calls.CodeById(row.CodeId, (code, _) => code.Verify(row.CodeId, secret));
        if (verdictTask is null) { return new CloudSignInResult("code", "wrong", null); }
        var verdict = await verdictTask;
        if (verdict.Outcome != CodeOutcome.Success) { return new CloudSignInResult(row.Purpose == CodePurpose.Reset ? "reset-code" : "code", "wrong", null); }
        if (row.Purpose == CodePurpose.Signup)
        {
            var member = await provisioner.CompleteSignUpAsync(hashing.EmailSubject(row.Email));
            if (member is null) { return new CloudSignInResult("code", "wrong", null); }
            pending.TryRemove(visitorId, out _);
            return new CloudSignInResult("login", null, member);
        }

        if (row.Purpose == CodePurpose.Reset)
        {
            pending.TryRemove(visitorId, out _);
            return new CloudSignInResult("reset-done", null, null);
        }

        var challengeTask = calls.CodeById(row.CodeId, (code, _) => code.GetChallenge(row.CodeId));
        if (challengeTask is null) { return new CloudSignInResult("code", "wrong", null); }
        var challenge = await challengeTask;
        if (challenge is null) { return new CloudSignInResult("code", "wrong", null); }
        var entry = await calls.EmailIndex(challenge.SubjectHash, index => index.Get());
        if (entry.PrincipalId is null || entry.Email is null) { return new CloudSignInResult("code", "wrong", null); }
        if (await login.ChallengeExternalAsync(entry.Email, challenge.AmrOrigin + " otp_email") is { CodeId: { } next })
        {
            pending[visitorId] = new Pending(next, CodePurpose.Login, entry.Email);
            return new CloudSignInResult("factor", null, null);
        }

        var signedIn = await provisioner.RegisterAsync(entry.PrincipalId, entry.PendingDisplayName ?? entry.Email);
        pending.TryRemove(visitorId, out _);
        return new CloudSignInResult("login", null, signedIn);
    }

    private async Task<CloudSignInResult> Factor(string visitorId, string? secret)
    {
        if (!pending.TryGetValue(visitorId, out var row) || string.IsNullOrWhiteSpace(secret))
        {
            return new CloudSignInResult("factor", "wrong", null);
        }

        var challengeTask = calls.CodeById(row.CodeId, (code, _) => code.GetChallenge(row.CodeId));
        if (challengeTask is null) { return new CloudSignInResult("factor", "wrong", null); }
        var challenge = await challengeTask;
        if (challenge is null) { return new CloudSignInResult("factor", "wrong", null); }
        var entry = await calls.EmailIndex(challenge.SubjectHash, index => index.Get());
        if (entry.PrincipalId is null) { return new CloudSignInResult("factor", "wrong", null); }
        var attemptTask = calls.CodeById(row.CodeId, (code, _) => code.ReserveAttempt(row.CodeId));
        if (attemptTask is null) { return new CloudSignInResult("factor", "wrong", null); }
        var attempt = await attemptTask;
        if (!attempt.Allowed) { return new CloudSignInResult("factor", "wrong", null); }
        if (!await calls.Credential(entry.PrincipalId, credential => credential.VerifyTotp(secret)))
        {
            return new CloudSignInResult("factor", "wrong", null);
        }

        var consumed = calls.CodeById(row.CodeId, (code, _) => code.Consume(row.CodeId));
        if (consumed is null || !await consumed) { return new CloudSignInResult("factor", "wrong", null); }
        var member = await provisioner.RegisterAsync(entry.PrincipalId, entry.PendingDisplayName ?? entry.Email ?? row.Email);
        pending.TryRemove(visitorId, out _);
        return new CloudSignInResult("login", null, member);
    }

    private async Task<CloudSignInResult> Reset(string visitorId, string? email)
    {
        if (EmailNormalizer.Normalize(email) is not { } normalized) { return new CloudSignInResult("reset", "invalid", null); }
        var hash = hashing.EmailSubject(normalized);
        var entry = await calls.EmailIndex(hash, index => index.Get());
        var verified = entry.Status == EmailIndexStatus.Verified && entry.Email is not null;
        var delivery = verified ? new CodeDelivery(normalized, "en", entry.PendingDisplayName ?? normalized, MailTemplateId.ResetCode, null) : null;
        var issue = await calls.Code(hash, CodePurpose.Reset, code => code.Issue(new CodeIssueRequest(hash, !verified, true, delivery, ["email"], "pwd")));
        if (issue.Outcome == CodeIssueOutcome.RateLimited || issue.CodeId is null)
        {
            return new CloudSignInResult("reset", "unavailable", null);
        }

        pending[visitorId] = new Pending(issue.CodeId, CodePurpose.Reset, normalized);
        return new CloudSignInResult("reset-code", null, null);
    }
}
