namespace IntoChat.OS.Modules.Tools;

// A neuron whose tools are discovered at runtime: an MCP server, a page's agent API. Every instance of a
// type deriving this contract is a source; the assistant finds its live tools through neurons_search and runs one through Run; arguments is a JSON object that
// matches the tool's input schema. Implementations derive their own contract from this one.
[Alias("tools.source")]
public interface IToolSource : INeuron
{
    [Orleans.Concurrency.AlwaysInterleave] Task<ToolSet> Tools(CancellationToken ct = default);
    [ResponseTimeout("00:10:00")] Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default);
}
