using DigitalBrain.Framework;

namespace DigitalBrain.Compute.Usage;

[GrainType("compute.usage")]
internal sealed class ComputeUsageNeuron(IUsageStore store) : Neuron, IComputeUsage
{
    public override NeuronAccess Access(string operation)
    {
        RequireScope(BrainScope.CurrentId());
        return new(BrainScope.CurrentId());
    }
    public async Task Append(string brainId, string id, string payload, DateTimeOffset revision, CancellationToken ct = default)
    {
        RequireScope(brainId);
        await store.AppendAsync(this.GetPrimaryKeyString(), brainId, id, payload, ct, revision);
    }
    public async Task<UsagePage> Read(string brainId, int limit, string? cursor, CancellationToken ct = default)
    {
        RequireScope(brainId);
        return await store.ReadAsync(this.GetPrimaryKeyString(), brainId, limit, cursor, ct);
    }

    private void RequireScope(string brainId)
    {
        if (CallerContextStamper.TryGet(out var caller) && caller.Kind != CallerKind.Platform
            && (caller.AccountId != this.GetPrimaryKeyString() || brainId != BrainScope.CurrentId()))
        { throw new UnauthorizedAccessException("Usage belongs to another brain."); }
    }
}
