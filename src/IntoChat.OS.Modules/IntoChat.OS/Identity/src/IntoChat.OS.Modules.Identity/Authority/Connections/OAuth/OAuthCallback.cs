using System.Diagnostics;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Authority.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Connections.OAuth;

// The browser returns here with a code and the one-use state. The owner was recorded at login
// start; this edge stamps that owner, the way the authenticated edge stamps a cookie.
internal sealed class OAuthCallback : IHttpSurface
{
    private const string Prefix = "/connections/oauth/";
    private const string Suffix = "/callback";

    // ASP.NET Core's per-request Information log, which prints the full query string.
    internal const string RequestLogCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public void Map(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!path.StartsWith(Prefix, StringComparison.Ordinal) || !path.EndsWith(Suffix, StringComparison.Ordinal) || path.Length <= Prefix.Length + Suffix.Length)
            { await next(context); return; }
            Redact(context);
            LoginPage.PrivateHeaders(context);
            var connector = Uri.UnescapeDataString(path[Prefix.Length..^Suffix.Length]);
            var requests = context.RequestServices.GetRequiredService<OAuthLoginRequests>();
            var code = context.Request.Query["code"].ToString();
            if (!HttpMethods.IsGet(context.Request.Method) || string.IsNullOrWhiteSpace(code) || !requests.TryClaim(context.Request.Query["state"], out var login))
            {
                await LoginPage.WriteAsync(context, "This login link expired or was already used", "Return to IntoChat.OS and connect again.", 400);
                return;
            }
            var (_, keyConnector, connectionId) = ConnectionKeys.Parse(login.ConnectionKey);
            if (keyConnector != connector) { await LoginPage.WriteAsync(context, "Invalid callback", "Return to IntoChat.OS and connect again.", 400); return; }
            try
            {
                var exchange = context.RequestServices.GetRequiredService<OAuthTokenExchange>();
                var flow = context.RequestServices.GetRequiredService<OAuthLoginFlow>();
                using (CallerContextScope.Scoped(login.Owner))
                using (TraceContext.Enter(TraceContext.Begin(login.Owner.AccountId,
                    BrainScope.Create(login.Owner.AccountId, login.Owner.BrainId).Id, TraceContext.OriginOf(login.Owner.BrainId, login.ConnectionKey))))
                {
                    var neurons = context.RequestServices.GetRequiredService<AuthorityNeurons>();
                    // Another connector may have taken this id while the browser was at the provider;
                    // granting anyway would leave two keys under one id that probe and disconnect refuse.
                    if (await ConnectionEndpoints.IsTakenByAnotherConnector(neurons, connectionId, connector))
                    {
                        await LoginPage.WriteAsync(context, $"{connector} connection needs attention", "Return to IntoChat.OS and connect again.", 400);
                        return;
                    }
                    var grant = await exchange.ExchangeCodeAsync(login.ConnectionKey, login.AuthMethodId, code, login.CodeVerifier, flow.CallbackUri(connector).AbsoluteUri, login.Target, context.RequestAborted);
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
