using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

internal static class EmailChangeEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/email-change", BeginAsync);
        endpoints.MapPost("/auth/email-change/confirm", ConfirmAsync);
    }

    private static async Task<IResult> BeginAsync(HttpContext http, AuthCalls calls, AuthHashing hashing, AuthModuleOptions options, TimeProvider clock)
    {
        var caller = AuthCaller.Require(http);
        if (!RecentAuth.IsRecent(http.User, clock, options.RecentAuthWindow)) { return Reauthenticate(); }
        var (body, failure) = await AuthProblems.ReadAsync<BeginRequest>(http.Request);
        if (failure is not null) { return failure; }
        if (EmailNormalizer.Normalize(body!.NewEmail) is not { } email) { return AuthProblems.BadRequest(AuthProblems.EmailInvalid); }
        var hash = hashing.EmailSubject(email);
        var previousHash = await calls.Credential(caller.PrincipalId, credential => credential.GetEmailHash());
        if (previousHash is null) { return Reauthenticate(); }
        var reserved = await calls.EmailIndex(hash, index => index.ReserveForChange(caller.PrincipalId, caller.AccountId!, caller.BrainId!, email, previousHash));
        var issue = await calls.Code(hash, CodePurpose.ChangeEmail, code => code.Issue(new CodeIssueRequest(hash, !reserved, true,
            reserved ? new CodeDelivery(email, "en", email, MailTemplateId.ChangeEmailCode, null) : null, ["email"], "pwd")));
        if (reserved && issue.CodeId is { } codeId)
        {
            await calls.EmailIndex(hash, index => index.BindChangeCode(caller.PrincipalId, codeId));
        }
        return AuthProblems.Issued(issue);
    }

    private static async Task<IResult> ConfirmAsync(HttpContext http, AuthCalls calls, SessionStarter starter, AuthAudit audit, AuthModuleOptions options, TimeProvider clock)
    {
        var caller = AuthCaller.Require(http);
        if (!RecentAuth.IsRecent(http.User, clock, options.RecentAuthWindow)) { return Reauthenticate(); }
        var (body, failure) = await AuthProblems.ReadAsync<ConfirmRequest>(http.Request);
        if (failure is not null) { return failure; }
        if (body!.CodeId is null || !CodeEndpoints.IsCode(body.Code)
            || IAuthCode.KeyForCodeId(body.CodeId, out var purpose) is null || purpose != CodePurpose.ChangeEmail) { return AuthProblems.NotFound(); }
        var challenge = await calls.CodeById(body.CodeId, (code, _) => code.GetChallenge(body.CodeId))!;
        // A successful retry needs no second proof; the destination remembers the exact consumed code id.
        var subject = challenge?.SubjectHash;
        subject ??= await calls.CodeById(body.CodeId, (code, _) => code.GetChangeSubject())!;
        if (subject is null)
        {
            // The subject hash is not recoverable from the opaque code id; a completed operation is checked through the caller's current index.
            subject = await calls.Credential(caller.PrincipalId, credential => credential.GetEmailHash());
            if (subject is null) { return AuthProblems.NotFound(); }
            var completed = await calls.EmailIndex(subject, index => index.Get());
            if (completed.PrincipalId != caller.PrincipalId || completed.ChangeCodeId != body.CodeId) { return AuthProblems.NotFound(); }
        }
        var entry = await calls.EmailIndex(subject, index => index.Get());
        if (entry.PrincipalId != caller.PrincipalId || entry.PreviousEmailHash is null || entry.ChangeCodeId != body.CodeId) { return AuthProblems.NotFound(); }
        if (entry.Status == EmailIndexStatus.ChangePending && !entry.ChangeProved)
        {
            var verified = await calls.CodeById(body.CodeId, (code, _) => code.GetVerifiedChangeSubject(body.CodeId))!;
            if (verified != subject)
            {
                var verdict = await calls.CodeById(body.CodeId, (code, _) => code.Verify(body.CodeId, body.Code!))!;
                if (verdict.Outcome != CodeOutcome.Success)
                {
                    if (verdict.Reissued?.CodeId is { } replacement
                        && !await calls.EmailIndex(subject, index => index.RebindChangeCode(caller.PrincipalId, body.CodeId, replacement)))
                    {
                        return AuthProblems.NotFound();
                    }
                    return AuthProblems.Verdict(verdict);
                }
            }
            if (!await calls.EmailIndex(subject, index => index.ProveChange(caller.PrincipalId, body.CodeId))) { return AuthProblems.NotFound(); }
        }
        // A principal can reserve more than one destination, but only one can move its current email. The
        // credential turn chooses the winner atomically; the consumed proof supports retry after interruption.
        if (!await calls.Credential(caller.PrincipalId, credential => credential.TryMoveEmailHash(entry.PreviousEmailHash, subject)))
        {
            await calls.EmailIndex(subject, index => index.CancelChange(caller.PrincipalId, body.CodeId));
            return AuthProblems.BadRequest("EMAIL_CHANGE_SUPERSEDED");
        }
        if (!await calls.EmailIndex(subject, index => index.CommitChange(caller.PrincipalId, body.CodeId)))
        {
            await calls.Credential(caller.PrincipalId, credential => credential.TryMoveEmailHash(subject, entry.PreviousEmailHash));
            return AuthProblems.NotFound();
        }
        await calls.EmailIndex(entry.PreviousEmailHash, index => index.Release(caller.PrincipalId));
        if (starter.CurrentSidHash(http) is { } sidHash) { await calls.Sessions(caller.PrincipalId, table => table.RevokeOthers(sidHash)); }
        await audit.RecordAsync(caller.PrincipalId, AuthAuditKind.EmailChanged, "success", http);
        return Results.Ok();
    }

    private static IResult Reauthenticate() => AuthProblems.Code(401, AuthErrors.ReauthenticationRequired);
    private sealed record BeginRequest(string? NewEmail);
    private sealed record ConfirmRequest(string? CodeId, string? Code);
}
