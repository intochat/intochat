using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Sdk;
using IntoChat.OS.Sdk.Http.Compute;
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
[Setting("TopicName", Integration = "google-pubsub")]
[ModulePriceList(typeof(GooglePriceList))]
[ExternalOperation(GoogleOperations.Profile, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.ListMessages, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.Metadata, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.Message, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.CreateDraft, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.History, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.Watch, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.Stop, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.DriveAbout, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.DriveList, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.DriveMetadata, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.DriveMedia, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.DriveExport, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.CodeExchange, "google-oauth", "request")]
[ExternalOperation(GoogleOperations.Refresh, "google-oauth", "request")]
public sealed partial class GmailModule : Module<GmailModuleOptions>, IHttpModule
{
    public const string HttpClientName = "IntoChat.OS.Gmail";
    public const string ClockKey = "google-gmail";
    public const string WatchPath = "/google/gmail/watch";

    public static ConnectorDefinition[] Connectors => [Gmail, Drive];

    public static ConnectorDefinition Gmail { get; } = new("gmail", "Gmail", ["google-oauth", "google-oauth-compose"], ["google-oauth"])
    {
        OptionalIntegrations = ["google-pubsub"],
        Neurons = [typeof(IGmail)],
        Signals = [typeof(MailReceived)],
        OAuthMethods = [GoogleOAuth("google-oauth", [GmailScopes.Read]),
            GoogleOAuth("google-oauth-compose", [GmailScopes.Read, "https://www.googleapis.com/auth/gmail.compose"])],
    };

    // Google Drive as read-only storage, signed in through the same Google OAuth integration as Gmail.
    // A separate connector keeps existing Gmail grants unchanged: adding a scope to them would force re-consent.
    public static ConnectorDefinition Drive { get; } = new(DriveScopes.Connector, "Google Drive", ["google-oauth"], ["google-oauth"])
    {
        Neurons = [typeof(IStorage)],
        Capabilities = [IStorage.Capability],
        OAuthMethods = [GoogleOAuth("google-oauth", [DriveScopes.Read])],
    };

    private static OAuthMethod GoogleOAuth(string id, string[] scopes) => new(id,
        "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", scopes, "google-oauth")
    {
        AuthorizationParameters = { ["access_type"] = "offline", ["prompt"] = "consent" },
        ExternalOperations = new(GoogleOperations.CodeExchange, GoogleOperations.Refresh),
    };

    partial void OnConfigure(IModuleServices services)
    {
        var collection = services.Services;
        collection.TryAddSingleton(TimeProvider.System);
        collection.TryAddKeyedSingleton(ClockKey, TimeProvider.System);
        collection.TryAddSingleton(services.Options<GmailModuleOptions>());
        collection.TryAddSingleton<IGmailPushAuthenticator, GmailPushAuthenticator>();
        collection.TryAddSingleton<IGmailApi>(provider => new GmailApi(provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<GmailModuleOptions>(), provider.GetRequiredKeyedService<TimeProvider>(ClockKey),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GmailApi>>()));
        collection.TryAddEnumerable(ServiceDescriptor.Singleton<IActivationHandler<GmailNeuron>, GmailActivation>());
        collection.AddMeteredHttpClient(HttpClientName).ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }

    public void Configure(IEndpointRouteBuilder endpoints) => GmailWebhook.Map(endpoints);
}
