using System.Diagnostics;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
namespace DigitalBrain.Google.Gmail;
internal sealed class GmailOAuthPrivacySurface : IHttpSurface
{
    public void Map(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/google/gmail/oauth/callback" && !context.Request.Path.Value!.EndsWith("/gmail/login", StringComparison.Ordinal))
            { await next(context); return; }
            LoginPage.PrivateHeaders(context);
            Redact(context);
            try { await next(context); }
            finally { Redact(context); }
        });
    }
    private static void Redact(HttpContext context)
    {
        Activity.Current?.SetTag("url.query", null);
        Activity.Current?.SetTag("http.target", context.Request.Path.Value);
        Activity.Current?.SetTag("url.full", $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}");
    }
}
