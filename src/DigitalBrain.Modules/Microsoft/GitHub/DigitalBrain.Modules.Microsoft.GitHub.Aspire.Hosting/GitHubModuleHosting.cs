using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Microsoft.GitHub;

// Repository connections are per-brain neurons a user authorizes; the deployment provisions only
// the shared GitHub App, through WithGitHubApp or WithConfiguredGitHubApp.
public sealed class GitHubModuleHosting : ModuleHosting<DigitalBrain.Microsoft.GitHub.GitHubModule>
{
    public override void Configure(DigitalBrainBuilder brain) => ArgumentNullException.ThrowIfNull(brain);
}
