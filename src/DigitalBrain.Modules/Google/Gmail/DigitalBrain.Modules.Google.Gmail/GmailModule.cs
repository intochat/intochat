using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Google.Gmail;

public sealed class GmailModule() : Module<GmailModuleOptions>(new ModuleId("gmail")), IHttpModule
{
    public static IntegrationSchema[] Integrations { get; } = [
        IntegrationSchema.For("google-oauth", "Google OAuth").RequiresSecret("ClientId").RequiresSecret("ClientSecret"),
        IntegrationSchema.For("google-pubsub", "Google Pub/Sub push").RequiresSetting("Audience").RequiresSetting("ServiceAccountEmail"),
    ];

    public static ConnectorDefinition Connector { get; } = new("gmail", "Gmail", ["google-oauth"], ["google-oauth"]) { OptionalIntegrations = ["google-pubsub"], Neurons = [typeof(IGmail)], Signals = [typeof(GmailConnected), typeof(MailReceived)] };

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(silo.Configuration.GetModuleOptions<GmailModuleOptions>("gmail"));
        services.TryAddSingleton<GmailRegistration>();
        services.TryAddSingleton<GmailCallbackRequests>();
        services.TryAddSingleton<IGmailTokenExchange, GmailTokenExchange>();
        services.TryAddSingleton<IGmailPushAuthenticator, GmailPushAuthenticator>();
        services.AddSingleton<IHttpSurface, GmailOAuthPrivacySurface>();
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/brains/{brainId}/connections/{connectionId}/gmail/login", async (string brainId, string connectionId, GmailCallbackRequests requests, GmailRegistration registration, DigitalBrain.Framework.Platform.Identity.IIdentity identity) =>
        {
            var principal = identity.CurrentPrincipal;
            if (principal is null) { return Results.Unauthorized(); }
            if (!await identity.CanAccessAsync(principal.PrincipalId, principal.AccountId, brainId)) { return Results.Forbid(); }
            var credentials = await registration.ReleaseAsync();
            var state = requests.Begin(principal.PrincipalId, principal.AccountId, brainId, connectionId);
            var redirect = new Uri(credentials.PublicOrigin, "google/gmail/oauth/callback").AbsoluteUri;
            var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&client_id=" + Uri.EscapeDataString(credentials.ClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&scope=" + Uri.EscapeDataString(GmailScopes.Read) + "&state=" + state;
            return Results.Ok(new { url });
        });
        endpoints.MapPost("/google/gmail/watch", async (GmailPubSubPush envelope, HttpRequest request, IGmailPushAuthenticator authenticator, IGrainFactory grains, CancellationToken ct) =>
        {
            if (!await authenticator.AuthenticateAsync(request, ct)) { return Results.Unauthorized(); }
            if (!GmailPubSub.TryUnwrap(envelope, out var push))
            {
                return Results.BadRequest();
            }

            foreach (var key in await grains.GetGrain<IGmailMailboxIndex>(push.EmailAddress.ToLowerInvariant()).Read())
            { await grains.GetGrain<IGmailInbound>(key).AcceptWatchPush(push); }
            return Results.Accepted();
        }).AllowAnonymous();
        endpoints.MapGet("/google/gmail/oauth/callback", async (string? code, string? state, GmailCallbackRequests requests, GmailRegistration registration, IGrainFactory grains, DigitalBrain.Framework.Platform.Identity.IIdentity identity) =>
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.BadRequest();
            }

            if (registration.Explain(await registration.ReadAsync()) is { } unavailable)
            {
                return Results.Json(unavailable, statusCode: StatusCodes.Status409Conflict);
            }

            var principal = identity.CurrentPrincipal;
            if (principal is null) { return Results.Unauthorized(); }
            if (!requests.TryClaim(state, principal.PrincipalId, principal.AccountId, out var target)) { return Results.BadRequest(); }
            if (!await identity.CanAccessAsync(principal.PrincipalId, target.AccountId, target.BrainId)) { return Results.Forbid(); }
            await grains.GetGrain<IGmailInbound>(ConnectionKeys.For(BrainScope.Create(target.AccountId, target.BrainId).Id, "gmail", target.ConnectionId)).AcceptAuthorizationCode(code, target.BrainId);
            return Results.Ok();
        });
    }
}
