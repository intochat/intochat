using DigitalBrain.Invocation;
using IntoChat.OS.Kernel.Composition;
using DB = DigitalBrain;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;

// Typed calls cross the runtimes while families migrate (06-02, R4-12): plain to legacy and legacy to plain, each with the
// caller's neuron id as provenance, and one IAsyncEnumerable each way (T-06-41).
[Trait("host", "own")]
public sealed class LegacyCallFacts
{
    private const string Brain = "owner";

    private static IPlainProbe Plain(ITestBrain brain, string key)
        => NeuronProxy.Create<IPlainProbe>(DB.NeuronId.Parse($"{Brain}/probe.plain/{key}"), new LegacyGrainSink(brain.Grains(), "owner/test.edge/facts"));

    private static Task<ITestBrain> Start(CancellationToken ct) => TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);

    [Fact(Timeout = 120_000)]
    public async Task APlainNeuronCallsALegacyOneAndTheLegacyOneSeesThePlainCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);

        var answer = await Plain(brain, "a").CallLegacy("l1", "hi");

        Assert.Equal("hi|owner/probe.plain/a", answer);
    }

    [Fact(Timeout = 120_000)]
    public async Task ALegacyNeuronCallsAPlainOneAndThePlainOneSeesTheLegacyCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);

        var answer = await brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/l2").CallPlain("b", "hello");

        Assert.Equal("hello|owner/probe.legacy/l2", answer);
    }

    // 06-07b (R4-17): a module neuron reaches a legacy grain through ILegacyNeurons as itself, and only in its own brain.
    [Fact(Timeout = 120_000)]
    public async Task AModuleNeuronCallsALegacyOneThroughTheOsServiceAsItself()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);

        Assert.Equal("hey|owner/probe.plain/m", await Plain(brain, "m").CallLegacyAsModule(Brain, "l5", "hey"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Plain(brain, "m").CallLegacyAsModule("someone-else", "l5", "no"));
    }

    [Fact(Timeout = 120_000)]
    public async Task APlainNeuronStreamsFromALegacyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        List<int> items = [];

        await foreach (var item in Plain(brain, "c").CountLegacy("l3", 3, ct).WithCancellation(ct)) { items.Add(item); }

        Assert.Equal([1, 2, 3], items);
    }

    [Fact(Timeout = 120_000)]
    public async Task ALegacyNeuronStreamsFromAPlainOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        List<int> items = [];

        await foreach (var item in brain.Grains().GetGrain<ILegacyProbe>($"{Brain}/l4").CountPlain("d", 4, ct).WithCancellation(ct)) { items.Add(item); }

        Assert.Equal([1, 2, 3, 4], items);
    }

    [Fact(Timeout = 120_000)]
    public async Task AContractThatIsNotTheTargetsGrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var wrong = NeuronProxy.Create<ILegacyProbe>(DB.NeuronId.Parse($"{Brain}/probe.other/x"), new LegacyGrainSink(brain.Grains(), "owner/test.edge/facts"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.Echo("no"));

        Assert.Contains("probe.legacy", error.Message, StringComparison.Ordinal);
    }
}
