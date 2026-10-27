using IntoChat.OS.Testing.Neurons;
using DB = DigitalBrain;

namespace IntoChat.OS.Testing.Tests.Integration;

// The both-runtimes harness itself (06-03, R4-05): one fact body runs on TestOS and on SiloHost.Local, with a typed call,
// a Save that journals a signal, the journal read back and a restart over the same state.
public sealed class BothRuntimesFixtureFacts(HarnessProbeFixture fixture) : IClassFixture<HarnessProbeFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ATypedCallSavesStateAndItsSignalOnBothRuntimes(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        var id = runtime.Id(HarnessCounter.Type, "calls");
        var counter = runtime.Get<IHarnessCounter>(id);

        Assert.Equal(1, await counter.Add(1));
        Assert.Equal(3, await counter.Add(2));

        var counted = await runtime.WaitForSignals<HarnessCounted>(id, 2, Within, ct);
        Assert.Equal([1, 3], counted.Select(signal => signal.Total));
        Assert.Equal(kind, runtime.Kind);
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task StateAndSignalsSurviveARestart(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(kind, ct);
        var id = runtime.Id(HarnessCounter.Type, "restart");
        await runtime.Get<IHarnessCounter>(id).Add(5);

        await runtime.Restart(ct);

        Assert.Equal(6, await runtime.Get<IHarnessCounter>(id).Add(1));
        Assert.Equal([5, 6], (await runtime.WaitForSignals<HarnessCounted>(id, 2, Within, ct)).Select(signal => signal.Total));
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ElapseLetsTimePassOnBothRuntimes(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        var counter = runtime.Get<IHarnessCounter>(runtime.Id(HarnessCounter.Type, "clock"));
        var before = await counter.Now();

        await runtime.Elapse(TimeSpan.FromMilliseconds(200), ct);

        Assert.True(await counter.Now() - before >= TimeSpan.FromMilliseconds(200));
    }
}

public sealed class HarnessProbeFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } = [typeof(HarnessProbeModule)];
}

[DB.Module("testing-harness-probe")]
public sealed class HarnessProbeModule : DB.Module
{
    public static DB.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}

[DB.Signal("testing.harness-counted")]
public sealed record HarnessCounted(int Total) : DB.Signal;

public sealed record HarnessCount
{
    public int Total { get; init; }
}

public interface IHarnessCounter
{
    Task<int> Add(int by);
    Task<DateTimeOffset> Now();
}

[DB.Neuron(Type)]
public sealed class HarnessCounter : DB.Neuron<HarnessCount>, IHarnessCounter
{
    public const string Type = "testing.harness-counter";

    public async Task<int> Add(int by)
    {
        var next = State with { Total = State.Total + by };
        await Save(next, new HarnessCounted(next.Total));
        return next.Total;
    }

    public Task<DateTimeOffset> Now() => Task.FromResult(Context.Time.GetUtcNow());
}
