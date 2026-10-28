using System.Threading.Channels;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

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
        HoldingSignals? held = null;
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost(), apps: true)
            .ConfigureServices(services => services.AddSingleton<IPlainSignals>(provider => held = new(provider))).StartAsync(ct);
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
        held!.HoldUnsubscribe();

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

    // Wraps the kernel's plain signals: an armed unwatch is held on its way to the source until the fact releases it.
    private sealed class HoldingSignals(IServiceProvider services) : IPlainSignals
    {
        private readonly IPlainSignals _inner = (IPlainSignals)ActivatorUtilities.CreateInstance(services,
            typeof(IntoChat.OS.Kernel.Composition.KernelTestHooks).Assembly.GetType("IntoChat.OS.Kernel.Composition.PlainSignals")!);
        private TaskCompletionSource? _hold;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task UnsubscribeEntered => Entered.Task;
        public Task UnsubscribeDone => Done.Task;

        public void HoldUnsubscribe() => _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseUnsubscribe() => _hold!.TrySetResult();

        public IPlainSession Session(string brain, string session) => new Held(this, _inner.Session(brain, session));

        public Task<PlainSignalPage> Read(DigitalBrain.NeuronId publisher, long since, int limit, CancellationToken cancellationToken)
            => _inner.Read(publisher, since, limit, cancellationToken);

        private sealed class Held(HoldingSignals owner, IPlainSession inner) : IPlainSession
        {
            public Task Subscribe(DigitalBrain.NeuronId source, string[] aliases) => inner.Subscribe(source, aliases);

            public async Task Unsubscribe(DigitalBrain.NeuronId source)
            {
                if (owner._hold is { } hold)
                {
                    owner.Entered.TrySetResult();
                    await hold.Task;
                    await inner.Unsubscribe(source);
                    owner.Done.TrySetResult();
                    return;
                }
                await inner.Unsubscribe(source);
            }

            public Task<PlainSignalBatch?> Next(long acked, CancellationToken cancellationToken) => inner.Next(acked, cancellationToken);
            public Task End() => inner.End();
        }
    }
}
