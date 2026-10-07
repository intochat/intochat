using DigitalBrain.Framework;
using System.ComponentModel;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using DigitalBrain.Framework.Surface;

namespace DigitalBrain.Mcp;

[McpServerToolType]
internal sealed class McpCaller(IHttpContextAccessor http, IOptions<AuthOptions> auth)
{
    [McpServerTool(Name = NeuronSurfaceTools.ContextName), Description(NeuronSurfaceTools.ContextDescription)]
    public CallerContext Context() => Stamp();

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
