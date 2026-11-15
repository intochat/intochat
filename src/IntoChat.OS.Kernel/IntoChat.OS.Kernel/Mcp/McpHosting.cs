using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Signals.Watch;
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
    // What the watch below stamps as when no caller is stamped yet. It is not a guest tool.
    private const string SignalWatch = "a signal watch";

    public static void AddIntoChatOSMcp(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRuntimeCapabilitySource, IntoChat.OS.Modules.Tools.ToolCapabilitySource>());
        services.AddIntoChatOSConnections();
        services.AddIdentity();
        services.AddLocalPlatformClients();
        // Each request is a tool call in the caller's brain; a harness may hand in its own brain instead.
        services.TryAddScoped<ISignalWatch>(provider =>
        {
            var caller = RuntimeCallerContext.TryGet(out var stamped) ? stamped : provider.GetRequiredService<McpCaller>().Stamp(SignalWatch);
            // A guest opens no signal watch, whichever way its caller was stamped.
            if (GuestSurface.Is(caller)) { throw new UnauthorizedAccessException($"A guest session cannot use {SignalWatch}."); }
            return provider.GetRequiredService<IIntoChatOSBrains>().Open(caller.BrainId);
        });
        services.TryAddSingleton<IBrainObserver>(IntoChat.OS.Kernel.Composition.Kernel.For);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<McpCaller>();
        services.TryAddScoped<NeuronSurface>();
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
            .WithTools<NeuronTools>().WithTools<ScreenTools>().WithTools<McpCaller>();
    }

    public static void MapIntoChatOSMcp(this WebApplication app)
    {
        app.MapMcp("/mcp").AddEndpointFilter(async (context, next) =>
        {
            if (!RuntimeCallerContext.TryGet(out var caller))
            {
                return Results.Unauthorized();
            }

            if (await BrainAccessFilter.Decide(context.HttpContext, caller.BrainId) is { } denied)
            {
                return denied;
            }

            using (RuntimeCallerContext.Stamp(McpCaller.AsAgent(caller)))
            {
                return await next(context);
            }
        });
        // The one door a /try guest has. The root /mcp map above is not one.
        BrainRoutes.Group(app).MapMcp("/mcp").WithMetadata(GuestSurface.Door.Instance).AddEndpointFilter(async (context, next) =>
        {
            if (!RuntimeCallerContext.TryGet(out var caller))
            {
                return Results.Unauthorized();
            }

            using (RuntimeCallerContext.Stamp(McpCaller.AsAgent(caller)))
            {
                return await next(context);
            }
        });
    }
}
