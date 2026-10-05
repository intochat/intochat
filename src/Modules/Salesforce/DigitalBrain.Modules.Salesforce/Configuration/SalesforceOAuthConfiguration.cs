using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Salesforce;

internal sealed class SalesforceOAuthConfiguration(IModuleIntegrations<SalesforceModule> integrations, IOptions<SalesforceModuleOptions> options)
{
    internal Uri? PublicOrigin => TryOrigin(out var origin) ? origin : null;
    internal static Uri AuthorizationEndpoint => new("https://login.salesforce.com/services/oauth2/authorize");
    internal static Uri TokenEndpoint => new("https://login.salesforce.com/services/oauth2/token");

    internal async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => PublicOrigin is not null && (await integrations.Get("salesforce").ReadAsync(cancellationToken)).Status == IntegrationStatus.Ready;

    private async Task<(string Key, string Secret)> CredentialsAsync(CancellationToken cancellationToken)
    {
        if (!await IsConfiguredAsync(cancellationToken))
        {
            throw new SalesforceUnavailableException("Salesforce setup is incomplete. Provision the Salesforce integration ConsumerKey and ConsumerSecret and configure the module PublicOrigin.");
        }
        var handle = integrations.Get("salesforce");
        var material = await handle.ReleaseAsync(cancellationToken);
        var key = material.Values["ConsumerKey"];
        var secret = material.Values["ConsumerSecret"];
        if (!IsValidCredential(key) || !IsValidCredential(secret)) { throw new SalesforceUnavailableException("Salesforce integration credentials are invalid."); }
        return (key, secret);
    }

    internal static void Configure(OAuthOptions options)
    {
        options.ClientId = "not-configured";
        options.ClientSecret = "not-configured";
        options.AuthorizationEndpoint = AuthorizationEndpoint.AbsoluteUri;
        options.TokenEndpoint = TokenEndpoint.AbsoluteUri;
    }

    internal async Task ConfigureAsync(OAuthOptions options, CancellationToken cancellationToken)
    {
        var credentials = await CredentialsAsync(cancellationToken);
        options.ClientId = credentials.Key;
        options.ClientSecret = credentials.Secret;
    }

    internal async Task<HttpRequestMessage> RefreshRequestAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var credentials = await CredentialsAsync(cancellationToken);
        return new(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = credentials.Key,
                ["client_secret"] = credentials.Secret,
                ["refresh_token"] = refreshToken,
            }),
        };
    }

    private static bool IsValidCredential(string value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private bool TryOrigin(out Uri? origin)
    {
        origin = options.Value.PublicOrigin;
        if (origin is { IsAbsoluteUri: true }
            && (origin.Scheme == Uri.UriSchemeHttps || (origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
            && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.AbsolutePath == "/") { return true; }
        origin = null;
        return false;
    }
}
