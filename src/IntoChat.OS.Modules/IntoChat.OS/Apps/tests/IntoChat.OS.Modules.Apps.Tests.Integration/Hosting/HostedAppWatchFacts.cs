using DigitalBrain;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Contracts;
using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

[DigitalBrain.Signal("test.hosted-watch.pinged")]
public sealed record HostedWatchPinged(string From) : DigitalBrain.Signal;

[NeuronAlias("test.pinger"), DefaultNeuronType("test.pinger")]
public interface IHostedWatchPinger
{
    Task Ping();
}

[Neuron("test.pinger")]
public sealed class HostedWatchPinger : Neuron, IHostedWatchPinger
{
    public Task Ping() => Publish(new HostedWatchPinged(Id.Key));
}

public sealed record HostedWatchState(string[] Heard)
{
    public HostedWatchState() : this([]) { }
}

/*
# Hosted Watch
Role: Test
Shipped: false
Operations:

Records each ping its set watch hears from the current ino.conversations member.
*/
[DigitalBrain.App("hosted-watch")]
public sealed class HostedWatchApp : App<HostedWatchState>
{
    protected override void Watches(WatchBuilder watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        watch.OnEach<IInoConversations, InoConversationsChanged, IHostedWatchPinger, HostedWatchPinged>(
            "pings",
            _ => IInoConversations.DefaultKey,
            (registry, _, _) => registry.Current(),
            pinged =>
            {
                Set(State with { Heard = [.. State.Heard, pinged.From] });
                return Task.CompletedTask;
            });
    }
}

public sealed class HostedAppWatchFacts
{
    private static readonly BrainId Brain = new("local");

    [Fact]
    public async Task HostedAppReceivesSetWatchSignalsAndFollowsRotation()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register<HostedWatchPinger>();
        var conversations = os.Get<IInoConversations>(new NeuronId(Brain, "ino.conversations", IInoConversations.DefaultKey));
        var first = os.Get<IHostedWatchPinger>(new NeuronId(Brain, "test.pinger", "pinger/first"));
        var second = os.Get<IHostedWatchPinger>(new NeuronId(Brain, "test.pinger", "pinger/second"));
        await conversations.Use("pinger/first");
        var definition = new AppDefinition(typeof(HostedWatchApp));
        var app = (HostedWatchApp)definition.Create(_ => null);
        var turns = new AppTurns(app, definition);
        await using var watches = await HostedAppWatches.Attach(os.ClusterListeners, turns, new AppAddress("local", "local/apps/hosted-watch"),
            (contract, id) => typeof(TestOS).GetMethod(nameof(TestOS.Get))!.MakeGenericMethod(contract).Invoke(os, [id])!,
            (error, _) => throw error);

        await first.Ping();
        await os.Idle();
        await turns.Enqueue(_ => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(["pinger/first"], app.State.Heard);

        await conversations.Use("pinger/second");
        await os.Idle();
        await first.Ping();
        await second.Ping();
        await os.Idle();
        await turns.Enqueue(_ => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(["pinger/first", "pinger/second"], app.State.Heard);
    }
}
