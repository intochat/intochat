using DigitalBrain.Framework.Compute;
using DigitalBrain.Platform.Compute;

namespace DigitalBrain.Platform.Tests.Unit;

public sealed class UsageFacts
{
    private const string BrainId = "brain-usage-1";

    [Fact]
    public async Task UsageGroupsByAppSynapseNeuronAndOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var usage = brain.Grains.GetGrain<IUsageGrain>(BrainId);

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
        var usage = brain.Grains.GetGrain<IUsageGrain>(BrainId);

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
        var usage = brain.Grains.GetGrain<IUsageGrain>(BrainId);
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
        var usage = brain.Grains.GetGrain<IUsageGrain>(BrainId);
        var now = DateTimeOffset.UtcNow;

        await usage.Add(Cost("tr-1", "c-1", 10_000, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge");
        await usage.Add(Cost("tr-1", "c-2", 4_000, "mail", "on-inbound", "gmail/inbox", now), "gmail/edge");
        await brain.DeactivateAsync(usage, ct);

        AssertRow(await usage.Group(UsageGrouping.App, now.AddDays(-1), 10), "mail", 14_000, 2);
        AssertRow(await usage.Group(UsageGrouping.Origin, now.AddDays(-1), 10), "gmail/edge", 14_000, 2);
        var recent = await usage.Recent(10);
        Assert.Equal(2, recent.Length);
        Assert.Equal(4_000, recent[0].Micros);
        Assert.Equal(10_000, recent[1].Micros);
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
}