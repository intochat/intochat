using System.Collections.Concurrent;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Modules.Microsoft.CSharp.Assemblies;
using IntoChat.OS.Modules.Microsoft.CSharp.Files;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class CpuMeteringFacts
{
    private const string Acc = "acc-brain";
    private const string Owner = "acc-brain/apps/x";
    private const string Behavior = "acc-brain/apps/x/synapses/a.cs";

    [Trait("host", "own")]
    [Fact]
    public async Task AnUntracedBatchUsesItsSavedProgramCallerForCpuAttribution()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter { RequireAttribution = true };
        await using var brain = await StartAsync(new FakeSessionHost(), meter, ct);
        var owner = brain.Owner;
        var app = owner.Key("apps/metered");
        var runtime = RuntimeBrain.Edge(brain, owner.BrainId);
        using (owner.AsCaller()) { await RuntimeBrain.Programs(brain, owner.BrainId).Assign(app, Synapse(), []); }
        var session = (await RuntimeBrain.Runtime(brain, owner.BrainId).Inspect(ct)).SessionId!;
        await runtime.Connected(session, RuntimeBrain.Fingerprint(brain));
        var source = brain.Get<IPinger>(owner.Key("untraced"));
        var subscription = await runtime.Subscribed(session, app + "/synapses/a.cs", source.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(RuntimeBrain.Runtime(brain, owner.BrainId), ct);
        await source.Ping(1);
        await changes.NextAsync(ct: ct);

        await runtime.AcknowledgeMany(session, [new AcknowledgeReport(subscription, 1, 1_000)]);
        Assert.True(await meter.AttributionAttempt.Task.WaitAsync(brain.Execution.AssertionTimeout, ct),
            "Untraced script pricing must retain the trusted program caller.");
        var recorded = Assert.Single(meter.Calls, call => call.Kind == CostKind.Script);
        Assert.Equal(owner.BrainId, recorded.Caller?.BrainId);
        Assert.Equal(owner.AccountId, recorded.Caller?.AccountId);
        Assert.NotNull(recorded.TraceId);
        Assert.Equal(owner.BrainId, recorded.TraceBrain);
        Assert.Equal(owner.AccountId, recorded.TracePayer);
    }

    // A synapse's batch and the CPU it cost: the ack prices the run against the batch's own trace.
    [Trait("host", "own")]
    [Fact]
    public async Task AnAcknowledgedBatchRecordsItsCpuTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        var host = new FakeSessionHost();
        await using var brain = await StartAsync(host, meter, ct);
        var runtime = RuntimeBrain.Edge(brain, Acc);
        await RuntimeBrain.Programs(brain, Acc).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await runtime.Connected(session, RuntimeBrain.Fingerprint(brain));

        // The retaining source seeds the subscription's backlog, so the batch's first signal carries a trace.
        var source = brain.Get<IRetaining>("retaining");
        var trace = new Trace { TraceId = "4bf92f3577b34da6a3ce929d0e0e4736", PayerAccountId = "account-a", BrainId = Acc, Origin = new NeuronId("acc-brain/edge") };
        RetainingSource.Traced = [new RetainedSignal(1, new Pinged(1) { Trace = trace })];
        var subscription = await runtime.Subscribed(session, Behavior, source.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        // An arrival is what delivers a batch: the runtime builds it and remembers the trace to price on ack.
        await source.Fire();
        await changes.NextAsync(ct: ct);

        await runtime.AcknowledgeMany(session, [new AcknowledgeReport(subscription, 2, 1_234_567)]);

        // The session host's own HTTP calls are metered too; the ack prices the script.
        var recorded = Assert.Single(meter.Calls, call => call.Kind == CostKind.Script);
        Assert.Equal("cpu-ms", recorded.Item);
        Assert.Equal(1_234.567m, recorded.Quantity);
        Assert.Equal("ms", recorded.Unit);
        Assert.Equal(Funding.Local, recorded.Funding);
        Assert.Equal($"{subscription}:2", recorded.CostId);
        Assert.Equal(trace.TraceId, recorded.TraceId);
    }

    // Pricing observes a run: a ledger that is slow to answer must not hold the acknowledgement, or every other
    // call of the brain's runtime waits behind it.
    [Trait("host", "own")]
    [Fact]
    public async Task ASlowMeterDoesNotHoldTheAcknowledgement()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        await using var brain = await StartAsync(new FakeSessionHost(), meter, ct);
        var runtime = RuntimeBrain.Edge(brain, Acc);
        await RuntimeBrain.Programs(brain, Acc).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await runtime.Connected(session, RuntimeBrain.Fingerprint(brain));
        var source = brain.Get<IPinger>("metered");
        var subscription = await runtime.Subscribed(session, Behavior, source.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await source.Ping(1);
        await changes.NextAsync(ct: ct);
        meter.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var within = brain.Execution.AssertionTimeout;
            await runtime.AcknowledgeMany(session, [new AcknowledgeReport(subscription, 1, 1_000)]).WaitAsync(within, ct);
            Assert.Empty(await runtime.PendingBatches(session).WaitAsync(within, ct));
        }
        finally { meter.Hold.TrySetResult(); }

        Assert.Equal($"{subscription}:1", Assert.Single(meter.Calls, call => call.Kind == CostKind.Script).CostId);
    }

    // A batch the synapse passed over ran nothing it owns: the ack settles it without a priced run.
    [Trait("host", "own")]
    [Fact]
    public async Task ASkippedBatchIsNotPriced()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        await using var brain = await StartAsync(new FakeSessionHost(), meter, ct);
        var runtime = RuntimeBrain.Edge(brain, Acc);
        await RuntimeBrain.Programs(brain, Acc).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await runtime.Connected(session, RuntimeBrain.Fingerprint(brain));
        var source = brain.Get<IPinger>("skipped");
        var subscription = await runtime.Subscribed(session, Behavior, source.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await source.Ping(1);
        await changes.NextAsync(ct: ct);

        await runtime.AcknowledgeMany(session, [new AcknowledgeReport(subscription, 1, 1_000, Skipped: true)]);
        // Runs are priced one after another, so once the handled batch behind it is priced the skip would have been.
        await source.Ping(2);
        await changes.NextAsync(ct: ct);
        await runtime.AcknowledgeMany(session, [new AcknowledgeReport(subscription, 2, 1_000)]);
        await meter.ScriptPriced.Task.WaitAsync(ct);

        Assert.Equal($"{subscription}:2", Assert.Single(meter.Calls, call => call.Kind == CostKind.Script).CostId);
    }

    private static Dictionary<string, string> Synapse()
        => new(StringComparer.Ordinal) { ["synapses/a.cs"] = RuntimeBrain.Idle };

    // The meter is the kernel's pricing seam; the fact reads what the ack priced from it.
    private static Task<ITestBrain> StartAsync(FakeSessionHost host, RecordingMeter meter, CancellationToken ct)
        => RuntimeBrain.Create(host).ConfigureServices(services => services.AddSingleton<ICostMeter>(meter)).StartAsync(ct);

    private static IBrainRuntime Runtime(ITestBrain brain) => RuntimeBrain.Runtime(brain, Acc);

    private sealed record Recorded(CostKind Kind, string Item, decimal Quantity, string Unit, Funding Funding, string CostId, string? TraceId, CostSubject? Subject, CallerContext? Caller, string? TraceBrain, string? TracePayer);

    private sealed class RecordingMeter : ICostMeter
    {
        public ValueTask<ExternalCallHandle> BeginExternalCallAsync(ExternalCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask MarkExternalCallDispatchStartedAsync(ExternalCallHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ExternalCallCompletion> CompleteExternalCallAsync(ExternalCallHandle handle, ExternalCallOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ConcurrentQueue<Recorded> Calls { get; } = new();
        public bool RequireAttribution { get; init; }
        public TaskCompletionSource<bool> AttributionAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Once armed, a script cost is recorded and then answered only when released, as a busy ledger answers.
        public TaskCompletionSource? Hold { get; set; }

        public TaskCompletionSource ScriptPriced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken, CostSubject? subject = null)
        {
            var caller = CallerContextStamper.TryGet(out var stamped) ? stamped : null;
            if (kind == CostKind.Script && RequireAttribution)
            {
                var attributed = TraceContext.Current is not null || caller is not null;
                AttributionAttempt.TrySetResult(attributed);
                if (!attributed) { throw new UntrustedCallerException("Script usage has no trusted attribution."); }
            }
            Calls.Enqueue(new Recorded(kind, item, quantity, unit, funding, costId, TraceContext.Current?.TraceId, subject, caller,
                TraceContext.Current?.BrainId, TraceContext.Current?.PayerAccountId));
            if (kind == CostKind.Script) { ScriptPriced.TrySetResult(); }
            if (kind == CostKind.Script && Hold is { } hold) { await hold.Task; }
            return new Cost
            {
                TraceId = TraceContext.Current?.TraceId ?? "",
                CostId = costId,
                Kind = kind,
                Item = item,
                Quantity = quantity,
                Unit = unit,
                Micros = 0,
            };
        }
    }
}

// A source that keeps one signal, so a subscription seeded from it carries a real trace, and that
// publishes an arrival that delivers the batch. Its own interface addresses the grain without
// colliding with the vocabulary's IRetainsSignals mapping.
[Alias("tests.csharp.retaining")]
public interface IRetaining : IRetainsSignals
{
    Task Fire();
}

public sealed class RetainingSource : Neuron, IRetaining
{
    public static RetainedSignal[] Traced { get; set; } = [];

    public Task<RetainedSignal[]> Retained(string signal, long since) => Task.FromResult(Traced);

    public Task Fire() => PublishAsync(new Pinged(1));
}
