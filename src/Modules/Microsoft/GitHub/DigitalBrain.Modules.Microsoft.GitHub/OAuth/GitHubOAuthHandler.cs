using System.Text.Encodings.Web;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Microsoft.GitHub;

internal sealed class GitHubOAuthHandler(IOptionsMonitor<GitHubOAuthHandlerOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : OAuthHandler<GitHubOAuthHandlerOptions>(new GitHubRequestOptions(options), logger, encoder)
{
    // This scheme completes provider connections; it does not authenticate ordinary
    // host requests, even when ASP.NET selects the only scheme as its default.
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await Context.RequestServices.GetRequiredService<GitHubOAuthConfiguration>().ConfigureAsync(Options, Context.RequestAborted);
        await base.HandleChallengeAsync(properties);
    }

    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        await Context.RequestServices.GetRequiredService<GitHubOAuthConfiguration>().ConfigureAsync(Options, Context.RequestAborted);
        return await base.ExchangeCodeAsync(context);
    }

    protected override bool ValidateCorrelationId(AuthenticationProperties properties)
        => base.ValidateCorrelationId(properties)
            && BrowserLoginCorrelation.Claim(Context, properties, Context.RequestServices.GetRequiredService<GitHubLogins>());
}

internal sealed class GitHubOAuthHandlerOptions : OAuthOptions
{
    internal GitHubOAuthHandlerOptions Copy() => (GitHubOAuthHandlerOptions)MemberwiseClone();
}
internal sealed class GitHubRequestOptions(IOptionsMonitor<GitHubOAuthHandlerOptions> source) : IOptionsMonitor<GitHubOAuthHandlerOptions>
{
    public GitHubOAuthHandlerOptions CurrentValue => source.CurrentValue.Copy();
    public GitHubOAuthHandlerOptions Get(string? name) => source.Get(name).Copy();
    public IDisposable? OnChange(Action<GitHubOAuthHandlerOptions, string?> listener) => source.OnChange(listener);
}
