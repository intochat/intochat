using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace IntoChat.OS.Mcp;

[McpServerToolType]
internal sealed class McpCaller(IHttpContextAccessor http, IOptions<AuthOptions> auth, IKernelView kernel)
{
    // The caller as stamped, plus the brains it may select with /brains/{brainId}/mcp.
    [McpServerTool(Name = NeuronSurfaceTools.ContextName), Description(NeuronSurfaceTools.ContextDescription)]
    public async Task<JsonObject> Context(CancellationToken cancellationToken)
    {
        var context = JsonSerializer.SerializeToNode(Stamp(), JsonSerializerOptions.Web)!.AsObject();
        context["brains"] = JsonSerializer.SerializeToNode(await kernel.Brains(cancellationToken), JsonSerializerOptions.Web);
        return context;
    }

    // MCP dispatch can run in a separate execution context. Stamp from the current HTTP
    // request at the tool boundary, rather than inheriting a previous MCP session's caller.
    internal CallerContext Stamp()
    {
        var context = http.HttpContext ?? throw new UnauthorizedAccessException("MCP requires an HTTP caller.");
        var caller = AccountSession.ResolveCaller(context, auth.Value)
            ?? throw new UnauthorizedAccessException("MCP requires an authenticated caller.");
        return CallerContextStamper.Stamp(caller);
    }
}
