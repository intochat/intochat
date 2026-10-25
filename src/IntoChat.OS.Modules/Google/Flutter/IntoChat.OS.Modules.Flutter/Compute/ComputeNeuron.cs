using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Modules.Flutter.Compute;

[GrainType("ui.compute")]
internal sealed class ComputeNeuron(IComputeReader reader) : Neuron, ICompute
{
    public async Task<ComputeState> Read()
    {
        if (this.GetPrimaryKeyString() != BrainScope.CurrentId() + "/compute")
        { throw new UnauthorizedAccessException("Read usage only through the current brain's compute neuron."); }
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var summary = await reader.Read(since);
        return new(summary.SpentMicros, since,
            summary.Rows.Select(row => new ComputeRow(row.Key, row.Micros, row.Count)).ToArray());
    }
}
