using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
            Orleans.Dashboard.ServiceCollectionExtensions.MapOrleansDashboard(app, routePrefix: Path)
                .AddEndpointFilter(static async (context, next) =>
                    context.HttpContext.RequestServices.GetService<DigitalBrain.Access.ICallerAmbient>()?.Current is not null ? await next(context) : Results.Unauthorized());
        }
        return app;
    }
}
