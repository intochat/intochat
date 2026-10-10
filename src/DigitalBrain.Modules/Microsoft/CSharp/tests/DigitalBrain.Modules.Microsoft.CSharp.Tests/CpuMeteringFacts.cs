using System.Collections.Concurrent;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Microsoft.CSharp.Sessions;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CpuMeteringFacts
{
    private const string Acc = "acc-brain";
    private const string Owner = "acc-brain/apps/x";
    private const string Behavior = "acc-brain/apps/x/synapses/a.cs";

    // A synapse's batch and the CPU it cost: the ack prices the run against the batch's own trace.
    [Fact(Timeout = 60_000)]
    public async Task AnAcknowledgedBatchRecordsItsCpuTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        var host = new FakeSessionHost();
        await using var brain = await StartAsync(host, meter, ct);
        var runtime = Grains(brain).GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For(Acc));
        await Grains(brain).GetGrain<IBrainPrograms>(BrainRuntimeKeys.For(Acc)).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await runtime.Connected(session, ContractsFingerprint.Of(brain.SiloServices.GetRequiredService<CompiledTypes>().Assemblies));

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

        var recorded = Assert.Single(meter.Calls);
        Assert.Equal(CostKind.Script, recorded.Kind);
        Assert.Equal("cpu-ms", recorded.Item);
        Assert.Equal(1_234.567m, recorded.Quantity);
        Assert.Equal("ms", recorded.Unit);
        Assert.Equal(Funding.Local, recorded.Funding);
        Assert.Equal($"{subscription}:2", recorded.CostId);
        Assert.Equal(trace.TraceId, recorded.TraceId);
    }

    private static Dictionary<string, string> Synapse()
        => new(StringComparer.Ordinal) { ["synapses/a.cs"] = RuntimeBrain.Idle };

    // The harness the other runtime facts use, plus a meter that records what the ack prices.
    private static Task<ModuleBrain> StartAsync(FakeSessionHost host, RecordingMeter meter, CancellationToken ct)
    {
        var builder = ModuleTest.Create().WithModule<CSharpModule>().WithReminders()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddHttpClient<SessionRunner>().ConfigurePrimaryHttpMessageHandler(() => host);
                silo.Services.AddSingleton(new MemoryBehaviorAssemblies());
                silo.Services.AddSingleton<ICostMeter>(meter);
                silo.Services.AddSingleton(new CompiledTypes([typeof(IPinger).Assembly, typeof(ICSharpFile).Assembly]));
                silo.Services.PostConfigure<CSharpDeploymentSettings>(options =>
                {
                    options.EdgeUrl = "http://edge.test/";
                    options.SandboxUrl = FakeSessionHost.Url;
                });
            });
        return builder.StartAsync(ct);
    }

    private static IGrainFactory Grains(ModuleBrain brain) => brain.Grains;

    private static IBrainRuntime Runtime(ModuleBrain brain) => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Acc));

    private sealed record Recorded(CostKind Kind, string Item, decimal Quantity, string Unit, Funding Funding, string CostId, string? TraceId);

    private sealed class RecordingMeter : ICostMeter
    {
        public ConcurrentQueue<Recorded> Calls { get; } = new();

        public ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new Recorded(kind, item, quantity, unit, funding, costId, TraceContext.Current?.TraceId));
            return ValueTask.FromResult(new Cost
            {
                TraceId = TraceContext.Current?.TraceId ?? "",
                CostId = costId,
                Kind = kind,
                Item = item,
                Quantity = quantity,
                Unit = unit,
                Micros = 0,
            });
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