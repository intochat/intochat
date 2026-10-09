namespace DigitalBrain.Tools;

// A neuron whose tools are discovered at runtime: an MCP server, a page's agent API. The assistant
// finds its tools through neurons_search and runs one through Run; arguments is a JSON object that
// matches the tool's input schema. Implementations derive their own contract from this one.
[Alias("tools.source")]
public interface IToolSource : INeuron
{
    [Orleans.Concurrency.AlwaysInterleave] Task<ToolDescriptor[]> Tools(CancellationToken ct = default);
    [ResponseTimeout("00:10:00")] Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default);
}
