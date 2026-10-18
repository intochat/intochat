using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

// Watching a source is a call to another grain: one that never answers holds its own lane until the call times
// out, never the runtime's turn, so the edge, the shell, a live signal and an assignment all go on meanwhile.
public sealed class HangingWatchFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ASourceWhoseWatchHangsDoesNotHoldIdentityInspectANudgeOrAnApply()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var within = brain.Execution.AssertionTimeout;
        var owner = brain.Owner;
        var app = owner.Key("apps/hangs");
        var other = owner.Key("apps/other");
        var programs = RuntimeBrain.Programs(brain, owner.BrainId);
        var runtime = RuntimeBrain.Runtime(brain, owner.BrainId);
        var edge = RuntimeBrain.Edge(brain, owner.BrainId);
        await programs.Assign(app, RuntimeBrain.One(RuntimeBrain.Idle), []);
        await programs.Assign(other, RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        var hanging = brain.Get<IHangingSource>(owner.Key("apps/source/hanging"));
        var pinger = brain.Get<IPinger>(owner.Key("apps/source/pinger"));
        await edge.Subscribed(session, app, hanging.GetGrainId().ToString(), nameof(Pinged));
        var subscription = await edge.Subscribed(session, app, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct);
        var hang = HangingSource.Hang = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // Retiring a program watches every remaining source again, the hanging one among them.
            await programs.Retire(other).WaitAsync(within, ct);
            await HangingSource.Entered.Task.WaitAsync(ct);

            Assert.NotNull(await edge.Identity(session, app).WaitAsync(within, ct));
            Assert.Single((await runtime.Inspect(ct).WaitAsync(within, ct)).Behaviors);
            await pinger.Ping(1).WaitAsync(within, ct);
            while (!(await edge.PendingBatches(session).WaitAsync(within, ct)).Any(batch => batch.Subscription == subscription)) { await changes.NextAsync(ct: ct); }
            Assert.Single((await programs.Assign(other, RuntimeBrain.One(RuntimeBrain.Idle), []).WaitAsync(within, ct)).Behaviors);
        }
        finally { hang.TrySetResult(); }
    }
}

[Alias("tests.csharp.hanging-source")]
[Public]
public interface IHangingSource : INeuron
{
    Task Ping(int number);
}

// A source whose watch, once a fact arms it, answers only when the fact releases it.
public sealed class HangingSource : Neuron, IHangingSource
{
    public static TaskCompletionSource? Hang { get; set; }
    public static TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ping(int number) => PublishAsync(new Pinged(number));

    public override async Task<Guid> Watch(INeuronObserver observer, string[] signals)
    {
        if (Hang is { } hang)
        {
            Entered.TrySetResult();
            await hang.Task;
        }
        return await base.Watch(observer, signals);
    }
}
