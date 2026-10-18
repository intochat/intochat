using DigitalBrain.Framework.Compute;
using DigitalBrain.Authority.Compute;

namespace DigitalBrain.Authority.Tests.Integration;

public sealed class UsageFacts
{
    private const string BrainId = "brain-usage-1";

    [Fact]
    public async Task UsageGroupsByAppSynapseNeuronAndOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var usage = await ActiveUsage(brain);

        await usage.Add(Cost("tr-1", "c-1", 1_000, "mail", "on-inbound", "gmail/inbox", DateTimeOffset.UtcNow), "gmail/edge");
        await usage.Add(Cost("tr-1", "c-2", 500, "mail", "on-inbound", "gmail/inbox", DateTimeOffset.UtcNow), "gmail/edge");

        var since = DateTimeOffset.UtcNow.AddDays(-1);
        AssertRow(await usage.Group(UsageGrouping.App, since, 10), "mail", 1_500, 2);
        AssertRow(await usage.Group(UsageGrouping.Synapse, since, 10), "on-inbound", 1_500, 2);
        AssertRow(await usage.Group(UsageGrouping.Neuron, since, 10), "gmail/inbox", 1_500, 2);
        AssertRow(await usage.Group(UsageGrouping.Origin, since, 10), "gmail/edge", 1_500, 2);
    }

    [Fact]
    public async Task TheMostExpensiveComesFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var usage = await ActiveUsage(brain);

        await usage.Add(Cost("tr-1", "c-1", 2_000, "mail", "on-inbound", "gmail/inbox", DateTimeOffset.UtcNow), "origin/cheap");
        await usage.Add(Cost("tr-2", "c-2", 11_000, "notes", "on-edited", "notes/doc", DateTimeOffset.UtcNow), "origin/expensive");

        var rows = await usage.Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10);
        Assert.Equal(2, rows.Length);
        Assert.Equal("origin/expensive", rows[0].Key);
        Assert.Equal(11_000, rows[0].Micros);
        Assert.Equal("origin/cheap", rows[1].Key);
        Assert.Equal(2_000, rows[1].Micros);
    }

    [Fact]
    public async Task RowsSinceExcludeOlderDays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var usage = await ActiveUsage(brain);
        var now = DateTimeOffset.UtcNow;

        await usage.Add(Cost("tr-1", "c-1", 500, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge");
        await usage.Add(Cost("tr-2", "c-2", 600, "notes", "on-saved", "notes/doc", now.AddDays(-3)), "notes/edge");

        var rows = await usage.Group(UsageGrouping.Origin, now.AddDays(-1), 10);
        AssertRow(rows, "gmail/edge", 500, 1);
    }

    [Fact]
    public async Task UsageSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var usage = await ActiveUsage(brain);
        var now = DateTimeOffset.UtcNow;

        await usage.Add(Cost("tr-1", "c-1", 10_000, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge");
        await usage.Add(Cost("tr-1", "c-2", 4_000, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge");
        Assert.Equal(2, (await usage.Recent(10)).Length);
        await brain.DeactivateAsync(usage, ct);

        AssertRow(await usage.Group(UsageGrouping.App, now.AddDays(-1), 10), "mail", 14_000, 2);
        AssertRow(await usage.Group(UsageGrouping.Origin, now.AddDays(-1), 10), "gmail/edge", 14_000, 2);
        var recent = await usage.Recent(10);
        Assert.Equal(2, recent.Length);
        Assert.Equal(4_000, recent[0].Micros);
        Assert.Equal(10_000, recent[1].Micros);
    }

    // A busy brain's costs arrive in bursts: each Add applies its cost at once and shares the write that is out,
    // so a burst costs one more write however long it is, and the daily totals still count every cost.
    [Fact]
    public async Task ABurstOfAddsCostsAtMostTwoWrites()
    {
        var store = new ScriptedUsageStore { Held = new TaskCompletionSource() };
        var usage = new UsageGrain(store);
        var now = DateTimeOffset.UtcNow;

        var adds = Enumerable.Range(0, 50).Select(i => usage.Add(Cost("tr-1", $"c-{i}", 100, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge")).ToArray();
        store.Held.SetResult();
        await Task.WhenAll(adds);

        Assert.InRange(store.Writes, 1, 2);
        AssertRow(await usage.Group(UsageGrouping.App, now.AddDays(-1), 10), "mail", 5_000, 50);
        AssertRow(await usage.Group(UsageGrouping.Origin, now.AddDays(-1), 10), "gmail/edge", 5_000, 50);
    }

    // The ring holds what GET /compute/recent can serve, newest first; the daily totals still count every cost.
    [Fact]
    public async Task TheRingKeepsTheLatestCostsTheRecentEndpointCanServe()
    {
        var usage = new UsageGrain(new ScriptedUsageStore());
        var now = DateTimeOffset.UtcNow;
        var total = UsageGrain.RecentLimit + 10;

        for (var i = 0; i < total; i++) { await usage.Add(Cost("tr-1", $"c-{i}", 1, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge"); }

        var recent = await usage.Recent(total);
        Assert.Equal(UsageGrain.RecentLimit, recent.Length);
        Assert.Equal($"c-{total - 1}", recent[0].CostId);
        Assert.Equal("c-10", recent[^1].CostId);
        AssertRow(await usage.Group(UsageGrouping.App, now.AddDays(-1), 10), "mail", total, total);
    }

    [Fact]
    public async Task AUsageWriteAfterTenThousandChargesPersistsABoundedPayload()
    {
        var store = new ScriptedUsageStore();
        var usage = new UsageGrain(store);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10_000; i++) { await usage.Add(Cost("tr-1", $"c-{i}", 2, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge"); }

        Assert.Equal(UsageGrain.RecentLimit, store.State.Recent.Length);
        Assert.True(store.State.Daily.Length <= 5);
        AssertRow(await usage.Group(UsageGrouping.App, now.AddDays(-1), 10), "mail", 20_000, 10_000);
    }

    // Costs reach usage one-way; a read sent after them is answered after them once the grain is active.
    private static async Task<IUsageGrain> ActiveUsage(ITestBrain brain)
    {
        var usage = brain.Grains.GetGrain<IUsageGrain>(BrainId);
        await usage.Recent(0);
        return usage;
    }

    private static void AssertRow(CostRow[] rows, string key, long micros, long count)
    {
        var row = Assert.Single(rows);
        Assert.Equal(key, row.Key);
        Assert.Equal(micros, row.Micros);
        Assert.Equal(count, row.Count);
    }

    private static Cost Cost(string traceId, string costId, long micros, string app, string synapse, string neuron, DateTimeOffset at) => new()
    {
        TraceId = traceId,
        CostId = costId,
        Kind = CostKind.Model,
        Item = "gpt-5.1:input",
        Quantity = 1m,
        Unit = "mtok",
        Micros = micros,
        App = app,
        Synapse = synapse,
        Neuron = neuron,
        At = at,
    };

    private sealed class ScriptedUsageStore : IPersistentState<UsageState>
    {
        public UsageState State { get; set; } = new();
        public string Etag => string.Empty;
        public bool RecordExists => true;
        // The first write stays out until this completes.
        public TaskCompletionSource? Held { get; init; }
        public int Writes { get; private set; }
        public Task ClearStateAsync() => Task.CompletedTask;
        public Task ReadStateAsync() => Task.CompletedTask;
        public Task WriteStateAsync() => ++Writes == 1 && Held is { } held ? held.Task : Task.CompletedTask;
    }
}
