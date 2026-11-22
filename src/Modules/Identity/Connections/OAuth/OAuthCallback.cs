using System.Diagnostics;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Identity.Connections.OAuth;

// Unified /integrations/{provider} edge for OAuth redirects (/integrations/oauth/callback, /integrations/{provider}/oauth/callback,
// /integrations/{provider}/callback, and legacy /connections/oauth/{provider}/callback).
public sealed class OAuthCallback : IHttpSurface
{
    public const string CanonicalCallbackPath = "/integrations/oauth/callback";
    private const string IntegrationsPrefix = "/integrations/";
    private const string LegacyPrefix = "/connections/oauth/";
    private const string OAuthCallbackSuffix = "/oauth/callback";
    private const string CallbackSuffix = "/callback";

    // ASP.NET Core's per-request Information log, which prints the full query string.
    internal const string RequestLogCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public static bool TryMatchCallbackPath(string? path, out string provider)
    {
        provider = "";
        if (string.IsNullOrWhiteSpace(path)) { return false; }

        if (string.Equals(path, CanonicalCallbackPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (path.StartsWith(IntegrationsPrefix, StringComparison.Ordinal))
        {
            if (path.EndsWith(OAuthCallbackSuffix, StringComparison.Ordinal)
                && path.Length > IntegrationsPrefix.Length + OAuthCallbackSuffix.Length)
            {
                var segment = path[IntegrationsPrefix.Length..^OAuthCallbackSuffix.Length];
                if (!segment.Contains('/', StringComparison.Ordinal))
                {
                    provider = Uri.UnescapeDataString(segment);
                    return !string.IsNullOrWhiteSpace(provider);
                }
            }

            if (path.EndsWith(CallbackSuffix, StringComparison.Ordinal)
                && path.Length > IntegrationsPrefix.Length + CallbackSuffix.Length)
            {
                var segment = path[IntegrationsPrefix.Length..^CallbackSuffix.Length];
                if (!segment.Contains('/', StringComparison.Ordinal))
                {
                    provider = Uri.UnescapeDataString(segment);
                    return !string.IsNullOrWhiteSpace(provider);
                }
            }
        }

        if (path.StartsWith(LegacyPrefix, StringComparison.Ordinal)
            && path.EndsWith(CallbackSuffix, StringComparison.Ordinal)
            && path.Length > LegacyPrefix.Length + CallbackSuffix.Length)
        {
            var segment = path[LegacyPrefix.Length..^CallbackSuffix.Length];
            if (!segment.Contains('/', StringComparison.Ordinal))
            {
                provider = Uri.UnescapeDataString(segment);
                return !string.IsNullOrWhiteSpace(provider);
            }
        }

        return false;
    }

    public void Map(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!TryMatchCallbackPath(path, out var connector))
            {
                await next(context);
                return;
            }

            Redact(context);
            LoginPage.PrivateHeaders(context);
            var requests = context.RequestServices.GetRequiredService<OAuthLoginRequests>();
            var code = context.Request.Query["code"].ToString();
            if (!HttpMethods.IsGet(context.Request.Method) || string.IsNullOrWhiteSpace(code) || !requests.TryClaim(context.Request.Query["state"], out var login))
            {
                await LoginPage.WriteAsync(context, "This login link expired or was already used", "Return to IntoChat.OS and connect again.", 400);
                return;
            }
            var (_, keyConnector, _) = ConnectionKeys.Parse(login.ConnectionKey);
            if (string.IsNullOrEmpty(connector))
            {
                connector = keyConnector;
            }
            else if (keyConnector != connector)
            {
                await LoginPage.WriteAsync(context, "Invalid callback", "Return to IntoChat.OS and connect again.", 400);
                return;
            }
            try
            {
                var exchange = context.RequestServices.GetRequiredService<OAuthTokenExchange>();
                var flow = context.RequestServices.GetRequiredService<OAuthLoginFlow>();
                // The person who began the login, if still a member of that brain; a signed-in browser must be that person.
                var identity = context.RequestServices.GetRequiredService<IntoChat.OS.Modules.Identity.Contracts.Accounts.IIdentityClient>();
                var edge = context.RequestServices.GetRequiredService<DigitalBrain.Access.EdgeAuthority>();
                if (HttpCaller.Current(context)?.For is { } present && present.Principal != login.OwnerPrincipal)
                { throw new UnauthorizedAccessException("The login was begun by another person."); }
                var member = await identity.Membership(login.OwnerBrain, login.OwnerPrincipal).WaitAsync(context.RequestAborted)
                    ?? throw new UnauthorizedAccessException("The login's person is no longer a member of its brain.");
                using (edge.Enter(edge.RelayedPerson(member.PrincipalId, member.AccountId, new DigitalBrain.BrainId(login.OwnerBrain))))
                using (TraceContext.Enter(TraceContext.Begin(member.AccountId,
                    BrainScope.Create(member.AccountId, login.OwnerBrain).Id, TraceContext.OriginOf(login.OwnerBrain, login.ConnectionKey))))
                {
                    var neurons = context.RequestServices.GetRequiredService<AuthorityNeurons>();
                    var grant = await exchange.ExchangeCodeAsync(login.ConnectionKey, login.AuthMethodId, code, login.CodeVerifier, flow.CallbackUri(connector, context.Request).AbsoluteUri, login.Target, context.RequestAborted);
                    await neurons.ConnectionInbound(login.ConnectionKey).AcceptGrant(grant);
                }
                await LoginPage.WriteAsync(context, $"{connector} connected", "You can close this tab. IntoChat.OS is continuing your request.", 200);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Provider replies and tokens never reach the browser or the log: only the failure's type is kept.
                context.RequestServices.GetRequiredService<ILogger<OAuthCallback>>()
                    .LogWarning("OAuth callback for {Connector} failed ({ErrorType}).", connector, error.GetType().Name);
                await LoginPage.WriteAsync(context, $"{connector} connection needs attention", "Return to IntoChat.OS and connect again.", 400);
            }
            finally { Redact(context); }
        });
    }

    // Callback query strings carry authorization codes; traces keep only the path.
    private static void Redact(HttpContext context)
    {
        var activity = Activity.Current;
        activity?.SetTag("url.query", null);
        activity?.SetTag("http.target", context.Request.Path.Value);
        activity?.SetTag("url.full", $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}");
    }
}
