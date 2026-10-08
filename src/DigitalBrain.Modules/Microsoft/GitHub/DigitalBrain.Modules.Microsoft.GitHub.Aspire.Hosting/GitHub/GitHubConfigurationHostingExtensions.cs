using DigitalBrain.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Microsoft.GitHub;

public static class GitHubConfigurationHostingExtensions
{
    public static DigitalBrainModuleBuilder<DigitalBrain.Microsoft.GitHub.GitHubModuleHosting> WithConfiguredGitHubApp(
        this DigitalBrainModuleBuilder<DigitalBrain.Microsoft.GitHub.GitHubModuleHosting> module, IConfiguration configuration)
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
