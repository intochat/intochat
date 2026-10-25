using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

// Repository connections are per-brain neurons a user authorizes; the deployment provisions only
// the shared GitHub App, through WithGitHubApp or WithConfiguredGitHubApp.
public sealed class GitHubModuleHosting : ModuleHosting<IntoChat.OS.Modules.Microsoft.GitHub.GitHubModule>
{
    public override void Configure(IntoChatOSBuilder brain) => ArgumentNullException.ThrowIfNull(brain);
}
