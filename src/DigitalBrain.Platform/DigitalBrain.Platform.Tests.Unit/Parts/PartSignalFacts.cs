using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public interface ISignalCounter : INeuron { Task Change(int value); Task Ping(); Task<string?> SeenInStorage(); }

[GrainType("test.signal-counter")]
public sealed class SignalCounterNeuron([PersistentState("counter", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state)
    : Neuron<PartCounterState>(state), ISignalCounter
{
    public Task Change(int value) => Save(new PartCounterState { Value = value }, new Changed(value));
    public Task Ping() => PublishAsync(new Pinged());
    public async Task<string?> SeenInStorage()
    {
        await Save(new PartCounterState { Value = -1 }, new Changed(-1));
        return SignalProbeStorage.Seen;
    }
}

[GenerateSerializer, Alias("test.signal-counter.changed")] public sealed record Changed([property: Id(0)] int Value) : Signal;
[GenerateSerializer, Alias("test.signal-counter.pinged")] public sealed record Pinged : Signal;

// Records what the storage write saw pending for its own grain, after Orleans has run it.
internal sealed class SignalProbeStorage(IGrainStorage inner) : IGrainStorage
{
    public static string? Seen { get; private set; }
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => inner.ReadStateAsync(stateName, grainId, grainState);
    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await Task.Yield();
        Seen = string.Join(",", SignalJournal.TakePending(grainId).Select(s => s.GetType().Name));
        await inner.WriteStateAsync(stateName, grainId, grainState);
    }
    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => inner.ClearStateAsync(stateName, grainId, grainState);
}

// Real timers (so batches flush) over a clock the fact can move past a lease.
internal sealed class SkewedTime : TimeProvider
{
    public TimeSpan Skew { get; set; }
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Skew;
}

public sealed class PartSignalFacts
{
    private static string Scope => BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;

    private static string Counter => $"{Scope}/apps/counter/s1";

    private static async Task<LogEntry> LastEntry(ModuleBrain brain)
    {
        var log = await brain.SiloServices.GetRequiredService<PartLogs>().For(PartId.Of(Counter));
        await log.Flushed();
        return log.SignalsSince(0)[^1];
    }

    [Fact(Timeout = 60_000)]
    public async Task AStateChangeAndItsSignalAreOneEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);

        await brain.Get<ISignalCounter>(Counter).Change(5);

        var entry = await LastEntry(brain);
        Assert.Equal(RecordChange.Write, entry.Change);
        Assert.Equal(5, Assert.IsType<Changed>(Assert.Single(entry.Signals)).Value);
    }

    [Fact(Timeout = 60_000)]
    public async Task ASignalWithoutAStateChangeIsItsOwnEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);
        var counter = brain.Get<ISignalCounter>(Counter);
        await counter.Change(1);

        await counter.Ping();

        var entry = await LastEntry(brain);
        Assert.Equal(RecordChange.None, entry.Change);
        Assert.IsType<Pinged>(Assert.Single(entry.Signals));
    }

    // The part log refuses the write: another writer moved the record past the neuron's ETag. (A lost lease
    // cannot refuse it here: the default brain's core needs an Own machine, which acknowledges on its journal.)
    [Fact(Timeout = 60_000)]
    public async Task AFailedStateWriteNeverPublishesItsSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithPartLog().StartAsync(ct);
        var counter = brain.Get<ISignalCounter>(Counter);
        await counter.Change(1);
        await using var probe = await brain.Observe<Changed>(counter, ct);
        var log = await brain.SiloServices.GetRequiredService<PartLogs>().For(PartId.Of(Counter));
        await log.Append(counter.GetGrainId().ToString(), "counter", RecordChange.Write,
            brain.SiloServices.GetRequiredService<Serializer>().SerializeToArray(new PartCounterState { Value = 9 }), []);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => counter.Change(2));
        await counter.Ping();
        await log.Flushed();

        Assert.IsType<InconsistentStateException>(error as InconsistentStateException ?? error.InnerException);
        Assert.DoesNotContain(log.SignalsSince(0), entry => entry.Signals.OfType<Changed>().Any(c => c.Value == 2));
        Assert.DoesNotContain(probe.Snapshot, c => c.Value == 2);
    }

    // On a non-own machine a commit refused by a lost lease closes the log (R12): the write throws and its
    // signal is neither delivered nor in the log. A machine cannot start as Cloud here (kernel instance records
    // live in the brain's core, which only own machines hold), so it boots Own, opening the core, and is then
    // demoted before the counter's part opens. A Pinged after the failure orders the probe.
    [Fact(Timeout = 60_000)]
    public async Task AWriteRefusedByALostLeaseNeverPublishesItsSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new SkewedTime();
        await using var brain = await ModuleTest.Create().WithPartLog()
            .ConfigureSilo(silo => silo.Services.AddSingleton<TimeProvider>(time))
            .StartAsync(ct);
        var options = brain.SiloServices.GetRequiredService<IOptions<PartsOptions>>().Value;
        options.Role = MachineRole.Cloud;
        var counter = brain.Get<ISignalCounter>(Counter);
        await counter.Change(1);
        await using var probe = await brain.SubscribeAsync<Signal>(counter, ct);
        var part = PartId.Of(Counter);
        var key = await brain.SiloServices.GetRequiredService<IPartKeys>().Key(part, ModuleTest.DefaultAccount, ct);
        using var rival = SequencerFacts.Machine();
        time.Skew = options.ShortLease + TimeSpan.FromSeconds(1);
        var taken = await PartLease.Acquire(part, key, brain.Grains, rival, PartLeaseFacts.Seen(), new PartsOptions { Role = MachineRole.Cloud }, time,
            brain.SiloServices.GetRequiredService<Serializer>());

        var error = await Assert.ThrowsAnyAsync<Exception>(() => counter.Change(2));
        await taken.Release();
        await counter.Ping();

        Assert.IsType<LeaseLostException>(error as LeaseLostException ?? error.InnerException);
        await using var next = probe.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await next.MoveNextAsync());
        Assert.IsType<Pinged>(next.Current);
        var log = await brain.SiloServices.GetRequiredService<PartLogs>().For(part);
        await log.Flushed();
        Assert.DoesNotContain(log.SignalsSince(0), entry => entry.Signals.OfType<Changed>().Any(c => c.Value == 2));
    }

    [Fact(Timeout = 60_000)]
    public async Task PlainHostsStillPublishAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var counter = brain.Get<ISignalCounter>(Counter);
        await using var probe = await brain.Observe<Changed>(counter, ct);

        await counter.Change(1);

        Assert.Equal(1, (await probe.NextAsync(ct: ct)).Value);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheStorageWriteOfASaveSeesItsSignalsPending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .ConfigureSilo(silo =>
            {
                var memory = silo.Services.Last(d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, DigitalBrainNames.ClusterGrainStorage));
                silo.Services.Remove(memory);
                silo.Services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.ClusterGrainStorage, (provider, key) =>
                    new SignalProbeStorage((IGrainStorage)memory.KeyedImplementationFactory!(provider, key)));
            })
            .StartAsync(ct);

        Assert.Equal(nameof(Changed), await brain.Get<ISignalCounter>(Counter).SeenInStorage());
    }
}
