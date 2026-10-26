namespace IntoChat.OS.Modules.Tools;

// A neuron whose tools are discovered at runtime: an MCP server, a page's agent API. Every instance of a
// type deriving this contract is a source; the assistant finds its live tools through neurons_search and runs one through Run; arguments is a JSON object that
// matches the tool's input schema. Implementations derive their own contract from this one.
// R4-21(6): the tool-source shape is not itself a grain interface, so a plain contract (Gmail, PageTools in 06-06d) can
// derive it. The Orleans scheduling attributes stay for the still-legacy implementations until the Framework leaves (06-08);
// the DigitalBrain ones serve plain implementations.
public interface IToolSource
{
    [Orleans.Concurrency.AlwaysInterleave, DigitalBrain.Interleaves] Task<ToolSet> Tools(CancellationToken ct = default);
    [ResponseTimeout("00:10:00"), DigitalBrain.CallTimeout(600_000)] Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default);
}

// Transitional (R4-21(6), deleted in 06-08): the grain form of a tool source. Still-legacy sources (IMcpServer, IGmail,
// IPageTools) derive it, and the tool capability source reaches them through it. It keeps the grain interface alias.
[Alias("tools.source")]
public interface ILegacyToolSource : IToolSource, INeuron;
