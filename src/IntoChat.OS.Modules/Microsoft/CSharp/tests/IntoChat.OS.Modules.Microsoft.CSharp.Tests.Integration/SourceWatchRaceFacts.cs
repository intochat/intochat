using DigitalBrain.Runtime.Orleans.Signalling;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// Reassigning a program removes its subscriptions, so the runtime unwatches the source they named; the new program subscribes again
// while that unwatch is still on its way to the source. The unwatch must not outlive the new watch: the first request after a
// reassign is delivered, not lost.
public sealed class SourceWatchRaceFacts
{
    private const string Brain = "race-brain";
    private const string Echo = Brain + "/apps/echo";
    private const string Behavior = Echo + "/synapses/a.cs";

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnwatchStillInFlightWhenTheNewProgramSubscribesDoesNotEndItsWatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = new UnsubscribeGate();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost(), apps: true)
            .ConfigureServices(services => services.AddSingleton<IOutgoingGrainCallFilter>(held)).StartAsync(ct);
        var within = brain.Execution.AssertionTimeout;
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "", [new("open", "")], [], []), "spec");
        var programs = RuntimeBrain.Programs(brain, Brain);
        await programs.Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await RuntimeBrain.Runtime(brain, Brain).Inspect(ct)).SessionId!;
        var edge = RuntimeBrain.Edge(brain, Brain);
        await edge.Connected(session, RuntimeBrain.Fingerprint(brain));
        var source = RuntimeBrain.Plain<IApp>(brain, IApp.Key(Brain, "echo"));
        await edge.Subscribed(session, Behavior, source, RuntimeBrain.Requested);
        var stream = brain.Services.GetRequiredService<SessionStreams>().Open(session);
        held.HoldUnsubscribe();

        await programs.Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle + "// a new build\n" }, []);
        await held.UnsubscribeEntered.WaitAsync(within, ct);
        var subscription = await edge.Subscribed(session, Behavior, source, RuntimeBrain.Requested);
        held.ReleaseUnsubscribe();
        await held.UnsubscribeDone.WaitAsync(within, ct);
        var request = await app.Request("open", "first after the reassign");

        while (true)
        {
            var pushed = await stream.Reader.ReadAsync(ct).AsTask().WaitAsync(within, ct);
            if (pushed.Payload is SignalBatchEvent batch && batch.Subscription == subscription
                && batch.Signals.Any(json => SignalJson.RequestId(json) == request)) { break; }
        }
    }

    // Holds an armed session Unsubscribe on its way to the source until the fact releases it.
    private sealed class UnsubscribeGate : IOutgoingGrainCallFilter
    {
        private TaskCompletionSource? _hold;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task UnsubscribeEntered => Entered.Task;
        public Task UnsubscribeDone => Done.Task;

        public void HoldUnsubscribe() => _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseUnsubscribe() => _hold!.TrySetResult();

        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            if (_hold is { } hold && context.InterfaceMethod.DeclaringType == typeof(ISessionSynapses)
                && context.InterfaceMethod.Name == nameof(ISessionSynapses.Unsubscribe))
            {
                Entered.TrySetResult();
                await hold.Task.ConfigureAwait(false);
                await context.Invoke().ConfigureAwait(false);
                Done.TrySetResult();
                return;
            }
            await context.Invoke().ConfigureAwait(false);
        }
    }
}
