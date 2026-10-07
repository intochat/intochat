using System.ComponentModel;
using System.Text.Json;
using DigitalBrain.Framework;
using ModelContextProtocol.Server;

namespace DigitalBrain.Mcp;

[McpServerToolType]
internal sealed class NeuronTools(NeuronSurface surface, McpCaller caller)
{
    [McpServerTool(Name = NeuronSurfaceTools.ListName), Description(NeuronSurfaceTools.ListDescription)]
    public IReadOnlyList<string> List()
    {
        caller.Stamp();
        return surface.List();
    }

    [McpServerTool(Name = NeuronSurfaceTools.SearchName), Description(NeuronSurfaceTools.SearchDescription)]
    public Task<IReadOnlyList<NeuronTypeHit>> Search(string query, CancellationToken cancellationToken = default)
    {
        caller.Stamp();
        return surface.Search(query, cancellationToken);
    }

    [McpServerTool(Name = NeuronSurfaceTools.GetName), Description(NeuronSurfaceTools.GetDescription)]
    public string Get(string contract, string key)
    {
        caller.Stamp();
        return surface.Get(contract, key);
    }

    [McpServerTool(Name = NeuronSurfaceTools.CallName), Description(NeuronSurfaceTools.CallDescription)]
    public Task<JsonElement?> Call(string contract, string key, string method, string arguments = "[]", CancellationToken cancellationToken = default)
    {
        caller.Stamp();
        return surface.Call(contract, key, method, arguments, cancellationToken);
    }
}
