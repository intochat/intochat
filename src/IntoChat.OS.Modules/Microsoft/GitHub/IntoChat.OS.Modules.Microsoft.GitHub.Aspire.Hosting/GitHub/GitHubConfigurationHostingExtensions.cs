using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Microsoft.GitHub;

public static class GitHubConfigurationHostingExtensions
{
    public static IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> WithConfiguredGitHubApp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> module, IConfiguration configuration)
    {
        var options = configuration.GetModuleOptions<GitHubModuleOptions>("github");
        if (options.AppId != 0)
        {
            Uri? webhook = options.PublicWebhookUrl.Length == 0 ? null : new Uri(options.PublicWebhookUrl, UriKind.Absolute);
            module.WithGitHubApp(options.AppId, options.ClientId, options.PublicOrigin, webhook);
        }
        return module;
    }
}
