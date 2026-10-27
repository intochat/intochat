using DigitalBrain;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Modules.Flutter.Compute;

[Neuron("ui.compute")]
internal sealed class ComputeNeuron(IComputeReader reader) : DigitalBrain.Neuron, ICompute
{
    public async Task<ComputeState> Read()
    {
        if ($"{Id.Brain.Value}/{Id.Key}" != BrainScope.CurrentId() + "/compute")
        { throw new UnauthorizedAccessException("Read usage only through the current brain's compute neuron."); }
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var summary = await reader.Read(since);
        return new(summary.SpentMicros, since,
            summary.Rows.Select(row => new ComputeRow(row.Key, row.Micros, row.Count)).ToArray());
    }
}
