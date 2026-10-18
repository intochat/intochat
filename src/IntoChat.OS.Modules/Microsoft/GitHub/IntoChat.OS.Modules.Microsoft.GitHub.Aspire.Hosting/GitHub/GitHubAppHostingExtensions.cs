using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Platform.Integrations;

namespace IntoChat.OS.Microsoft.GitHub;

public static class GitHubAppHostingExtensions
{
    public static IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> WithGitHubApp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> module, Action<GitHubAppHostingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GitHubAppHostingOptions();
        configure(options);
        return module.WithGitHubApp(options.AppId, options.ClientId, options.PublicOrigin, options.PublicWebhookUrl);
    }

    public static IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> WithGitHubApp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> module,
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
        var projection = module.IntoChatOSBuilder.GetOrAddState(_ => new GitHubAppProjection(module.IntoChatOSBuilder, appId, clientId, publicOrigin, publicWebhookUrl), out var added);
        if (!added)
        {
            throw new InvalidOperationException("Configure the shared GitHub App once per IntoChat.OS.");
        }
        module.AddProjection(projection);
        return module;
    }

    private sealed class GitHubAppProjection(IntoChatOSBuilder brain, long appId, string clientId = "", Uri? publicOrigin = null, Uri? publicWebhookUrl = null)
        : IntoChatOSModuleProjection
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
            builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntegrationRoot, "AppId"), appId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntegrationRoot, "PrivateKeyPem"), _privateKey);
            if (publicOrigin is not null)
            {
                // The Platform's OAuth login reads the client from the github Integration and calls
                // back on the shared connections origin.
                builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ConnectionsPublicOriginKey), publicOrigin.AbsoluteUri)
                    .WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntegrationRoot, "ClientId"), clientId)
                    .WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntegrationRoot, "ClientSecret"), _clientSecret!);
            }
            if (publicWebhookUrl is not null)
            {
                builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(root, "PublicWebhookUrl"), publicWebhookUrl.AbsoluteUri)
                    .WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntegrationRoot, "WebhookSecret"), _webhookSecret!);
            }
        }
    }
}
