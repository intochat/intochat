using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.GitHub;

// Repository declarations and public GitHub App metadata contain no credentials.
public sealed class GitHubModuleOptions : IModuleOptions
{
    public static readonly string SectionName = DigitalBrainConfiguration.ModuleOptionsSection(new ModuleId("github"));
    public static readonly string RepositoriesSectionName = SectionName + ":Repositories";

    public long AppId { get; set; }
    public string ClientId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string PublicWebhookUrl { get; set; } = "";

    public Dictionary<string, GitHubRepositoryDeclaration> Repositories { get; set; } = new(StringComparer.Ordinal);

    public GitHubModuleOptions WithGitHubRepositories(IReadOnlyDictionary<string, GitHubRepositoryDeclaration> repositories)
    {
        Repositories = new(repositories, StringComparer.Ordinal);
        return this;
    }

    public void Validate()
    {
        foreach (var id in Repositories.Keys)
        {
            if (id.Length is 0 or > 80 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            { throw new ArgumentException("Repository binding IDs must contain letters, numbers or hyphens."); }
        }
    }
}

public sealed class GitHubRepositoryDeclaration
{
    public long AppId { get; set; }
    public long InstallationId { get; set; }
    public long RepositoryId { get; set; }
    public string RepoOwner { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string? EndpointId { get; set; }
    public Uri? ApiHost { get; set; }
    public Uri? McpEndpoint { get; set; }
}
