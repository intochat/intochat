using System.Security.Cryptography;
using IntoChat.OS.Authority.Keys;
using IntoChat.OS.Authority.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

public sealed class BrainLogConcurrencyFacts(AuthorityHost host)
{
    private const int Appenders = 16;
    private const int Appends = 20;

    [Fact]
    public async Task ParallelAppendersACheckpointAndAReaderLoseNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var log = new BrainLog(Directory.CreateTempSubdirectory("brain-concurrent-").FullName, "owner", RandomNumberGenerator.GetBytes(32),
            host.Brain.Services.GetRequiredService<Serializer>(), BrainLogFormat.Framed);
        var checkpointed = new TaskCompletionSource();
        var reading = true;

        var reader = Task.Run(async () =>
        {
            while (Volatile.Read(ref reading)) { await log.ReadSignals(0, int.MaxValue, null, ct); }
        }, ct);
        var checkpoint = Task.Run(async () =>
        {
            await log.Checkpoint(ct);
            checkpointed.SetResult();
        }, ct);
        var appenders = Enumerable.Range(0, Appenders).Select(writer => Task.Run(async () =>
        {
            for (var i = 0; i < Appends; i++)
            {
                await log.Append($"g{writer}", $"s{i}", null, BrainRecordChange.Write, [(byte)i], [new BrainLogFacts.Changed($"{writer}-{i}")], ct);
            }
        }, ct)).ToArray();

        await Task.WhenAll(appenders);
        await Task.WhenAll(checkpoint, checkpointed.Task);
        Volatile.Write(ref reading, false);
        await reader;

        var signals = await log.ReadSignals(0, int.MaxValue, null, ct);
        Assert.Equal(Appenders * Appends, signals.Length);
        Assert.Equal(Enumerable.Range(1, Appenders * Appends).Select(n => (long)n), signals.Select(signal => signal.Sequence));
        for (var writer = 0; writer < Appenders; writer++)
        {
            for (var i = 0; i < Appends; i++) { Assert.Equal([(byte)i], (await log.Read($"g{writer}", $"s{i}"))!.Record); }
        }
    }

    [Fact]
    public async Task OpeningAnotherBrainDoesNotWaitForAnEvictedLogsLeaseRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownership = new BlockableOwnership();
        var options = Options.Create(new BrainStoreOptions { Directory = Directory.CreateTempSubdirectory("brain-evict-").FullName });
        var keys = new BrainStorageKeys(new ConfiguredMasterKeyCustody(RandomNumberGenerator.GetBytes(32)), options);
        await using var logs = new BrainLogs(keys, host.Brain.Services.GetRequiredService<Serializer>(), options, ownership: ownership);
        await logs.For("alpha").WaitAsync(ct);
        ownership.Lease("alpha").Lose();

        var reclaim = logs.For("alpha");
        try
        {
            await ownership.Lease("alpha").ReleaseStarted.Task.WaitAsync(ct);
            var other = await logs.For("beta").WaitAsync(ct);

            Assert.True(other.Usable);
            Assert.False(reclaim.IsCompleted);
        }
        finally { ownership.Lease("alpha").Proceed.TrySetResult(); }
        Assert.True((await reclaim.WaitAsync(ct)).Usable);
    }

    private sealed class BlockableOwnership : IBrainLogOwnership
    {
        public const string BlockedBrain = "alpha";
        private readonly Dictionary<string, GatedLease> leases = new(StringComparer.Ordinal);

        // The first claim of the blocked brain is the one whose release waits; a reclaim after it, and every other brain, release at once.
        public Task<IBrainLogLease> ClaimAsync(string directory, string brainId, CancellationToken ct)
        {
            Directory.CreateDirectory(directory);
            var lease = new GatedLease(blocks: brainId == BlockedBrain && !leases.ContainsKey(brainId));
            leases.TryAdd(brainId, lease);
            return Task.FromResult<IBrainLogLease>(lease);
        }

        public GatedLease Lease(string brainId) => leases[brainId];
    }

    private sealed class GatedLease(bool blocks) : IBrainLogLease
    {
        private volatile bool lost;
        public TaskCompletionSource ReleaseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsHeld => !lost;
        public void Lose() => lost = true;
        public long BeginWrite() => 0;
        public void EndWrite(long started) { }

        public async ValueTask DisposeAsync()
        {
            ReleaseStarted.TrySetResult();
            if (blocks) { await Proceed.Task; }
        }
    }
}
