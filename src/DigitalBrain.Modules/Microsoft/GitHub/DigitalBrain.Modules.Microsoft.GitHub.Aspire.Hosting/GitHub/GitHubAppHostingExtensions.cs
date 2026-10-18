using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Microsoft.GitHub;

public static class GitHubAppHostingExtensions
{
    public static DigitalBrainModuleBuilder<DigitalBrain.Microsoft.GitHub.GitHubModuleHosting> WithGitHubApp(
        this DigitalBrainModuleBuilder<DigitalBrain.Microsoft.GitHub.GitHubModuleHosting> module, Action<GitHubAppHostingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GitHubAppHostingOptions();
        configure(options);
        return module.WithGitHubApp(options.AppId, options.ClientId, options.PublicOrigin, options.PublicWebhookUrl);
    }

    public static DigitalBrainModuleBuilder<DigitalBrain.Microsoft.GitHub.GitHubModuleHosting> WithGitHubApp(
        this DigitalBrainModuleBuilder<DigitalBrain.Microsoft.GitHub.GitHubModuleHosting> module,
        long appId, string clientId = "", Uri? publicOrigin = null, Uri? publicWebhookUrl = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentOutOfRangeException.ThrowIfLessThan(appId, 1);
        if (string.IsNullOrWhiteSpace(clientId) != (publicOrigin is null))
        {
            throw new ArgumentException("GitHub OAuth requires both ClientId and PublicOrigin.");
        }
        if (publicOrigin is not null && (!publicOrigin.IsAbsoluteUri || publicOrigin.UserInfo.Length != 0 || publicOrigin.AbsolutePath != "/"
            || publicOrigin.Query.Length != 0 || publicOrigin.Fragment.Length != 0
            || publicOrigin.Scheme != "https" && !(publicOrigin.Scheme == "http" && publicOrigin.IsLoopback)))
        {
            throw new ArgumentException("GitHub OAuth requires a public HTTPS origin or a local development origin.", nameof(publicOrigin));
        }
        if (publicWebhookUrl is not null && (!publicWebhookUrl.IsAbsoluteUri || publicWebhookUrl.Scheme != "https" || publicWebhookUrl.UserInfo.Length != 0
            || publicWebhookUrl.Query.Length != 0 || publicWebhookUrl.Fragment.Length != 0
            || publicWebhookUrl.AbsolutePath != "/integrations/github/webhook"))
        {
            throw new ArgumentException("GitHub requires a public HTTPS /integrations/github/webhook endpoint.", nameof(publicWebhookUrl));
        }
        var projection = module.DigitalBrainBuilder.GetOrAddState(_ => new GitHubAppProjection(module.DigitalBrainBuilder, appId, clientId, publicOrigin, publicWebhookUrl), out var added);
        if (!added)
        {
            throw new InvalidOperationException("Configure the shared GitHub App once per DigitalBrain.");
        }
        module.AddProjection(projection);
        return module;
    }

    private sealed class GitHubAppProjection(DigitalBrainBuilder brain, long appId, string clientId = "", Uri? publicOrigin = null, Uri? publicWebhookUrl = null)
        : DigitalBrainModuleProjection
    {
        private IResourceBuilder<ParameterResource>? _privateKey;
        private IResourceBuilder<ParameterResource>? _webhookSecret;
        private IResourceBuilder<ParameterResource>? _clientSecret;

        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            _privateKey ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("github-app-private-key"), secret: true)
                .WithDescription("PEM private key for the GitHub App; projected only to the host.");
            if (publicWebhookUrl is not null)
            {
                _webhookSecret ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("github-app-webhook-secret"), secret: true)
                .WithDescription("GitHub App HMAC webhook secret, at least 16 characters.");
            }
            if (publicOrigin is not null)
            {
                _clientSecret ??= brain.ApplicationBuilder.AddParameter(brain.ResourceName("github-app-client-secret"), secret: true)
                .WithDescription("GitHub App OAuth client secret. Register <public origin>/connections/oauth/github/callback; user tokens only prove repository access once.");
            }
            var root = GitHubModuleOptions.SectionName;
            const string IntegrationRoot = IntegrationSchema.SectionName + ":github";
            builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationRoot, "AppId"), appId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationRoot, "PrivateKeyPem"), _privateKey);
            if (publicOrigin is not null)
            {
                // The Platform's OAuth login reads the client from the github Integration and calls
                // back on the shared connections origin.
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ConnectionsPublicOriginKey), publicOrigin.AbsoluteUri)
                    .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationRoot, "ClientId"), clientId)
                    .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationRoot, "ClientSecret"), _clientSecret!);
            }
            if (publicWebhookUrl is not null)
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(root, "PublicWebhookUrl"), publicWebhookUrl.AbsoluteUri)
                    .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IntegrationRoot, "WebhookSecret"), _webhookSecret!);
            }
        }
    }
}
