using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Synapses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit;

public sealed class BrainResidencyStorageFacts : IDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "db-residency-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider services;

    public BrainResidencyStorageFacts()
    {
        Directory.CreateDirectory(tempDir);
        var sc = new ServiceCollection();
        sc.AddSerializer();
        services = sc.BuildServiceProvider();
    }

    public void Dispose()
    {
        services.Dispose();
        try { if (Directory.Exists(tempDir)) { Directory.Delete(tempDir, recursive: true); } }
        catch { }
    }

    [Fact]
    public async Task EphemeralBrainLog_StaysPurelyInRam_EnforcesMemoryLimit_AndPromotesDurablyInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var serializer = services.GetRequiredService<Serializer>();
        var payloads = new JournalPayloads(new SignalTypeMap([]));
        var brainId = "try-" + Guid.NewGuid().ToString("N")[..12];
        var brainDir = Path.Combine(tempDir, brainId);
        var key = RandomNumberGenerator.GetBytes(32);

        // 1. Open purely in RAM with a tight 4 KiB memory limit.
        await using var log = BrainLog.CreateEphemeral(
            brainId,
            maxMemoryBytes: 4096,
            serializer,
            payloads,
            TimeProvider.System,
            NoRetention.Instance);

        Assert.True(log.IsEphemeral);
        Assert.False(Directory.Exists(brainDir));

        var smallPayload = Encoding.UTF8.GetBytes("""{"note":"kept-across-claim"}""");
        await log.AppendEncoded(
            grainId: "neuron.host/notes/main",
            stateName: "notes",
            expectedETag: null,
            change: BrainRecordChange.Write,
            record: smallPayload,
            recordType: "NotesState",
            publisher: "neuron.host/notes/main",
            encoded: [],
            side: null,
            ct: ct);

        Assert.True(log.MemoryUsedBytes > 0);
        // Zero files written to disk while ephemeral.
        Assert.False(Directory.Exists(brainDir));

        // 2. Exceeding MaxMemoryBytes throws BrainMemoryLimitExceededException without touching disk.
        var hugePayload = new byte[8192];
        Array.Fill(hugePayload, (byte)'x');
        await Assert.ThrowsAsync<BrainMemoryLimitExceededException>(() => log.AppendEncoded(
            grainId: "neuron.host/notes/big",
            stateName: "notes",
            expectedETag: null,
            change: BrainRecordChange.Write,
            record: hugePayload,
            recordType: "NotesState",
            publisher: "neuron.host/notes/big",
            encoded: [],
            side: null,
            ct: ct));
        Assert.False(Directory.Exists(brainDir));

        // 3. Promote in-place to durable encrypted storage.
        await log.PromoteToDurableAsync(brainDir, key, new NoopLease(), ct);

        Assert.False(log.IsEphemeral);
        Assert.True(Directory.Exists(brainDir));
        Assert.True(File.Exists(Path.Combine(brainDir, "current")));

        // 4. Subsequent large write now succeeds because durable storage has no ephemeral RAM cap.
        await log.AppendEncoded(
            grainId: "neuron.host/notes/after-claim",
            stateName: "notes",
            expectedETag: null,
            change: BrainRecordChange.Write,
            record: hugePayload,
            recordType: "NotesState",
            publisher: "neuron.host/notes/after-claim",
            encoded: [],
            side: null,
            ct: ct);

        await log.DisposeAsync();

        // 5. Reopen as a normal durable BrainLog and verify both pre-claim and post-claim records survived!
        await using var reopened = new BrainLog(
            brainDir,
            brainId,
            key,
            serializer,
            payloads,
            TimeProvider.System,
            NoRetention.Instance,
            new NoopLease());

        Assert.False(reopened.IsEphemeral);
        var beforeRecord = await reopened.Read("neuron.host/notes/main", "notes");
        Assert.NotNull(beforeRecord);
        Assert.Equal(smallPayload, beforeRecord.Record);

        var afterRecord = await reopened.Read("neuron.host/notes/after-claim", "notes");
        Assert.NotNull(afterRecord);
        Assert.Equal(hugePayload, afterRecord.Record);
    }

    private sealed class NoRetention : IRetentionPolicy
    {
        public static readonly NoRetention Instance = new();
        public ValueTask<long> FloorFor(string brainId, NeuronId publisher, long lastSequence, Func<TimeSpan, long> countNewerThan, CancellationToken ct = default)
            => ValueTask.FromResult(0L);
    }

    private sealed class NoopLease : IBrainLogLease
    {
        public bool IsHeld => true;
        public long BeginWrite() => 1;
        public void EndWrite(long token) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
