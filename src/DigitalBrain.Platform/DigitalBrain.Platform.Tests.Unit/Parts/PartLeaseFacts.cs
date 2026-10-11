using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartLeaseFacts
{
    internal static DigitalBrain.Platform.Trust.ISequencerClient Sequencer(ITestBrain brain) =>
        brain.SiloServices.GetRequiredService<DigitalBrain.Platform.Trust.ISequencerClient>();

    internal static SeenVersions Seen() => new(Path.Combine(Path.GetTempPath(), $"seen-{Guid.NewGuid():N}.json"));

    private static PartsOptions Short => new() { Role = MachineRole.Cloud, ShortLease = TimeSpan.FromSeconds(1), RenewBefore = TimeSpan.FromMilliseconds(500) };

    [Fact(Timeout = 60_000)]
    public async Task ALeaseIsExclusiveUntilItIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var options = new PartsOptions { Role = MachineRole.Own };

        var a = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), alice, Seen(), options, TimeProvider.System, serializer);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), bob, Seen(), options, TimeProvider.System, serializer));
        await a.Release();
        Assert.NotNull(await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), bob, Seen(), options, TimeProvider.System, serializer));
    }

    [Fact(Timeout = 60_000)]
    public async Task ACommitAfterAnotherMachineTookTheLeaseFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await TestBrain.Create().ConfigureServices(services => services.AddSingleton<TimeProvider>(time)).StartAsync(ct);
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var a = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), alice, Seen(), Short, time, serializer);

        time.Advance(TimeSpan.FromSeconds(2));
        var b = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), bob, Seen(), Short, time, serializer);

        await Assert.ThrowsAsync<LeaseLostException>(() => a.Commit(new HeadPayload { LastSeq = 99 }));
        await Assert.ThrowsAsync<LeaseLostException>(a.Renew);
        Assert.Equal(b.Version, (await brain.Grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf(part.Value)).Get())!.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentCommitsAndRenewalsOnOneLeaseAllSucceedInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        using var alice = SequencerFacts.Machine();
        var lease = await PartLease.Acquire(part, RandomNumberGenerator.GetBytes(32), PartLeaseFacts.Sequencer(brain), alice, Seen(), new PartsOptions(), TimeProvider.System,
            brain.SiloServices.GetRequiredService<Serializer>());
        var start = lease.Version;

        var calls = Enumerable.Range(0, 10).SelectMany(i => new[] { lease.Commit(new HeadPayload { LastSeq = i }), lease.Renew() });
        await Task.WhenAll(calls.ToArray());

        Assert.Equal(start + 20, (await brain.Grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf(part.Value)).Get())!.Version);
        Assert.Equal(start + 20, lease.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheCoreIsNeverLeasedToAMachineThatIsNotTheOwners()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var cloud = SequencerFacts.Machine();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => PartLease.Acquire(PartId.Core("home"), RandomNumberGenerator.GetBytes(32),
            PartLeaseFacts.Sequencer(brain), cloud, Seen(), new PartsOptions { Role = MachineRole.Cloud }, TimeProvider.System, brain.SiloServices.GetRequiredService<Serializer>()));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOlderHeadThanOnceSeenIsRefusedAtAcquire()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        using var alice = SequencerFacts.Machine();
        var seen = Seen();
        seen.Saw(HeadRecord.HashOf(part.Value), 5);
        await brain.Grains.GetGrain<ISequencerGrain>(HeadRecord.HashOf(part.Value))
            .CompareAndSet(0, SequencerFacts.Record(alice, HeadRecord.HashOf(part.Value), 1, TimeSpan.FromSeconds(-1)));

        await Assert.ThrowsAsync<RollbackDetectedException>(() => PartLease.Acquire(part, RandomNumberGenerator.GetBytes(32), PartLeaseFacts.Sequencer(brain), alice, seen,
            new PartsOptions(), TimeProvider.System, brain.SiloServices.GetRequiredService<Serializer>()));
    }
}
