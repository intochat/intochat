using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Platform.Identity;
using ModelContextProtocol.AspNetCore;

namespace DigitalBrain.Mcp;

public static class McpHosting
{
    public static void AddDigitalBrainMcp(this IServiceCollection services)
    {
        services.AddSingleton(new ContractCatalog(NeuronContracts.Deployed()));
        services.AddContractCatalog(lexicalFallback: true);
        services.AddIdentity();
        // Each request is a tool call in the caller's brain; a harness may hand in its own brain instead.
        services.TryAddScoped<IDigitalBrain>(provider => provider.GetRequiredService<IDigitalBrains>().Open(provider.GetRequiredService<McpCaller>().Stamp().BrainId));
        services.AddHttpContextAccessor();
        services.AddSingleton<McpCaller>();
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
