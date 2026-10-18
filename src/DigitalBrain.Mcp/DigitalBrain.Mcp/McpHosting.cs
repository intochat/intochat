using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Surface;
using DigitalBrain.Authority.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Mcp;

public static class McpHosting
{
    public static void AddDigitalBrainMcp(this IServiceCollection services)
    {
        // Types come from the silo's kernel; this client carries contracts only to call neurons.
        services.AddSingleton<IRuntimeCapabilitySource, DigitalBrain.Tools.ToolCapabilitySource>();
        services.TryAddSingleton(NeuronTypeSearch.Create);
        services.AddIdentity();
        // IBrainAccess asks the trust side for memberships: the MCP composes the trust clients as a reader (R70).
        services.AddLocalPlatformClients();
        // Each request is a tool call in the caller's brain; a harness may hand in its own brain instead.
        services.TryAddScoped<IDigitalBrain>(provider => provider.GetRequiredService<IDigitalBrains>().Open(
            (CallerContextStamper.TryGet(out var caller) ? caller : provider.GetRequiredService<McpCaller>().Stamp()).BrainId));
        services.TryAddSingleton<IKernelView>(provider => new Kernel(provider.GetRequiredService<IGrainFactory>()));
        services.AddHttpContextAccessor();
        services.AddSingleton<McpCaller>();
        services.AddScoped<NeuronSurface>();
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
            .WithTools<NeuronTools>().WithTools<McpCaller>();
    }

    public static void MapDigitalBrainMcp(this WebApplication app)
    {
        app.MapMcp("/mcp").AddEndpointFilter(async (context, next) =>
            await BrainAccessFilter.Decide(context.HttpContext, CallerContextStamper.Require().BrainId) is { } denied
                ? denied : await next(context));
        BrainRoutes.Group(app).MapMcp("/mcp");
    }
}
