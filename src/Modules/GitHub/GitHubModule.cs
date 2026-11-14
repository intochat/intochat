using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Modules.Microsoft.GitHub.Webhook;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class GitHubModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "github";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs:
        [
            DigitalBrain.Modules.Need.HttpClient("github", requestsPerMinute: 120),
            DigitalBrain.Modules.Need.Webhook("/github/webhook", "github.repository"),
        ],
        Settings: [DigitalBrain.Modules.Setting.Of<GitHubModuleOptions>(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: [DigitalBrain.Modules.Secret.Named("private-key-pem"), DigitalBrain.Modules.Secret.Named("client-secret"), DigitalBrain.Modules.Secret.Named("webhook-secret")])
    {
        Auth = new DigitalBrain.Modules.ModuleAuth("github", []),
        Actions =
        [
            new("github.branch.push", "push a GitHub branch", null),
            new("github.pull-request.create", "create a GitHub pull request", null),
            new("github.review.reply", "reply to a GitHub review comment", null),
            new("github.pull-request.merge", "merge a GitHub pull request", null),
        ],
    };

    public static System.Reflection.Assembly? Contracts => typeof(IRepository).Assembly;

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

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<GitHubAppRegistration>();
        services.AddSingleton<GitHubInstallationTokens>();
        services.AddSingleton<IGitHubInstallationVerifier>(services => services.GetRequiredService<GitHubInstallationTokens>());
        services.AddSingleton<IGitHubRepositorySource, GitHubRepositorySource>();
        services.AddSingleton<IGitHubUserAccess, GitHubUserAccess>();
        services.AddSingleton<GitHubWebhookIngress>();
        services.AddSingleton<IHttpSurface, GitHubWebhookSurface>();
        services.TryAddSingleton<IWorkTree, WorkTreeService>();
        services.AddHttpClient(GitHubUserAccess.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
}
