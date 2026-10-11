using DigitalBrain.Framework.Mesh;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Mesh;

// T23-9: the part holder audits and repairs, because only it has the key that opens a manifest and its expected answers.
// Every AuditEvery, for each part this machine holds (PartLogs), one pass: the refs the head reaches are audited at the
// head's round (R111) and the round advances in one CAS. A failed or unusably placed chain segment is healed by an early
// snapshot; a failed snapshot ref is repaired and the new ref committed under the lease, its predecessor kept (R7). On
// challenge exhaustion the pass re-issues by snapshot and reports nothing (R110/R116). Parts nobody holds are not audited.
internal sealed partial class MeshAuditor(PartLogs logs, IMeshStore mesh, IShardClient shards, INodeDirectoryClient directory, IOptions<MeshOptions> options,
    [FromKeyedServices(MeshOptions.Key)] TimeProvider time, ILogger<MeshAuditor> logger) : IHostedService, IAsyncDisposable
{
    // R117: a report refused because this account's placement on the node was evicted; a miss, not a node failure.
    private const string Evicted = "placement evicted";

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private TaskCompletionSource _pass = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _auditing = Task.CompletedTask;

    // Completes when the pass after this call has finished (facts move a fake clock, then await it).
    internal Task NextPass()
    {
        lock (_gate) { return _pass.Task; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _auditing = Loop();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_stop.IsCancellationRequested) { await _stop.CancelAsync(); }
        await _auditing;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _stop.Dispose();
    }

    private async Task Loop()
    {
        using var timer = new PeriodicTimer(options.Value.AuditEvery, time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await Pass(_stop.Token);
                TaskCompletionSource done;
                lock (_gate) { (done, _pass) = (_pass, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)); }
                done.SetResult();
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task Pass(CancellationToken cancellationToken)
    {
        foreach (var part in logs.Opened.Distinct())
        {
            try { await Audit(await logs.For(part), cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            { Log.PassFailed(logger, error, part.Value); }
        }
    }

    private async Task Audit(PartLog log, CancellationToken cancellationToken)
    {
        var round = log.Head.AuditRound;
        if (round >= options.Value.ChallengesPerShard)
        {
            await log.HealBySnapshot(resetAuditRound: true);
            Log.Reissued(logger, log.Part.Value, round);
            return;
        }
        var (snapshot, chain) = await log.Reached();
        if (snapshot is null && chain.Length == 0) { return; }
        var health = (await directory.List()).ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        bool Failed(ShardAudit[] results) => results.Any(result => (!result.Passed && result.Reason != Evicted)
            || !(health.TryGetValue(result.NodeId, out var node) && node.Usable));

        var chainFailed = false;
        foreach (var reference in chain) { chainFailed |= Failed(await mesh.Audit(log.Key, reference, round, cancellationToken)); }
        var snapshotFailed = snapshot is not null && Failed(await mesh.Audit(log.Key, snapshot, round, cancellationToken));
        await log.AdvanceAuditRound();
        Log.Audited(logger, log.Part.Value, round, chain.Length + (snapshot is null ? 0 : 1));

        if (chainFailed)
        {
            await log.HealBySnapshot();
            Log.Healed(logger, log.Part.Value);
            return;
        }
        if (snapshotFailed) { await RepairSnapshot(log, snapshot!, cancellationToken); }
    }

    private async Task RepairSnapshot(PartLog log, MeshRef snapshot, CancellationToken cancellationToken)
    {
        var repair = await mesh.Repair(log.Key, snapshot, cancellationToken: cancellationToken);
        if (repair.Reference.ManifestCid == snapshot.ManifestCid) { return; }
        bool committed;
        try { committed = await log.CommitRepairedSnapshot(snapshot, repair.Reference); }
        catch (LeaseLostException) { committed = false; }
        if (!committed)
        {
            // Nothing was committed: only the new manifest's replicas go; its shards are shared with the old ref.
            await Drop(repair.Reference.ManifestNodes, repair.Reference.ManifestCid, cancellationToken);
            return;
        }
        Log.Repaired(logger, log.Part.Value, repair.Moved.Length);
        // R7: unmoved shards now belong to the repaired ref too; only the old manifest's replicas and the moved shards'
        // old copies are dropped, after the CAS.
        await Drop(snapshot.ManifestNodes.Except(repair.Reference.ManifestNodes, StringComparer.Ordinal), snapshot.ManifestCid, cancellationToken);
        if (repair.Moved.Length == 0 || mesh is not MeshStore store) { return; }
        var cids = await store.ShardCids(log.Key, repair.Reference, cancellationToken);
        foreach (var move in repair.Moved) { await Drop([move.FromNode], cids[move.Index], cancellationToken); }
    }

    private async Task Drop(IEnumerable<string> nodes, string cid, CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            try { await shards.Drop(node, cid, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { Log.NotDropped(logger, error, cid, node); }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Part {Part} audited at round {Round}: {Refs} refs.")]
        public static partial void Audited(ILogger logger, string part, int round, int refs);

        [LoggerMessage(Level = LogLevel.Information, Message = "Part {Part} spent its challenges at round {Round}; a new snapshot re-issues them.")]
        public static partial void Reissued(ILogger logger, string part, int round);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Part {Part} had a chain segment fail audit; a new snapshot replaced the chain.")]
        public static partial void Healed(ILogger logger, string part);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Part {Part}'s snapshot was repaired: {Moved} shards moved.")]
        public static partial void Repaired(ILogger logger, string part, int moved);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Part {Part} could not be audited this pass.")]
        public static partial void PassFailed(ILogger logger, Exception error, string part);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Could not drop {Cid} from node {Node}.")]
        public static partial void NotDropped(ILogger logger, Exception error, string cid, string node);
    }
}
