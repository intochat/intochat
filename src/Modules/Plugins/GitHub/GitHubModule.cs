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
    // Non-secret public client identifier fallback for RFC 8628 Device Flow when no host ClientId override is configured.
    internal const string DefaultDeviceClientId = "intochat-os-github-device";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs:
        [
            DigitalBrain.Modules.Need.HttpClient("github", requestsPerMinute: 120),
            DigitalBrain.Modules.Need.Webhook("/integrations/github/webhook", "github.repository"),
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
            new("github.review.submit", "submit a GitHub pull request review", null),
            new("github.pull-request.merge", "merge a GitHub pull request", null),
            new("github.issue.create", "create a GitHub issue", null),
            new("github.issue.comment", "comment on a GitHub issue", null),
            new("github.issue.label", "add labels to a GitHub issue", null),
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

    // GitHub App permissions belong to the installation, so the user authorization asks for no scopes; Device Login (RFC 8628) and Fine-Grained PAT are offered alongside OAuth.
    public static ConnectorDefinition Connector => new("github", "GitHub", ["oauth", "device", "pat"], [])
    {
        OptionalIntegrations = ["github"],
        Neurons = [typeof(IRepository)],
        Signals = [typeof(Signals.PullRequestChanged), typeof(Signals.IssueChanged)],
        OAuthMethods = [new("oauth", "https://github.com/login/oauth/authorize", "https://github.com/login/oauth/access_token", [], "github", "ClientId", "ClientSecret")],
        DeviceMethods = [new("device", "https://github.com/login/device/code", "https://github.com/login/oauth/access_token", ["repo"], DefaultClientId: DefaultDeviceClientId, IntegrationId: "github", ClientIdField: "ClientId", DisplayName: "GitHub Device Login")],
    };

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<GitHubAppRegistration>();
        services.AddSingleton<GitHubInstallationTokens>();
        services.AddSingleton<IGitHubInstallationVerifier>(services => services.GetRequiredService<GitHubInstallationTokens>());
        services.TryAddSingleton<IGitHubRepositorySource, GitHubRepositorySource>();
        services.AddSingleton<IGitHubUserAccess, GitHubUserAccess>();
        services.AddSingleton<GitHubWebhookIngress>();
        services.AddSingleton<IHttpSurface, GitHubWebhookSurface>();
        services.TryAddSingleton<IWorkTree, WorkTreeService>();
        services.AddHttpClient(GitHubUserAccess.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
}
