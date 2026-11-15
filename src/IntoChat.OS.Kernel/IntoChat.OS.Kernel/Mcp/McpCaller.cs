using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain.Capabilities;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace IntoChat.OS.Mcp;

[McpServerToolType]
internal sealed class McpCaller(IHttpContextAccessor http, IOptions<AuthOptions> auth, IBrainObserver kernel)
{
    // The caller as stamped, plus the brains it may select with /brains/{brainId}/mcp.
    [McpServerTool(Name = NeuronSurfaceTools.ContextName), Description(NeuronSurfaceTools.ContextDescription)]
    public async Task<JsonObject> Context(CancellationToken cancellationToken)
    {
        var context = JsonSerializer.SerializeToNode(Stamp(NeuronSurfaceTools.ContextName), JsonSerializerOptions.Web)!.AsObject();
        context["brains"] = JsonSerializer.SerializeToNode(await kernel.Brains(cancellationToken), JsonSerializerOptions.Web);
        return context;
    }

    // MCP dispatch can run in a separate execution context. Stamp from the current HTTP
    // request at the tool boundary, rather than inheriting a previous MCP session's caller.
    // Every tool names itself: a guest is refused any tool outside GuestSurface.Tools.
    internal CapabilityCaller Stamp(string tool)
    {
        var context = http.HttpContext ?? throw new UnauthorizedAccessException("MCP requires an HTTP caller.");
        var resolved = AccountSession.ResolveCaller(context, auth.Value)
            ?? throw new UnauthorizedAccessException("MCP requires an authenticated caller.");
        if (GuestSurface.Is(resolved) && !GuestSurface.MayUseTool(tool))
        {
            throw new UnauthorizedAccessException($"A guest session cannot use {tool}.");
        }

        var caller = AsAgent(resolved);
        RuntimeCallerContext.Stamp(caller);
        return caller;
    }

    internal static CapabilityCaller AsAgent(CapabilityCaller caller)
        => caller with { AppId = CapabilityCallers.AssistantApp, ConversationId = "mcp" };
}
