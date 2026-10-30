using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Spi;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Mcp;

public static class McpHosting
{
    public static void AddIntoChatOSMcp(this IServiceCollection services)
    {
        // Types come from the silo's kernel; this client carries contracts only to call neurons.
        services.AddSingleton<IRuntimeCapabilitySource, IntoChat.OS.Modules.Tools.ToolCapabilitySource>();
        services.AddIntoChatOSConnections();
        services.AddIdentity();
        services.AddLocalPlatformClients();
        // Each request is a tool call in the caller's brain; a harness may hand in its own brain instead.
        services.TryAddScoped<ISignalWatch>(provider => provider.GetRequiredService<IIntoChatOSBrains>().Open(
            (CallerContextStamper.TryGet(out var caller) ? caller : provider.GetRequiredService<McpCaller>().Stamp()).BrainId));
        services.TryAddSingleton<IBrainObserver>(IntoChat.OS.Kernel.Composition.Kernel.For);
        services.AddHttpContextAccessor();
        services.AddSingleton<McpCaller>();
        services.AddScoped<NeuronSurface>();
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
            .WithTools<NeuronTools>().WithTools<McpCaller>();
    }

    public static void MapIntoChatOSMcp(this WebApplication app)
    {
        app.MapMcp("/mcp").AddEndpointFilter(async (context, next) =>
            await BrainAccessFilter.Decide(context.HttpContext, CallerContextStamper.Require().BrainId) is { } denied
                ? denied : await next(context));
        BrainRoutes.Group(app).MapMcp("/mcp");
    }
}
