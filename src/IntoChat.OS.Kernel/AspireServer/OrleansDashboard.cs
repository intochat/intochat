using System.Net;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Kernel.AspireServer;

// Co-hosted in the silo, so it ships in every image: Aspire, standalone or container. Grain state
// and method profiles are operator data, so only a request the host stamped a caller on gets in:
// the same rule as every other route, whatever the identity posture.
public static class OrleansDashboard
{
    public const string Path = "/orleans";

    public static WebApplication MapOrleansDashboard(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<IntoChatOSServerBuilder>().Dashboard)
        {
            var isLocalLoopbackAllowed = app.Environment.IsDevelopment()
                || string.Equals(app.Configuration["IntoChatOS:Topology"], "local", StringComparison.OrdinalIgnoreCase);
            Orleans.Dashboard.ServiceCollectionExtensions.MapOrleansDashboard(app, routePrefix: Path)
                .AllowAnonymous()
                .AddEndpointFilter(async (context, next) =>
                {
                    var isAuthenticated = context.HttpContext.RequestServices.GetService<DigitalBrain.Access.ICallerAmbient>()?.Current is not null;
                    var isLocalLoopback = isLocalLoopbackAllowed
                        && context.HttpContext.Connection.RemoteIpAddress is { } remoteIp
                        && IPAddress.IsLoopback(remoteIp);
                    return isAuthenticated || isLocalLoopback ? await next(context) : Results.Unauthorized();
                });
        }
        return app;
    }
}
