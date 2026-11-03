using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Runtime.Http.Surface;
using System.Diagnostics;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Connections.OAuth;

// Unified /integrations/{provider} edge for OAuth redirects (/integrations/oauth/callback, /integrations/{provider}/oauth/callback,
// /integrations/{provider}/callback, and legacy /connections/oauth/{provider}/callback) and inbound webhooks
// (POST /integrations/{provider}/webhook and POST /integrations/{provider}), routing to target grains via 3-part ConnectionKeys.
public sealed class OAuthCallback : IHttpSurface
{
    public const string CanonicalCallbackPath = "/integrations/oauth/callback";
    private const string IntegrationsPrefix = "/integrations/";
    private const string LegacyPrefix = "/connections/oauth/";
    private const string OAuthCallbackSuffix = "/oauth/callback";
    private const string CallbackSuffix = "/callback";
    private const string WebhookSuffix = "/webhook";
    public const string ConnectionKeyHeader = "X-IntoChat-Connection-Key";

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

    public static bool TryMatchWebhookPath(string? path, out string provider)
    {
        provider = "";
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(IntegrationsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (path.EndsWith(WebhookSuffix, StringComparison.Ordinal)
            && path.Length > IntegrationsPrefix.Length + WebhookSuffix.Length)
        {
            var segment = path[IntegrationsPrefix.Length..^WebhookSuffix.Length];
            if (!segment.Contains('/', StringComparison.Ordinal))
            {
                provider = Uri.UnescapeDataString(segment);
                return !string.IsNullOrWhiteSpace(provider);
            }
        }

        var tail = path[IntegrationsPrefix.Length..].TrimEnd('/');
        if (tail.Length > 0 && !tail.Contains('/', StringComparison.Ordinal)
            && !string.Equals(tail, "registration", StringComparison.OrdinalIgnoreCase))
        {
            provider = Uri.UnescapeDataString(tail);
            return !string.IsNullOrWhiteSpace(provider);
        }

        return false;
    }

    public static bool TryResolveWebhookConnectionKey(HttpRequest request, string provider, out string connectionKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        connectionKey = "";

        var rawKey = request.Query["connectionKey"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey) && request.Headers.TryGetValue(ConnectionKeyHeader, out var headerValues))
        {
            rawKey = headerValues.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(rawKey))
        {
            if (!ConnectionKeys.TryParse(rawKey, out var parsed) || !string.Equals(parsed.ConnectorId, provider, StringComparison.Ordinal))
            {
                return false;
            }
            connectionKey = ConnectionKeys.For(parsed.BrainId, parsed.ConnectorId, parsed.ConnectionId);
            return true;
        }

        var brain = request.Query["brain"].FirstOrDefault() ?? request.Query["brainId"].FirstOrDefault();
        var connectionId = request.Query["connectionId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(brain) && !string.IsNullOrWhiteSpace(connectionId) && BrainScope.IsValidId(brain))
        {
            connectionKey = ConnectionKeys.For(brain, provider, connectionId);
            return true;
        }

        return false;
    }

    public void Map(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (HttpMethods.IsPost(context.Request.Method) && TryMatchWebhookPath(path, out var webhookProvider))
            {
                await HandleWebhookAsync(context, webhookProvider);
                return;
            }

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
                using (CallerContextScope.Scoped(login.Owner))
                using (TraceContext.Enter(TraceContext.Begin(login.Owner.AccountId,
                    BrainScope.Create(login.Owner.AccountId, login.Owner.BrainId).Id, TraceContext.OriginOf(login.Owner.BrainId, login.ConnectionKey))))
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

    internal static async Task HandleWebhookAsync(HttpContext context, string provider)
    {
        if (!TryResolveWebhookConnectionKey(context.Request, provider, out var connectionKey))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await System.Text.Json.JsonSerializer.SerializeAsync(
                context.Response.Body,
                new { error = $"A valid 3-part ConnectionKey for provider '{provider}' is required." },
                cancellationToken: context.RequestAborted);
            return;
        }

        if (context.RequestServices?.GetService<AuthorityNeurons>() is { } neurons)
        {
            try
            {
                _ = neurons.ConnectionInbound(connectionKey);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                _ = error.Message;
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                status = "accepted",
                connectionKey,
                provider,
            },
            cancellationToken: context.RequestAborted);
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
