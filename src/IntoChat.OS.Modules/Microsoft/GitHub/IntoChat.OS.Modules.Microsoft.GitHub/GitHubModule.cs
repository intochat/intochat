using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Microsoft.GitHub.Webhook;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace IntoChat.OS.Microsoft.GitHub;

public sealed class GitHubModule() : Module<GitHubModuleOptions>(new ModuleId("github"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Microsoft.GitHub.IRepository).Assembly;

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

    public override void Configure(ISiloBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<GitHubAppRegistration>();
        builder.Services.AddSingleton<GitHubInstallationTokens>();
        builder.Services.AddSingleton<IGitHubInstallationVerifier>(services => services.GetRequiredService<GitHubInstallationTokens>());
        builder.Services.AddSingleton<IGitHubRepositorySource, GitHubRepositorySource>();
        builder.Services.AddSingleton<IGitHubUserAccess, GitHubUserAccess>();
        builder.Services.AddSingleton<GitHubWebhookIngress>();
        builder.Services.AddSingleton<IHttpSurface, GitHubWebhookSurface>();
        builder.Services.AddHttpClient(GitHubUserAccess.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
}
