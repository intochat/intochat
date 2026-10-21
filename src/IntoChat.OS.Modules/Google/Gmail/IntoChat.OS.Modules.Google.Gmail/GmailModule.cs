using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Google.Gmail;

[Module("gmail")]
[ModuleContracts(typeof(IGmail))]
[Integration("google-oauth", "Google OAuth")]
[Integration("google-pubsub", "Google Pub/Sub push")]
[Secret("ClientId", Integration = "google-oauth")]
[Secret("ClientSecret", Integration = "google-oauth")]
[Setting("Audience", Integration = "google-pubsub")]
[Setting("ServiceAccountEmail", Integration = "google-pubsub")]
public sealed partial class GmailModule : Module<GmailModuleOptions>, IHttpModule
{
    public const string HttpClientName = "IntoChat.OS.Gmail";
    public const string WatchPath = "/google/gmail/watch";

    public static ConnectorDefinition[] Connectors => [Gmail, Drive];

    public static ConnectorDefinition Gmail { get; } = new("gmail", "Gmail", ["google-oauth"], ["google-oauth"])
    {
        OptionalIntegrations = ["google-pubsub"],
        Neurons = [typeof(IGmail)],
        Signals = [typeof(MailReceived)],
        OAuthMethods = [new("google-oauth", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", [GmailScopes.Read], "google-oauth")
            { AuthorizationParameters = { ["access_type"] = "offline", ["prompt"] = "consent" } }],
    };

    // Google Drive as read-only storage, signed in through the same Google OAuth integration as Gmail.
    // A separate connector keeps existing Gmail grants unchanged: adding a scope to them would force re-consent.
    public static ConnectorDefinition Drive { get; } = new(DriveScopes.Connector, "Google Drive", ["google-oauth"], ["google-oauth"])
    {
        Neurons = [typeof(IStorage)],
        Capabilities = [IStorage.Capability],
        OAuthMethods = [new("google-oauth", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", [DriveScopes.Read], "google-oauth")
            { AuthorizationParameters = { ["access_type"] = "offline", ["prompt"] = "consent" } }],
    };

    partial void OnConfigure(IModuleServices services)
    {
        var collection = services.Services;
        collection.TryAddSingleton(TimeProvider.System);
        collection.TryAddSingleton(services.Options<GmailModuleOptions>());
        collection.TryAddSingleton<IGmailPushAuthenticator, GmailPushAuthenticator>();
        collection.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
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
                if (ConnectionKeys.Parse(key).ConnectorId == Gmail.Id) { await grains.GetGrain<IGmailInbound>(key).AcceptWatchPush(push); }
            }
            return Results.Accepted();
        }).AllowAnonymous();
    }
}
