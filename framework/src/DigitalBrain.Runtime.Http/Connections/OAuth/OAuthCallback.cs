using System.Diagnostics;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

// Unified /integrations/{connector} edge for OAuth redirects (/integrations/oauth/callback, /integrations/{connector}/oauth/callback,
// /integrations/{connector}/callback, and legacy /connections/oauth/{connector}/callback).
public sealed partial class OAuthCallback : IHttpSurface
{
    public const string CanonicalCallbackPath = "/integrations/oauth/callback";
    private const string IntegrationsPrefix = "/integrations/";
    private const string LegacyPrefix = "/connections/oauth/";
    private const string OAuthCallbackSuffix = "/oauth/callback";
    private const string CallbackSuffix = "/callback";

    // ASP.NET Core's per-request Information log, which prints the full query string.
    public const string RequestLogCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public static bool TryMatchCallbackPath(string? path, out string connector)
    {
        connector = "";
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
                    connector = Uri.UnescapeDataString(segment);
                    return !string.IsNullOrWhiteSpace(connector);
                }
            }

            if (path.EndsWith(CallbackSuffix, StringComparison.Ordinal)
                && path.Length > IntegrationsPrefix.Length + CallbackSuffix.Length)
            {
                var segment = path[IntegrationsPrefix.Length..^CallbackSuffix.Length];
                if (!segment.Contains('/', StringComparison.Ordinal))
                {
                    connector = Uri.UnescapeDataString(segment);
                    return !string.IsNullOrWhiteSpace(connector);
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
                connector = Uri.UnescapeDataString(segment);
                return !string.IsNullOrWhiteSpace(connector);
            }
        }

        return false;
    }

    public void Map(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!TryMatchCallbackPath(path, out var connector))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            Redact(context);
            LoginPage.PrivateHeaders(context);
            var productName = context.RequestServices.GetService<IOptions<OAuthConnectionsOptions>>()?.Value.ResolvedProductName
                ?? OAuthConnectionsOptions.DefaultProductName;
            var requests = context.RequestServices.GetRequiredService<OAuthLoginRequests>();
            var code = context.Request.Query["code"].ToString();
            if (!HttpMethods.IsGet(context.Request.Method) || string.IsNullOrWhiteSpace(code) || !requests.TryClaim(context.Request.Query["state"], out var login))
            {
                await LoginPage.WriteAsync(context, "This login link expired or was already used", $"Return to {productName} and connect again.", 400).ConfigureAwait(false);
                return;
            }
            var (_, keyConnector, _) = ConnectionKeys.Parse(login.ConnectionKey);
            if (string.IsNullOrEmpty(connector))
            {
                connector = keyConnector;
            }
            else if (keyConnector != connector)
            {
                await LoginPage.WriteAsync(context, "Invalid callback", $"Return to {productName} and connect again.", 400).ConfigureAwait(false);
                return;
            }
            try
            {
                var exchange = context.RequestServices.GetRequiredService<OAuthTokenExchange>();
                var flow = context.RequestServices.GetRequiredService<OAuthLoginFlow>();
                // The person who began the login, if still a member of that brain; a signed-in browser must be that person.
                var directory = context.RequestServices.GetRequiredService<IIdentityDirectoryPort>();
                var edge = context.RequestServices.GetRequiredService<EdgeAuthority>();
                if (HttpCaller.Current(context)?.For is { } present && present.Principal != login.OwnerPrincipal)
                { throw new UnauthorizedAccessException("The login was begun by another person."); }
                var member = await directory.Membership(login.OwnerBrain, login.OwnerPrincipal, context.RequestAborted).ConfigureAwait(false)
                    ?? throw new UnauthorizedAccessException("The login's person is no longer a member of its brain.");
                using (edge.Enter(edge.RelayedPerson(member.PrincipalId, member.AccountId, new BrainId(login.OwnerBrain))))
                using (TraceContext.Enter(TraceContext.Begin(member.AccountId,
                    BrainScope.Create(member.AccountId, login.OwnerBrain).Id, TraceContext.OriginOf(login.OwnerBrain, login.ConnectionKey))))
                {
                    var proxies = context.RequestServices.GetRequiredService<INeuronProxies>();
                    var grant = await exchange.ExchangeCodeAsync(login.ConnectionKey, login.AuthMethodId, code, login.CodeVerifier, flow.CallbackUri(connector, context.Request).AbsoluteUri, login.Target, context.RequestAborted).ConfigureAwait(false);
                    await proxies.Get<IConnectionInbound>(ConnectionKeys.IdFor(login.ConnectionKey)).AcceptGrant(grant).ConfigureAwait(false);
                }
                await LoginPage.WriteAsync(context, $"{connector} connected", $"You can close this tab. {productName} is continuing your request.", 200).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // External replies and tokens never reach the browser or the log: only the failure's type is kept.
                LogCallbackFailed(
                    context.RequestServices.GetRequiredService<ILogger<OAuthCallback>>(),
                    connector,
                    error.GetType().Name);
                await LoginPage.WriteAsync(context, $"{connector} connection needs attention", $"Return to {productName} and connect again.", 400).ConfigureAwait(false);
            }
            finally { Redact(context); }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "OAuth callback for {Connector} failed ({ErrorType}).")]
    private static partial void LogCallbackFailed(ILogger logger, string connector, string errorType);

    // Callback query strings carry authorization codes; traces keep only the path.
    internal static void Redact(HttpContext context)
    {
        var activity = Activity.Current;
        activity?.SetTag("url.query", null);
        activity?.SetTag("http.target", context.Request.Path.Value);
        activity?.SetTag("url.full", $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}");
    }
}
