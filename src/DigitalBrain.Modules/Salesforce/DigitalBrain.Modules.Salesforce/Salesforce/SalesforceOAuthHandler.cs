using System.Text.Encodings.Web;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Salesforce;

internal sealed class SalesforceOAuthHandler(
    IOptionsMonitor<SalesforceOAuthHandlerOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : OAuthHandler<SalesforceOAuthHandlerOptions>(new SalesforceRequestOptions(options), logger, encoder)
{
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await Context.RequestServices.GetRequiredService<SalesforceOAuthConfiguration>().ConfigureAsync(Options, Context.RequestAborted);
        await base.HandleChallengeAsync(properties);
    }

    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        await Context.RequestServices.GetRequiredService<SalesforceOAuthConfiguration>().ConfigureAsync(Options, Context.RequestAborted);
        return await base.ExchangeCodeAsync(context);
    }

    protected override bool ValidateCorrelationId(AuthenticationProperties properties)
        => base.ValidateCorrelationId(properties)
            && BrowserLoginCorrelation.Claim(Context, properties, Context.RequestServices.GetRequiredService<SalesforceLogins>());
}

internal sealed class SalesforceOAuthHandlerOptions : OAuthOptions
{
    internal SalesforceOAuthHandlerOptions Copy() => (SalesforceOAuthHandlerOptions)MemberwiseClone();
}

internal sealed class SalesforceRequestOptions(IOptionsMonitor<SalesforceOAuthHandlerOptions> source) : IOptionsMonitor<SalesforceOAuthHandlerOptions>
{
    public SalesforceOAuthHandlerOptions CurrentValue => source.CurrentValue.Copy();
    public SalesforceOAuthHandlerOptions Get(string? name) => source.Get(name).Copy();
    public IDisposable? OnChange(Action<SalesforceOAuthHandlerOptions, string?> listener) => source.OnChange(listener);
}
