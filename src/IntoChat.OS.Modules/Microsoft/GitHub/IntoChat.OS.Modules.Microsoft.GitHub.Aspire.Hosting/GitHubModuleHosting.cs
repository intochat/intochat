using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Microsoft.GitHub;

// Repository connections are per-brain neurons a user authorizes; the deployment provisions only
// the shared GitHub App, through WithGitHubApp or WithConfiguredGitHubApp.
public sealed class GitHubModuleHosting : ModuleHosting<IntoChat.OS.Microsoft.GitHub.GitHubModule>
{
    public override void Configure(IntoChatOSBuilder brain) => ArgumentNullException.ThrowIfNull(brain);
}
