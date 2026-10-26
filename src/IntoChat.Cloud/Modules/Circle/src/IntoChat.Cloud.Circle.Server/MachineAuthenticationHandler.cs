using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Circle.Server;

// The IntoChatMachine scheme (D-09; T-10-02, T-10-18): Authorization Bearer plus X-Machine-Proof for the bound key.
// It never reads a cookie; circle module routes authenticate with this scheme only, through MachineGate.
internal sealed class MachineAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IntoChat.OS.Runtime.IStillGrains grains, INonceCache nonces)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (MachineAuthentication.Bearer(Request) is not { } token) { return AuthenticateResult.NoResult(); }
        if (!MachineTokens.TryCredentialId(token, out var credentialId)) { return AuthenticateResult.Fail("Machine authentication failed."); }
        var credential = grains.Get<IMachineCredential>(credentialId);
        // Revoked credentials authenticate so their machine can read status; the policies refuse them everywhere else.
        if (await credential.Authenticate(token, includeRevoked: true) is not { } machine
            || !await MachineAuthentication.ProofHolds(Context, machine.PublicKeySpki, token, credential, nonces))
        {
            return AuthenticateResult.Fail("Machine authentication failed.");
        }
        Claim[] claims =
        [
            new(MachineClaims.Credential, machine.CredentialId),
            new(MachineClaims.Circle, machine.CircleId),
            new(MachineClaims.CircleName, machine.CircleName),
            new(MachineClaims.Machine, machine.MachineId),
            new(MachineClaims.Role, machine.Role),
            new(MachineClaims.Pending, machine.PendingAccount ? "true" : "false"),
            new(MachineClaims.Revoked, machine.Revoked ? "true" : "false"),
            .. machine.AccountId is { } account ? [new Claim(MachineClaims.Account, account)] : Array.Empty<Claim>(),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, MachineAuthentication.Scheme, MachineClaims.Machine, MachineClaims.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, MachineAuthentication.Scheme));
    }
}

public static class MachineClaims
{
    public const string Credential = "circle-credential";
    public const string Circle = "circle";
    public const string CircleName = "circle-name";
    public const string Machine = "machine";
    public const string Role = "circle-role";
    public const string Account = "account";
    public const string Pending = "pending";
    public const string Revoked = "revoked";
}

// Authorization policies for circle module routes. CircleMachine: any credential that is not revoked.
// CircleApprovedMachine: also not waiting for an account.
public static class CirclePolicies
{
    public const string Machine = "CircleMachine";
    public const string ApprovedMachine = "CircleApprovedMachine";

    // Circle module routes sit behind the machine scheme only; owner cookies never reach them.
    public static RouteHandlerBuilder RequireCircleMachine(this RouteHandlerBuilder route)
        => route.AddEndpointFilter(MachineGate.For(Machine));

    public static RouteHandlerBuilder RequireApprovedMachine(this RouteHandlerBuilder route)
        => route.AddEndpointFilter(MachineGate.For(ApprovedMachine));

    internal static void Add(AuthorizationOptions options)
    {
        options.AddPolicy(Machine, policy => policy.AddAuthenticationSchemes(MachineAuthentication.Scheme)
            .RequireAuthenticatedUser().RequireClaim(MachineClaims.Revoked, "false"));
        options.AddPolicy(ApprovedMachine, policy => policy.AddAuthenticationSchemes(MachineAuthentication.Scheme)
            .RequireAuthenticatedUser().RequireClaim(MachineClaims.Revoked, "false").RequireClaim(MachineClaims.Pending, "false"));
    }
}

// Authenticates with IntoChatMachine alone (whatever the host's default scheme found is replaced) and evaluates the
// policy in the filter, so the routes need no authorization middleware in the host pipeline.
internal static class MachineGate
{
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> For(string? policy)
        => async (context, next) =>
        {
            var http = context.HttpContext;
            var result = await http.AuthenticateAsync(MachineAuthentication.Scheme);
            if (!result.Succeeded) { return CircleResponses.Unauthorized; }
            http.User = result.Principal;
            if (policy is not null)
            {
                var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
                if (!(await authorization.AuthorizeAsync(result.Principal, policy)).Succeeded)
                {
                    return result.Principal.HasClaim(MachineClaims.Revoked, "true") ? CircleResponses.Unauthorized : CircleResponses.Forbidden;
                }
            }
            return await next(context);
        };
}

public sealed record CircleStatusResponse(string CircleId, string CircleName, string Role, bool PendingAccount, bool Revoked, string MachineId);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[System.Text.Json.Serialization.JsonSerializable(typeof(CircleStatusResponse))]
internal sealed partial class CircleServerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

public sealed partial class CircleServerModule
{
    partial void ConfigureAuthentication(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MachineAuthenticationHandler>(MachineAuthentication.Scheme, null);
        services.AddAuthorization(CirclePolicies.Add);
    }

    // Leave revokes this machine's credential; status answers for revoked credentials too, so a machine learns why.
    partial void MapMembership(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder routes)
    {
        routes.MapPost(CircleRoutes.Leave, async (HttpContext http, IntoChat.OS.Runtime.IStillGrains grains) =>
        {
            await grains.Get<IMachineCredential>(http.User.FindFirstValue(MachineClaims.Credential)!).Revoke();
            return Results.NoContent();
        }).AddEndpointFilter(CircleResponses.Filter).RequireCircleMachine();

        routes.MapGet(CircleRoutes.Status, (HttpContext http) =>
        {
            var user = http.User;
            return Results.Json(new CircleStatusResponse(user.FindFirstValue(MachineClaims.Circle)!, user.FindFirstValue(MachineClaims.CircleName)!,
                user.FindFirstValue(MachineClaims.Role)!, user.HasClaim(MachineClaims.Pending, "true"), user.HasClaim(MachineClaims.Revoked, "true"),
                user.FindFirstValue(MachineClaims.Machine)!), CircleServerJsonContext.Default.CircleStatusResponse);
        }).AddEndpointFilter(CircleResponses.Filter).AddEndpointFilter(MachineGate.For(null));
    }
}
