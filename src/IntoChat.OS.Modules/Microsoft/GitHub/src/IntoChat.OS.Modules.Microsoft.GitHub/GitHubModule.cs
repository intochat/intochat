using Microsoft.Extensions.Configuration;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using IntoChat.OS.Modules.Microsoft.GitHub.Webhook;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

public sealed class GitHubModule() : Module<GitHubModuleOptions>(new ModuleId("github"))
{
    internal const string ManifestName = "github";

    // R4-04: what the module needs from its host (GitHubNeurons). The repository connection is still a grain (the connection
    // base, 06-06d); its registrations stay in Configure until the Framework leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest => GitHubNeurons.Manifest;

    // The installation index is a plain neuron (06-06d) in the system brain.
    public override Type? Neurons => typeof(GitHubNeurons);

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Microsoft.GitHub.IRepository).Assembly;

    // ClientId sits beside ClientSecret so the Platform's OAuth flow reads both from one release.
    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("github", "GitHub")
        .RequiresSecret("PrivateKeyPem")
        .RequiresSetting("AppId")
        .OffersSetting("ClientId")
        .OffersSecret("ClientSecret")
        .OffersSecret("WebhookSecret");

    // GitHub App permissions belong to the installation, so the user authorization asks for no scopes.
    public static ConnectorDefinition Connector => new("github", "GitHub", ["oauth"], ["github"])
    {
        Neurons = [typeof(IRepository)],
        Signals = [typeof(Signals.PullRequestChanged)],
        OAuthMethods = [new("oauth", "https://github.com/login/oauth/authorize", "https://github.com/login/oauth/access_token", [], "github", "ClientId", "ClientSecret")],
    };

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<GitHubAppRegistration>();
        services.AddSingleton<GitHubInstallationTokens>();
        services.AddSingleton<IGitHubInstallationVerifier>(services => services.GetRequiredService<GitHubInstallationTokens>());
        services.AddSingleton<IGitHubRepositorySource, GitHubRepositorySource>();
        services.AddSingleton<IGitHubUserAccess, GitHubUserAccess>();
        services.AddSingleton<GitHubWebhookIngress>();
        services.AddSingleton<IHttpSurface, GitHubWebhookSurface>();
        services.AddHttpClient(GitHubUserAccess.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
}

// The DigitalBrain module of GitHub (06-06d): its assembly's [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module(GitHubModule.ManifestName)]
public sealed class GitHubNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<GitHubModuleOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(GitHubModule.ManifestName))],
        Secrets: []);
}
