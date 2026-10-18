using System.Text.Json;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class OperationSubscriptionFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AnOperationSelectorDeliversOnlyThatOperationAndKeepsItsWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        const string scope = "acc-brain";
        const string owner = "acc-brain/apps/x";
        await RuntimeBrain.Programs(brain, scope).Assign(owner, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["synapses/send.cs"] = RuntimeBrain.Idle,
            ["synapses/all.cs"] = RuntimeBrain.Idle,
        }, []);
        var runtime = RuntimeBrain.Runtime(brain, scope);
        var session = (await runtime.Inspect(ct)).SessionId!;
        var edge = RuntimeBrain.Edge(brain, scope);
        await edge.Connected(session, RuntimeBrain.Fingerprint(brain));
        var source = brain.Get<IPinger>("ops");
        var neuron = source.GetGrainId().ToString();
        var send = owner + "/synapses/send.cs";
        var all = owner + "/synapses/all.cs";
        var sendId = await edge.Subscribed(session, send, neuron, nameof(AppRequested), "send");
        var allId = await edge.Subscribed(session, all, neuron, nameof(AppRequested));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct);

        await source.Request("open");
        var opened = await Until(changes, async () => Single(await edge.PendingBatches(session), allId), ct);
        Assert.Equal(["open"], opened.Signals.Select(Operation).ToArray());
        Assert.DoesNotContain(await edge.PendingBatches(session), batch => batch.Subscription == sendId);

        await source.Request("send");
        var sent = await Until(changes, async () => Single(await edge.PendingBatches(session), sendId), ct);
        Assert.Equal(["send"], sent.Signals.Select(Operation).ToArray());
        var watermark = sent.Watermark;

        var replaced = await edge.Subscribed(session, send, neuron, nameof(AppRequested), "open");
        Assert.Equal(sendId, replaced);
        var kept = Assert.Single(await edge.PendingBatches(session), batch => batch.Subscription == sendId);
        Assert.Equal(watermark, kept.Watermark);
        Assert.Equal(["send"], kept.Signals.Select(Operation).ToArray());

        await source.Request("open");
        var narrowed = await Until(changes, async () =>
        {
            var batch = Single(await edge.PendingBatches(session), sendId);
            return batch is { Signals.Length: 2 } ? batch : null;
        }, ct);
        Assert.Equal(["send", "open"], narrowed.Signals.Select(Operation).ToArray());
    }

    private static async Task<SignalBatchEvent> Until(SignalProbe<BrainRuntimeChanged> changes, Func<Task<SignalBatchEvent?>> ready, CancellationToken ct)
    {
        while (true)
        {
            if (await ready() is { } batch) { return batch; }
            await changes.NextAsync(ct: ct);
        }
    }

    private static SignalBatchEvent? Single(IReadOnlyList<SignalBatchEvent> batches, string subscription)
        => batches.SingleOrDefault(batch => batch.Subscription == subscription);

    private static string Operation(string json) => JsonDocument.Parse(json).RootElement.GetProperty("operation").GetString()!;
}
