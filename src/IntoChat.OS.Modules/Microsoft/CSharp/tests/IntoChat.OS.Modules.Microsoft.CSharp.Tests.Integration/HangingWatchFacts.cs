using IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// Subscribing to a source is a call to another neuron (06-08: its admission of the program's session synapse): one that never answers
// holds its own lane until the call times out, never the runtime's turn, so the edge, the shell, a live signal and an assignment all
// go on meanwhile.
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
        await edge.Subscribed(session, app, RuntimeBrain.Source(hanging), Pings.Pinged);
        var subscription = await edge.Subscribed(session, app, RuntimeBrain.Source(pinger), Pings.Pinged);
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

[DigitalBrain.Contracts.NeuronAlias("tests.csharp.hanging-source"), DigitalBrain.Contracts.DefaultNeuronType("tests.csharp.hanging-source")]
[Public]
public interface IHangingSource
{
    Task Ping(int number);
}

// A source whose admission of a subscriber, once a fact arms it, answers only when the fact releases it.
[DigitalBrain.Neuron("tests.csharp.hanging-source")]
public sealed class HangingSource : DigitalBrain.Neuron, IHangingSource
{
    public static TaskCompletionSource? Hang { get; set; }
    public static TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ping(int number) => Publish(new Pinged(number));

    protected override async Task AdmitSynapse(DigitalBrain.SynapseRequest request)
    {
        if (Hang is { } hang)
        {
            Entered.TrySetResult();
            await hang.Task;
        }
        await base.AdmitSynapse(request);
    }
}
