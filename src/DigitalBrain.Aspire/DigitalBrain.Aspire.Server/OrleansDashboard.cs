using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Aspire.Server;

// Co-hosted in the silo, so it ships in every image: Aspire, standalone or container. Mapped
// behind the host's authentication because grain state and method profiles are operator data.
public static class OrleansDashboard
{
    public const string Path = "/orleans";

    public static WebApplication MapOrleansDashboard(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<DigitalBrainServerBuilder>().Dashboard)
        { global::Orleans.Dashboard.ServiceCollectionExtensions.MapOrleansDashboard(app, routePrefix: Path).RequireAuthorization(); }
        return app;
    }
}
