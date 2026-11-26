using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Kernel.Mcp;

[McpServerToolType]
internal sealed class McpCaller(IHttpContextAccessor http, IOptions<AuthOptions> auth, IBrainObserver kernel, EdgeAuthority edge)
{
    // The caller as stamped, plus the brains it may select with /brains/{brainId}/mcp.
    [McpServerTool(Name = NeuronSurfaceTools.ContextName), Description(NeuronSurfaceTools.ContextDescription)]
    public async Task<JsonObject> Context(CancellationToken cancellationToken)
    {
        var context = Describe(Stamp(NeuronSurfaceTools.ContextName));
        context["brains"] = JsonSerializer.SerializeToNode(await kernel.Brains(cancellationToken), KernelMcpJsonContext.Default.IReadOnlyListString);
        return context;
    }

    // MCP dispatch can run in a separate execution context. Stamp from the current HTTP
    // request at the tool boundary, rather than inheriting a previous MCP session's caller.
    // Every tool names itself: a guest is refused any tool outside GuestSurface.Tools.
    internal Caller Stamp(string tool)
    {
        var context = http.HttpContext ?? throw new UnauthorizedAccessException("MCP requires an HTTP caller.");
        var resolved = AccountSession.ResolveCaller(context, auth.Value)
            ?? throw new UnauthorizedAccessException("MCP requires an authenticated caller.");
        if (GuestSurface.Is(resolved) && !GuestSurface.MayUseTool(tool))
        {
            throw new UnauthorizedAccessException($"A guest session cannot use {tool}.");
        }

        var caller = AsAgent(edge, resolved);
        // Entered for the rest of this tool call's execution context; the tool boundary re-stamps on the next call.
        _ = edge.Enter(caller);
        return caller;
    }

    // MCP is the assistant acting for the session's person, in the brain the edge decided, in the "mcp" conversation.
    internal static Caller AsAgent(EdgeAuthority edge, Caller caller)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(caller);
        var person = caller.For ?? throw new UnauthorizedAccessException("MCP requires a caller acting for a person.");
        return edge.Agent(person, caller.Brain, conversation: "mcp");
    }

    private static JsonObject Describe(Caller caller) => new()
    {
        ["actor"] = caller.Actor.GetType().Name,
        ["principalId"] = caller.For?.Principal,
        ["accountId"] = caller.For?.Account,
        ["brainId"] = caller.Brain.Value,
        ["conversationId"] = caller.Conversation,
    };
}
