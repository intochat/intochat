using IntoChat.OS.Modules.Google.Auth;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Framework.Modules.Metering;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Google.Gmail;

[Module("gmail")]
[ModuleContracts(typeof(IGmail))]
[UsesIntegration(GoogleAuth.Integration)]
[Integration("google-pubsub", "Google Pub/Sub push")]
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
public sealed partial class GmailModule : Module<GmailModuleOptions>, IHttpModule
{
    // R4-04: what the module needs from its host. It has no neurons; its registrations stay in Configure until the Framework
    // leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest => GmailNeurons.Manifest;

    // The connection neuron is plain (06-08): GmailNeurons' assembly [Neuron] classes run on the runtime's host grains.
    public override Type? Neurons => typeof(GmailNeurons);

    public const string ClockKey = "google-gmail";
    public const string WatchPath = "/google/gmail/watch";

    public static ConnectorDefinition[] Connectors => [Gmail];

    public static ConnectorDefinition Gmail { get; } = new("gmail", "Gmail", ["google-oauth", "google-oauth-compose"], ["google-oauth"])
    {
        OptionalIntegrations = ["google-pubsub"],
        Neurons = [typeof(IGmail)],
        Signals = [typeof(MailReceived)],
        OAuthMethods = [GoogleAuth.OAuth("google-oauth", [GmailScopes.Read]),
            GoogleAuth.OAuth("google-oauth-compose", [GmailScopes.Read, "https://www.googleapis.com/auth/gmail.compose"])],
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
        GoogleAuth.Require(collection, "gmail");
    }

    public void Configure(IEndpointRouteBuilder endpoints) => GmailWebhook.Map(endpoints);
}

// The DigitalBrain module of this connector (06-08): its assembly's [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module("gmail")]
public sealed class GmailNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<GmailModuleOptions>()],
        Secrets: []);
}
