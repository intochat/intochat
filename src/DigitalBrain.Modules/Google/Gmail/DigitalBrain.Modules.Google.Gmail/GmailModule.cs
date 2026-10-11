using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Google.Gmail;

public sealed class GmailModule() : Module<GmailModuleOptions>(new ModuleId("gmail")), IHttpModule
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Google.Gmail.IGmail).Assembly;

    public const string HttpClientName = "DigitalBrain.Gmail";
    public const string WatchPath = "/google/gmail/watch";

    public static IntegrationSchema[] Integrations { get; } = [
        IntegrationSchema.For("google-oauth", "Google OAuth").RequiresSecret("ClientId").RequiresSecret("ClientSecret"),
        IntegrationSchema.For("google-pubsub", "Google Pub/Sub push").RequiresSetting("Audience").RequiresSetting("ServiceAccountEmail"),
    ];

    public static ConnectorDefinition Connector { get; } = new("gmail", "Gmail", ["google-oauth"], ["google-oauth"])
    {
        OptionalIntegrations = ["google-pubsub"],
        Neurons = [typeof(IGmail)],
        Signals = [typeof(MailReceived)],
        OAuthMethods = [new("google-oauth", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", [GmailScopes.Read], "google-oauth")
            { AuthorizationParameters = { ["access_type"] = "offline", ["prompt"] = "consent" } }],
    };

    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(silo.Configuration.GetModuleOptions<GmailModuleOptions>("gmail"));
        services.TryAddSingleton<IGmailPushAuthenticator, GmailPushAuthenticator>();
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        // The Pub/Sub push subscription URL names the brain; each Gmail connection of that brain
        // decides by its own mailbox whether the delivery is its.
        endpoints.MapPost(WatchPath + "/{brainId}", async (string brainId, GmailPubSubPush envelope, HttpRequest request, IGmailPushAuthenticator authenticator, IGrainFactory grains, CancellationToken ct) =>
        {
            if (!BrainScope.IsValidId(brainId)) { return Results.NotFound(); }
            if (!await authenticator.AuthenticateAsync(request, ct)) { return Results.Unauthorized(); }
            if (!GmailPubSub.TryUnwrap(envelope, out var push)) { return Results.BadRequest(); }

            foreach (var key in await grains.GetGrain<IConnectionIndex>(brainId).List())
            {
                if (ConnectionKeys.Parse(key).ConnectorId == Connector.Id) { await grains.GetGrain<IGmailInbound>(key).AcceptWatchPush(push); }
            }
            return Results.Accepted();
        }).AllowAnonymous();
    }
}
