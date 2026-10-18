using IntoChat.OS.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Microsoft.GitHub;

public static class GitHubConfigurationHostingExtensions
{
    public static IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> WithConfiguredGitHubApp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Microsoft.GitHub.GitHubModuleHosting> module, IConfiguration configuration)
    {
        var options = configuration.GetSection(GitHubModuleOptions.SectionName).Get<GitHubAppHostingOptions>();
        if (options is not null)
        {
            module.WithGitHubApp(options.AppId, options.ClientId,
                options.PublicOrigin, options.PublicWebhookUrl);
        }
        return module;
    }
}
