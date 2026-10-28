using System.Text.RegularExpressions;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

// POST /auth/codes (resend) and POST /auth/codes/{codeId}/verify. Unknown and verified addresses resend a decoy with the
// same response; 404 answers only malformed or never-issued ids.
internal static partial class CodeEndpoints
{
    public const string ResendPath = "/auth/codes";
    public const string VerifyPath = "/auth/codes/{codeId}/verify";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ResendPath, ResendAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
        endpoints.MapPost(VerifyPath, VerifyAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
    }

    internal static bool IsCode(string? code) => code is not null && SixDigits().IsMatch(code);

    private static async Task<IResult> ResendAsync(HttpRequest request, AuthCalls calls, AuthHashing hashing)
    {
        var (body, failure) = await AuthProblems.ReadAsync<ResendRequest>(request);
        if (failure is not null)
        {
            return failure;
        }

        if (EmailNormalizer.Normalize(body!.Email) is not { } email)
        {
            return AuthProblems.BadRequest(AuthProblems.EmailInvalid);
        }

        if (body.Purpose is not ("signup" or "reset"))
        {
            return AuthProblems.BadRequest(AuthProblems.RequestInvalid);
        }

        if (body.Purpose == "reset") { return await ResetEndpoints.IssueAsync(email, calls, hashing); }
        var emailHash = hashing.EmailSubject(email);
        var entry = await calls.EmailIndex(emailHash, index => index.Get());
        var pending = entry.Status == EmailIndexStatus.Pending;
        var delivery = pending
            ? new CodeDelivery(email, entry.PendingLanguage ?? "en", entry.PendingDisplayName ?? email, MailTemplateId.SignupCode, null)
            : null;
        var issue = await calls.Code(emailHash, CodePurpose.Signup, code => code.Issue(
            new CodeIssueRequest(emailHash, IsDecoy: !pending, EnforceCooldown: true, delivery, SignUpEndpoints.EmailMethod, "pwd")));
        return AuthProblems.Issued(issue);
    }

    private static async Task<IResult> VerifyAsync(string codeId, HttpContext http, AuthCalls calls, AccountProvisioner provisioner, SessionStarter sessions, AuthAudit audit, LoginService login, ExternalTicketIssuer tickets)
    {
        var (body, failure) = await AuthProblems.ReadAsync<VerifyRequest>(http.Request);
        if (failure is not null)
        {
            return failure;
        }

        if (!IsCode(body!.Code))
        {
            return AuthProblems.BadRequest(AuthProblems.RequestInvalid);
        }

        // Login challenges verify through their own route; this one serves sign-up (and reset in 12-06).
        if (IAuthCode.KeyForCodeId(codeId, out var purpose) is null || purpose is not (CodePurpose.Signup or CodePurpose.Reset))
        {
            return AuthProblems.NotFound();
        }

        var verdict = await calls.CodeById(codeId, (code, _) => code.Verify(codeId, body.Code!))!;
        if (verdict.Outcome != CodeOutcome.Success)
        {
            return AuthProblems.Verdict(verdict);
        }

        if (purpose == CodePurpose.Reset)
        {
            var ticket = await calls.CodeById(codeId, (code, _) => code.IssueTicket(codeId))!;
            return ticket is null ? AuthProblems.NotFound() : Results.Json(new { ticket = ticket.Ticket, expiresAt = ticket.ExpiresAt });
        }

        var member = await provisioner.CompleteSignUpAsync(verdict.SubjectHash!);
        if (member is null)
        {
            return AuthProblems.NotFound();
        }

        // The code may predate an external provider verifying this address and the owner enabling TOTP. Every
        // session-producing email proof must honor the current credential state, including this stale sign-up proof.
        var entry = await calls.EmailIndex(verdict.SubjectHash!, index => index.Get());
        var flags = await calls.Credential(member.PrincipalId, credential => credential.GetFlags());
        var amr = flags.HasPassword ? "pwd otp_email" : "email otp_email";
        if (await login.ChallengeExternalAsync(entry.Email!, amr) is { } challenge)
        {
            return LoginEndpoints.Challenge(challenge, ["authenticator", "recovery"]);
        }
        await sessions.StartAsync(http, member, amr, http.RequestAborted);
        await audit.RecordAsync(member.PrincipalId, AuthAuditKind.SignUp, "success", http, "email");
        return ExternalTicketIssuer.SignedIn(member, await tickets.RedirectFor(http.Request, entry.Email!, entry.PendingDisplayName, amr));
    }

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex SixDigits();

    internal sealed record ResendRequest(string? Email, string? Purpose);

    internal sealed record VerifyRequest(string? Code);
}
