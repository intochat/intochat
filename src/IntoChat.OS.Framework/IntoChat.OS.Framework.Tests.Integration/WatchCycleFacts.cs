using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Framework.Tests.Integration;

// A source busy in a turn that waits on its subscriber (an app deploying its own behavior) must
// still accept that subscriber's Watch, or the two grains wait on each other until the call
// times out.
public sealed class WatchCycleFacts(FrameworkHost host)
{
    [Fact]
    public async Task ASubscriberMayWatchASourceWhoseTurnIsWaitingOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;

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

    public Task Nudge(SignalNudge nudge) => Task.CompletedTask;
}

// A subscriber that starts watching while the source is still notifying (an app deploying its
// behavior mid-publish) must not break the source's delivery to everyone else.
public sealed class WatchDuringNotifyFacts(FrameworkHost host)
{
    [Fact]
    public async Task ANewWatcherDuringANotificationLeavesThePublishIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var source = brain.Get<IChattySource>("chatty");
        await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IRelayObserver>("relay").Attach("chatty");

        var relay = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IRelayObserver>("relay");
        var late = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IRelayObserver>("late");

        // Nudges are one-way: "relay" has brought "late" in once it has heard the first signal.
        await source.Say().WaitAsync(TimeSpan.FromSeconds(15), ct);
        await TestWait.UntilAsync(_ => relay.Heard(), heard => heard == 1, TimeSpan.FromSeconds(15), ct);
        await source.Say().WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Equal(2, await TestWait.UntilAsync(_ => relay.Heard(), heard => heard >= 2, TimeSpan.FromSeconds(15), ct));
        Assert.Equal(1, await TestWait.UntilAsync(_ => late.Heard(), heard => heard >= 1, TimeSpan.FromSeconds(15), ct));
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
    private long _cursor;

    // A new watcher hears what is published from now on.
    public async Task Attach(string source)
    {
        var chatty = GrainFactory.GetGrain<IChattySource>(source);
        await chatty.Watch(this.AsReference<INeuronObserver>());
        _cursor = (await chatty.Read(0, 0)).Last;
    }

    public Task<int> Heard() => Task.FromResult(_heard);

    public async Task Nudge(SignalNudge nudge)
    {
        (var signals, _cursor) = await NudgeReader.ReadAfter(NudgeReader.Source(GrainFactory, nudge), _cursor);
        if (signals.Length == 0) { return; }
        _heard += signals.Length;
        if (this.GetPrimaryKeyString() == "relay" && _heard == 1)
        { await GrainFactory.GetGrain<IRelayObserver>("late").Attach("chatty"); }
    }
}
