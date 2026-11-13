using System.Text.Json;
using DigitalBrain.Capabilities.Tools;

namespace IntoChat.OS.Modules.Tools;

// Brain-scoped aggregator connection for all tool@1 capabilities available in the brain.
[DigitalBrain.Contracts.NeuronAlias("brain.tools-connection"), DigitalBrain.Contracts.DefaultNeuronType(TypeName)]
public interface IBrainToolsConnection : ITool
{
    const string TypeName = "brain.tools";

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<IReadOnlyList<ToolDescriptor>> DescribeAll(CancellationToken ct = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default);
}
