using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

internal static class ResetEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/password-reset", RequestAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
        endpoints.MapPost("/auth/password-reset/complete", CompleteAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();
    }

    internal static async Task<IResult> IssueAsync(string email, AuthCalls calls, AuthHashing hashing)
    {
        var hash = hashing.EmailSubject(email);
        var entry = await calls.EmailIndex(hash, index => index.Get());
        var verified = entry.Status == EmailIndexStatus.Verified;
        var issue = await calls.Code(hash, CodePurpose.Reset, code => code.Issue(new CodeIssueRequest(hash, !verified, true,
            verified ? new CodeDelivery(email, entry.PendingLanguage ?? "en", entry.PendingDisplayName ?? email, MailTemplateId.ResetCode, null) : null,
            ["email"], "pwd")));
        return AuthProblems.Issued(issue);
    }

    private static async Task<IResult> RequestAsync(HttpRequest request, AuthCalls calls, AuthHashing hashing)
    {
        var (body, failure) = await AuthProblems.ReadAsync<ResetRequest>(request);
        if (failure is not null) { return failure; }
        return EmailNormalizer.Normalize(body!.Email) is { } email
            ? await IssueAsync(email, calls, hashing) : AuthProblems.BadRequest(AuthProblems.EmailInvalid);
    }

    private static async Task<IResult> CompleteAsync(HttpContext http, AuthCalls calls, AuthHashing hashing, AuthModuleOptions options,
        AccountProvisioner provisioner, SessionStarter sessions, AuthAudit audit, ExternalTicketIssuer tickets)
    {
        var (body, failure) = await AuthProblems.ReadAsync<CompleteRequest>(http.Request);
        if (failure is not null) { return failure; }
        if (EmailNormalizer.Normalize(body!.Email) is not { } email || string.IsNullOrEmpty(body.Ticket)) { return InvalidTicket(); }
        if (!PasswordHasher.IsValidLength(body.NewPassword)) { return AuthProblems.BadRequest(AuthProblems.PasswordInvalid); }
        var hash = hashing.EmailSubject(email);
        var entry = await calls.EmailIndex(hash, index => index.Get());
        if (entry.Status != EmailIndexStatus.Verified || entry.PrincipalId is null
            || !await calls.Code(hash, CodePurpose.Reset, code => code.RedeemTicket(body.Ticket))) { return InvalidTicket(); }
        await calls.Credential(entry.PrincipalId, credential => credential.RotatePassword(new PasswordHasher(options.PasswordIterations).Hash(body.NewPassword!)));
        await calls.Sessions(entry.PrincipalId, table => table.RevokeAll());
        var flags = await calls.Credential(entry.PrincipalId, credential => credential.GetFlags());
        string? redirect = null;
        if (!flags.HasTotp)
        {
            var member = await provisioner.RegisterAsync(entry.PrincipalId, entry.PendingDisplayName ?? email);
            await sessions.StartAsync(http, member, "pwd otp_email", http.RequestAborted);
            redirect = await tickets.RedirectFor(http.Request, email, entry.PendingDisplayName, "pwd otp_email");
        }
        await audit.RecordAsync(entry.PrincipalId, AuthAuditKind.PasswordReset, "success", http);
        return redirect is null ? Results.Json(new { signedIn = !flags.HasTotp }) : Results.Json(new { signedIn = true, redirect });
    }

    private static IResult InvalidTicket() => AuthProblems.BadRequest(AuthErrors.PasswordResetTicketInvalid);
    private sealed record ResetRequest(string? Email);
    private sealed record CompleteRequest(string? Email, string? Ticket, string? NewPassword);
}

