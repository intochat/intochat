using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
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
        await using var brain = await TestBrain.Create().StartAsync(ct);

        await brain.Get<ICycleSource>("source").Deploy("subscriber").WaitAsync(TimeSpan.FromSeconds(15), ct);
    }
}

[Alias("test.cycle-source")]
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

// A subscriber that starts watching while the source is still notifying (an app deploying its
// behavior mid-publish) must not break the source's delivery to everyone else.
public sealed class WatchDuringNotifyFacts
{
    [Fact]
    public async Task ANewWatcherDuringANotificationLeavesThePublishIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var source = brain.Get<IChattySource>("chatty");
        await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IRelayObserver>("relay").Attach("chatty");

        await source.Say().WaitAsync(TimeSpan.FromSeconds(15), ct);
        await source.Say().WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Equal(2, await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IRelayObserver>("relay").Heard());
        Assert.Equal(1, await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IRelayObserver>("late").Heard());
    }
}

[Alias("test.chatty-source")]
public interface IChattySource : INeuron
{
    Task Say();
}

[GrainType("test-chatty-source")]
public sealed class ChattySource : Neuron, IChattySource
{
    public Task Say() => PublishAsync(new Number(1));
}

public interface IRelayObserver : INeuronObserver, IGrainWithStringKey
{
    Task Attach(string source);
    Task<int> Heard();
}

// "relay" brings "late" in while its own notification is still being delivered.
[GrainType("test-relay-observer")]
public sealed class RelayObserver : Grain, IRelayObserver
{
    private int _heard;

    public async Task Attach(string source)
        => await GrainFactory.GetGrain<IChattySource>(source).Watch(this.AsReference<INeuronObserver>());

    public Task<int> Heard() => Task.FromResult(_heard);

    public async Task OnSignalAsync(Signal signal)
    {
        _heard++;
        if (this.GetPrimaryKeyString() == "relay" && _heard == 1)
        { await GrainFactory.GetGrain<IRelayObserver>("late").Attach("chatty"); }
    }
}
