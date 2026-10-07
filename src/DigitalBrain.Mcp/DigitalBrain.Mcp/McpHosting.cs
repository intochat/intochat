using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DigitalBrain.Platform.Identity;

namespace DigitalBrain.Mcp;

public static class McpHosting
{
    public static void AddDigitalBrainMcp(this IServiceCollection services)
    {
        var deployed = NeuronContracts.Deployed();
        services.AddSingleton(new ContractCatalog(deployed));
        services.AddSingleton(new NeuronContracts(deployed));
        services.AddContractCatalog(lexicalFallback: true);
        services.AddIdentity();
        // Each request is a tool call in the caller's brain; a harness may hand in its own brain instead.
        services.TryAddScoped<IDigitalBrain>(provider => provider.GetRequiredService<IDigitalBrains>().Open(
            (CallerContextStamper.TryGet(out var caller) ? caller : provider.GetRequiredService<McpCaller>().Stamp()).BrainId));
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
