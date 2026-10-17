using DigitalBrain.Framework;
using DigitalBrain.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Storage;

[Alias("test.local-counter"), Orleans.Metadata.DefaultGrainType("test.local-counter")]
public interface ILocalCounter : INeuron { Task Set(int value); Task<int> Read(); }

[GenerateSerializer, Alias("test.local-counter.state")]
public sealed class LocalCounterState { [Id(0)] public int Value { get; set; } }

[GenerateSerializer, Alias("test.local-counter.changed")]
public sealed record LocalChanged([property: Id(0)] int Value) : Signal;

[GrainType("test.local-counter")]
public sealed class LocalCounter([PersistentState("s", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<LocalCounterState> storage)
    : Neuron<LocalCounterState>(storage), ILocalCounter
{
    public Task Set(int value) => Save(new LocalCounterState { Value = value }, new LocalChanged(value));
    public Task<int> Read() => Task.FromResult(Snapshot.Value);
}

public sealed class BrainStorageFacts
{
    [Fact]
    public async Task LocalAccountsDoNotRegisterTheRuntimeAsAMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var identity = brain.Services.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>();
        var person = await brain.PersonAsync("local-user", ct);
        Assert.NotNull(person);
        Assert.NotNull(await identity.ReadAccount("owner"));
    }

    [Fact]
    public async Task TheProductionRecipePersistsStateAndSignalsWithoutAMachineRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        Assert.NotNull(brain.Services.GetService<BrainLogs>());
        using (brain.Owner.AsCaller()) { await brain.Get<ILocalCounter>("owner/apps/counter").Set(17); }
        var before = await brain.Services.GetRequiredService<ISignalJournal>().ReadSince("owner", 0, 1000);
        var changed = Assert.Single(before, item => item.Signal is LocalChanged);
        await brain.RestartAsync(ct);
        using (brain.Owner.AsCaller()) { Assert.Equal(17, await brain.Get<ILocalCounter>("owner/apps/counter").Read()); }
        var after = await brain.Services.GetRequiredService<ISignalJournal>().ReadSince("owner", 0, 1000);
        Assert.Equal(changed.Sequence, Assert.Single(after, item => item.Signal is LocalChanged).Sequence);
        Assert.DoesNotContain(await brain.Services.GetRequiredService<ISignalJournal>().ReadSince("owner", changed.Sequence, 1000), item => item.Signal is LocalChanged);
    }

    // The nudge of a Save that rode in the storage write names the last signal's sequence, the one Read reports,
    // not the log entry's (a record's ETag).
    [Fact]
    public async Task AStorageWriteNudgesWithItsLastSignalSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = brain.Get<ILocalCounter>("owner/apps/counter");
        var observer = new NudgeRecorder();
        brain.Services.GetRequiredService<ILocalSignalHub>().Subscribe(counter.GetGrainId(), observer);
        using (brain.Owner.AsCaller())
        {
            await counter.Set(1);
            await counter.Set(2);
            var nudge = await observer.Second.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var page = await counter.Read(0, 100);
            Assert.Equal(page.Sequences[^1], nudge.Sequence);
            Assert.Equal(2, Assert.IsType<LocalChanged>(page.Signals[^1]).Value);
        }
    }

    private sealed class NudgeRecorder : INeuronObserver
    {
        private int _count;
        public TaskCompletionSource<SignalNudge> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Nudge(SignalNudge nudge)
        {
            if (Interlocked.Increment(ref _count) == 2) { Second.TrySetResult(nudge); }
            return Task.CompletedTask;
        }
    }
}
