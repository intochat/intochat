using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Microsoft.GitHub;

internal sealed class GitHubOAuthConfiguration(IOptions<GitHubModuleOptions> options, GitHubAppRegistration registration)
{
    internal string ClientId => options.Value.ClientId;
    internal string Slug => options.Value.Slug;
    internal Uri? PublicOrigin => Uri.TryCreate(options.Value.PublicOrigin, UriKind.Absolute, out var origin)
        && (origin.Scheme == "https" || origin.Scheme == "http" && origin.IsLoopback)
        && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.UserInfo.Length == 0
        ? origin : null;

    internal async Task ConfigureAsync(OAuthOptions options, CancellationToken cancellationToken)
    {
        var secret = await registration.ReadOptionalSecretAsync("ClientSecret", cancellationToken);
        if (PublicOrigin is null || string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(secret))
        { throw new GitHubUnavailableException("GitHub OAuth setup is incomplete. Configure public module metadata and provision the GitHub integration ClientSecret."); }
        options.ClientId = ClientId;
        options.ClientSecret = secret;
    }
    internal async Task<long> ReadAppIdAsync(CancellationToken cancellationToken)
        => await registration.ReadAppIdAsync(cancellationToken) ?? throw new GitHubUnavailableException("GitHub App integration is unavailable.");
}
