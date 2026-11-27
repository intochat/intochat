using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Access;
using DigitalBrain.Signals.Watch;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Http;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Kernel.Mcp;

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
            var caller = provider.GetService<ICallerAmbient>()?.Current ?? provider.GetRequiredService<McpCaller>().Stamp(SignalWatch);
            // A guest opens no signal watch, whichever way its caller was stamped.
            if (GuestSurface.Is(caller)) { throw new UnauthorizedAccessException($"A guest session cannot use {SignalWatch}."); }
            return provider.GetRequiredService<IIntoChatOSBrains>().Open(caller.Brain.Value);
        });
        services.TryAddSingleton<IBrainObserver>(IntoChat.OS.Kernel.Composition.Kernel.OsKernel.For);
        services.TryAddSingleton<IContractAssemblySource>(_ => new ModuleInventory(OperatingSystemModules.Modules));
        services.AddHttpContextAccessor();
        services.TryAddSingleton<McpCaller>();
        services.TryAddSingleton<Auth.InternalPageInspector>();
        services.TryAddScoped<NeuronSurface>();
        var mcpSerializerOptions = new System.Text.Json.JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        mcpSerializerOptions.TypeInfoResolverChain.Insert(0, KernelMcpJsonContext.Default);
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
            .WithTools<Tools.NeuronTools>(mcpSerializerOptions)
            .WithTools<Tools.ScreenTools>(mcpSerializerOptions)
            .WithTools<Tools.InternalPageTools>(mcpSerializerOptions)
            .WithTools<McpCaller>(mcpSerializerOptions);
    }

    public static void MapIntoChatOSMcp(this WebApplication app)
    {
        app.MapMcp("/mcp").AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.RequestServices.GetService<ICallerAmbient>()?.Current is not { } caller)
            {
                return Results.Unauthorized();
            }

            if (await BrainRouteGroups.Decide(context.HttpContext, caller.Brain.Value) is { } denied)
            {
                return denied;
            }

            var edge = context.HttpContext.RequestServices.GetRequiredService<EdgeAuthority>();
            using (edge.Enter(McpCaller.AsAgent(edge, caller)))
            {
                return await next(context);
            }
        });
        // The one door a /try guest has. The root /mcp map above is not one.
        BrainRouteGroups.Brain(app).MapMcp("/mcp").WithMetadata(GuestSurface.Door.Instance).AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.RequestServices.GetService<ICallerAmbient>()?.Current is not { } caller)
            {
                return Results.Unauthorized();
            }

            var edge = context.HttpContext.RequestServices.GetRequiredService<EdgeAuthority>();
            using (edge.Enter(McpCaller.AsAgent(edge, caller)))
            {
                return await next(context);
            }
        });
    }
}
