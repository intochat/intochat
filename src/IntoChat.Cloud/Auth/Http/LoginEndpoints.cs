using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

internal static class LoginEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/login", BeginAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
        endpoints.MapPost("/auth/login/challenges/{challengeId}/verify", VerifyAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
        endpoints.MapPost("/auth/login/challenges/{challengeId}/resend", ResendAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
    }

    private static async Task<IResult> BeginAsync(HttpContext http, LoginService login, AuthAudit audit)
    {
        var (body, failure) = await AuthProblems.ReadAsync<LoginRequest>(http.Request);
        if (failure is not null) { return failure; }
        if (EmailNormalizer.Normalize(body!.Email) is not { } email || string.IsNullOrEmpty(body.Password) || body.Password.Length > PasswordHasher.MaxLength)
        {
            return InvalidCredentials();
        }
        var (check, issue, methods) = await login.BeginAsync(email, body.Password);
        if (!check.Ok)
        {
            await audit.RecordAsync(null, AuthAuditKind.LoginFailed, "failed", http, "password");
            return check.Locked ? Results.Json(new { code = "LOGIN_LOCKED", retryAt = check.LockedUntil }, statusCode: 423) : InvalidCredentials();
        }
        return Challenge(issue, methods);
    }

    internal static IResult Challenge(CodeIssue issue, string[] methods) => issue.Outcome == CodeIssueOutcome.RateLimited
        ? AuthProblems.RateLimited(issue.RetryAt)
        : Results.Json(new { challengeId = issue.CodeId, methods, expiresAt = issue.ExpiresAt, resendAvailableAt = issue.ResendAvailableAt }, statusCode: 202);

    private static IResult InvalidCredentials() => AuthProblems.Code(401, "LOGIN_INVALID");

    private static async Task<IResult> VerifyAsync(string challengeId, HttpContext http, AuthCalls calls, AccountProvisioner provisioner, SessionStarter sessions, AuthAudit audit, LoginService login, ExternalTicketIssuer tickets)
    {
        var (body, failure) = await AuthProblems.ReadAsync<VerifyRequest>(http.Request);
        if (failure is not null) { return failure; }
        if (IAuthCode.KeyForCodeId(challengeId, out var purpose) is null || purpose != CodePurpose.Login)
        {
            return AuthProblems.NotFound();
        }
        var challenge = await calls.CodeById(challengeId, (code, _) => code.GetChallenge(challengeId))!;
        if (challenge is null) { return AuthProblems.NotFound(); }
        if (body!.Method is null || !challenge.AllowedMethods.Contains(body.Method, StringComparer.Ordinal))
        {
            return AuthProblems.BadRequest("METHOD_NOT_ALLOWED");
        }
        var entry = await calls.EmailIndex(challenge.SubjectHash, index => index.Get());
        if (entry.Status != EmailIndexStatus.Verified || entry.PrincipalId is null) { return AuthProblems.NotFound(); }
        var suffix = " otp_email";
        if (body.Method == "email")
        {
            if (!CodeEndpoints.IsCode(body.Code)) { return AuthProblems.BadRequest(AuthProblems.RequestInvalid); }
            var verdict = await calls.CodeById(challengeId, (code, _) => code.Verify(challengeId, body.Code!))!;
            if (verdict.Outcome != CodeOutcome.Success) { return AuthProblems.Verdict(verdict); }
            // Email ownership never downgrades an enabled authenticator. A proved email advances to a fresh,
            // factor-only challenge and does not issue a session.
            if (await login.ChallengeExternalAsync(entry.Email!, challenge.AmrOrigin + " otp_email") is { } next)
            {
                return Challenge(next, ["authenticator", "recovery"]);
            }
        }
        else
        {
            var attempt = await calls.CodeById(challengeId, (code, _) => code.ReserveAttempt(challengeId))!;
            if (!attempt.Allowed) { return AuthProblems.BadRequest(AuthErrors.EmailCodeLocked); }
            var flags = await calls.Credential(entry.PrincipalId, credential => credential.GetFlags());
            if (!flags.HasTotp) { return AuthProblems.BadRequest(AuthErrors.AuthenticatorNotEnabled); }
            var ok = body.Method == "authenticator"
                ? await calls.Credential(entry.PrincipalId, credential => credential.VerifyTotp(body.Code ?? ""))
                : body.Method == "recovery" && await calls.Credential(entry.PrincipalId, credential => credential.UseRecovery(body.Code ?? ""));
            if (!ok) { return AuthProblems.BadRequest(body.Method == "recovery" ? AuthErrors.RecoveryCodeInvalid : AuthErrors.AuthenticatorCodeInvalid); }
            if (!await calls.CodeById(challengeId, (code, _) => code.Consume(challengeId))!) { return AuthProblems.NotFound(); }
            suffix = body.Method == "authenticator" ? " otp" : " recovery";
        }
        var member = await provisioner.RegisterAsync(entry.PrincipalId, entry.PendingDisplayName ?? entry.Email!);
        await sessions.StartAsync(http, member, challenge.AmrOrigin + suffix, http.RequestAborted);
        await audit.RecordAsync(member.PrincipalId, AuthAuditKind.LoginSucceeded, "success", http, body.Method);
        var redirect = await tickets.RedirectFor(http.Request, entry.Email!, entry.PendingDisplayName, challenge.AmrOrigin + suffix);
        return ExternalTicketIssuer.SignedIn(member, redirect);
    }

    private static async Task<IResult> ResendAsync(string challengeId, AuthCalls calls)
    {
        if (IAuthCode.KeyForCodeId(challengeId, out var purpose) is null || purpose != CodePurpose.Login) { return AuthProblems.NotFound(); }
        var challenge = await calls.CodeById(challengeId, (code, _) => code.GetChallenge(challengeId))!;
        if (challenge is null) { return AuthProblems.NotFound(); }
        var issue = await calls.CodeById(challengeId, (code, _) => code.Resend())!;
        return Challenge(issue, challenge.AllowedMethods);
    }

    internal sealed record LoginRequest(string? Email, string? Password);
    internal sealed record VerifyRequest(string? Method, string? Code);
}

