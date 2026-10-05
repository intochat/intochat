using DigitalBrain;
using DigitalBrain.Framework;
using Orleans;

namespace DigitalBrain.Framework.Tests.Unit;

// A source busy in a turn that waits on its subscriber (an app deploying its own behavior) must
// still accept that subscriber's Watch, or the two grains wait on each other until the call
// times out.
public sealed class WatchCycleFacts
{
    [Fact]
    public async Task ASubscriberMayWatchASourceWhoseTurnIsWaitingOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);

        await brain.Get<ICycleSource>("source").Deploy("subscriber").WaitAsync(TimeSpan.FromSeconds(15), ct);
    }
}

public interface ICycleSource : INeuron
{
    Task Deploy(string subscriber);
}

public interface ICycleSubscriber : INeuronObserver, IGrainWithStringKey
{
    Task Subscribe(string source);
}

[GrainType("test-cycle-source")]
public sealed class CycleSource : Neuron, ICycleSource
{
    public Task Deploy(string subscriber) => GrainFactory.GetGrain<ICycleSubscriber>(subscriber).Subscribe(this.GetPrimaryKeyString());
}

[GrainType("test-cycle-subscriber")]
public sealed class CycleSubscriber : Grain, ICycleSubscriber
{
    public async Task Subscribe(string source)
        => await GrainFactory.GetGrain<ICycleSource>(source).Watch(this.AsReference<INeuronObserver>());

    public Task OnSignalAsync(Signal signal) => Task.CompletedTask;
}
