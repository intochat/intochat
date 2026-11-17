using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

internal static class TotpEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/totp", BeginAsync).AddEndpointFilter<AuthIpThrottleFilter>();
        endpoints.MapPost("/auth/totp/confirm", ConfirmAsync).AddEndpointFilter<AuthIpThrottleFilter>();
        endpoints.MapPost("/auth/totp/disable", DisableAsync).AddEndpointFilter<AuthIpThrottleFilter>();
        endpoints.MapPost("/auth/totp/recovery-codes", RegenerateAsync).AddEndpointFilter<AuthIpThrottleFilter>();
    }

    private static async Task<IResult> BeginAsync(HttpContext http, AuthCalls calls, AuthSecrets secrets, AuthModuleOptions options, TimeProvider clock)
    {
        var caller = AuthCaller.Require(http);
        if (!RecentAuth.IsRecent(http.User, clock, options.RecentAuthWindow)) { return Reauthenticate(); }
        var enrolment = TotpEnrolment.Create(caller.PrincipalId);
        var pending = await secrets.SealTotp(caller.PrincipalId, enrolment.Secret);
        if (!await calls.Credential(caller.PrincipalId, credential => credential.BeginTotp(pending))) { return AuthProblems.BadRequest("AUTHENTICATOR_ALREADY_ENABLED"); }
        return Results.Json(new { secret = enrolment.Base32Secret, otpauthUri = enrolment.OtpAuthUri });
    }

    private static async Task<IResult> ConfirmAsync(HttpContext http, AuthCalls calls, AuthAudit audit)
    {
        var caller = AuthCaller.Require(http);
        var (body, failure) = await AuthProblems.ReadAsync<FactorRequest>(http.Request);
        if (failure is not null) { return failure; }
        var recovery = await calls.Credential(caller.PrincipalId, credential => credential.ConfirmTotp(body!.Code ?? ""));
        if (recovery is null) { return Invalid(); }
        await audit.RecordAsync(caller.PrincipalId, AuthAuditKind.TotpEnabled, "success", http);
        return Results.Json(new { recoveryCodes = recovery });
    }

    private static async Task<IResult> DisableAsync(HttpContext http, AuthCalls calls, SessionStarter starter, AuthAudit audit, AuthModuleOptions options, TimeProvider clock)
    {
        var caller = AuthCaller.Require(http);
        if (!RecentAuth.IsRecent(http.User, clock, options.RecentAuthWindow)) { return Reauthenticate(); }
        var (body, failure) = await AuthProblems.ReadAsync<FactorRequest>(http.Request);
        if (failure is not null) { return failure; }
        var verified = body!.RecoveryCode is { } recovery
            ? await calls.Credential(caller.PrincipalId, credential => credential.UseRecovery(recovery))
            : await calls.Credential(caller.PrincipalId, credential => credential.VerifyTotp(body.Code ?? ""));
        if (!verified) { return Invalid(); }
        await calls.Credential(caller.PrincipalId, credential => credential.DisableTotp());
        if (starter.CurrentSidHash(http) is { } sidHash) { await calls.Sessions(caller.PrincipalId, sessions => sessions.RevokeOthers(sidHash)); }
        await audit.RecordAsync(caller.PrincipalId, AuthAuditKind.TotpDisabled, "success", http);
        return Results.Ok();
    }

    private static async Task<IResult> RegenerateAsync(HttpContext http, AuthCalls calls, AuthAudit audit, AuthModuleOptions options, TimeProvider clock)
    {
        var caller = AuthCaller.Require(http);
        if (!RecentAuth.IsRecent(http.User, clock, options.RecentAuthWindow)) { return Reauthenticate(); }
        var (body, failure) = await AuthProblems.ReadAsync<FactorRequest>(http.Request);
        if (failure is not null) { return failure; }
        if (!await calls.Credential(caller.PrincipalId, credential => credential.VerifyTotp(body!.Code ?? ""))) { return Invalid(); }
        var recovery = await calls.Credential(caller.PrincipalId, credential => credential.RegenerateRecovery());
        await audit.RecordAsync(caller.PrincipalId, AuthAuditKind.RecoveryCodesRegenerated, "success", http);
        return Results.Json(new { recoveryCodes = recovery });
    }

    private static IResult Reauthenticate() => AuthProblems.Code(401, AuthErrors.ReauthenticationRequired);
    private static IResult Invalid() => AuthProblems.BadRequest(AuthErrors.AuthenticatorCodeInvalid);
    private sealed record FactorRequest(string? Code, string? RecoveryCode);
}

