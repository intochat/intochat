using System.Text.Json;
using DigitalBrain.Capabilities.Tools;
using IntoChat.OS.Modules.Tools;

namespace IntoChat.OS.Modules.Mcp;

// A connection neuron representing an external MCP tool source offering tool@1 capability.
[DigitalBrain.Contracts.NeuronAlias("mcp.tool-connection"), DigitalBrain.Contracts.DefaultNeuronType(TypeName)]
public interface IMcpToolConnection : ITool, IToolSource
{
    const string TypeName = "mcp.tool";

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<McpServerState> ReadState(CancellationToken ct = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default);
}
