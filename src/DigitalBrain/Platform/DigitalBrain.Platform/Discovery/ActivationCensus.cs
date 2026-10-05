using System.Runtime.ExceptionServices;
using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Discovery;

internal sealed class ActivationCensus(IGrainFactory grains) : IActivationCensus
{
    private readonly AsyncLocal<Snapshot?> _request = new();

    public async Task<ActivationRecord[]> Activations(string grainTypeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grainTypeId);
        ct.ThrowIfCancellationRequested();
        var snapshot = _request.Value ?? await Capture(ct);
        snapshot.Error?.Throw();
        return snapshot.Records.Where(item => item.TypeId == grainTypeId).ToArray();
    }

    // A single Active request owns this input until fan-out finishes. Nothing is retained
    // for subsequent requests; direct provider calls still read a fresh runtime census.
    internal async Task<Snapshot> Capture(CancellationToken ct)
    {
        try { return new(await Read(ct), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return new([], ExceptionDispatchInfo.Capture(error)); }
    }

    internal IDisposable Use(Snapshot snapshot)
    {
        var previous = _request.Value;
        _request.Value = snapshot;
        return new RequestScope(() => _request.Value = previous);
    }

    private async Task<ActivationRecord[]> Read(CancellationToken ct)
    {
        var statistics = await grains.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics().WaitAsync(ct);
        return statistics.Select(item => new ActivationRecord(item.GrainId.Type.ToString(), item.GrainId.Key.ToString())).Distinct().ToArray();
    }

    internal sealed record Snapshot(ActivationRecord[] Records, ExceptionDispatchInfo? Error);
    private sealed class RequestScope(Action restore) : IDisposable { public void Dispose() => restore(); }
}
