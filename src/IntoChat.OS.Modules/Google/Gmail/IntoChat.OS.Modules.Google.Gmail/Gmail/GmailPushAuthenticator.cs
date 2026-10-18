using System.IdentityModel.Tokens.Jwt;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
namespace IntoChat.OS.Google.Gmail;

internal interface IGmailPushAuthenticator { Task<bool> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken); }
internal sealed class GmailPushAuthenticator(IModuleIntegrations<GmailModule> integrations) : IGmailPushAuthenticator
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration = new("https://accounts.google.com/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());
    public async Task<bool> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) { return false; }
        var snapshot = await integrations.Get("google-pubsub").ReadAsync(cancellationToken);
        if (snapshot.Status != IntegrationStatus.Ready || !snapshot.Settings.TryGetValue("Audience", out var audience)
            || !snapshot.Settings.TryGetValue("ServiceAccountEmail", out var email)) { return false; }
        try
        {
            var metadata = await configuration.GetConfigurationAsync(cancellationToken);
            return ValidatePushToken(authorization[7..], metadata, audience, email);
        }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException or InvalidOperationException or HttpRequestException) { return false; }
    }
    internal static bool ValidatePushToken(string token, OpenIdConnectConfiguration metadata, string audience, string email)
    {
        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(1),
        }, out _);
        return principal.FindFirst("email")?.Value == email && principal.FindFirst("email_verified")?.Value is "true" or "True";
    }
}
