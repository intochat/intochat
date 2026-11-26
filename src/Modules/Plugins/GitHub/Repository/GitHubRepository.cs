using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

// The coordinates a connection neuron holds; Key is the connection key that owns them.
[GenerateSerializer, Alias("github.repository-coordinates")]
internal sealed record GitHubRepository(
    [property: Id(0)] string Key,
    [property: Id(1)] long AppId,
    [property: Id(2)] long InstallationId,
    [property: Id(3)] long RepositoryId,
    [property: Id(4)] string Owner,
    [property: Id(5)] string Name,
    [property: Id(6)] string AuthKind = "app")
{
    [JsonIgnore]
    public Uri ApiHost => new("https://api.github.com/");

    [JsonIgnore]
    public string RepositoryPath => $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}";

    [JsonIgnore]
    public bool IsPersonalAccessToken => string.Equals(AuthKind, "pat", StringComparison.OrdinalIgnoreCase);
}

internal static class GitHubCredentialScope
{
    private static readonly AsyncLocal<Frame?> Current = new();

    private sealed record Frame(string ConnectionKey, string PersonalAccessToken, Frame? Outer);

    public static IDisposable UsePersonalAccessToken(string connectionKey, string personalAccessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);
        var outer = Current.Value;
        Current.Value = new Frame(connectionKey, personalAccessToken, outer);
        return new ScopeExit(outer);
    }

    public static string RequirePersonalAccessToken(string connectionKey)
    {
        for (var frame = Current.Value; frame is not null; frame = frame.Outer)
        {
            if (string.Equals(frame.ConnectionKey, connectionKey, StringComparison.Ordinal))
            {
                return frame.PersonalAccessToken;
            }
        }

        throw new GitHubAccessDeniedException("Personal access token is unavailable outside the owning GitHub connection turn.");
    }

    private sealed class ScopeExit(Frame? outer) : IDisposable
    {
        public void Dispose() => Current.Value = outer;
    }
}
