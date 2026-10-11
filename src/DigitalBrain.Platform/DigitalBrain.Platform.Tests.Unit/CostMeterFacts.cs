using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Platform.Compute;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Tests.Unit;

public sealed class CostMeterFacts
{
    [Fact]
    public async Task AnIntegrationCallIsChargedListPlusTheFee()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();

        var cost = await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);

        Assert.Equal(1_250, cost.Micros);
        Assert.Equal(-1_250, await brain.Grains.GetGrain<ILedgerGrain>(meter.AccountId).Balance());
    }

    [Fact]
    public async Task AnOwnConnectionCallIsMeteredAtZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();

        var cost = await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.OwnConnection, "c-1", ct);

        Assert.Equal(0, cost.Micros);
        Assert.Equal(0, await brain.Grains.GetGrain<ILedgerGrain>(meter.AccountId).Balance());
    }

    [Fact]
    public async Task ACallOverBudgetThrowsAndChargesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(400, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var trace = TraceContext.Enter(TraceContext.Begin(meter.AccountId, meter.BrainId, new NeuronId(meter.Key("edge")), budgetMicros: 1_000));
        var costs = Meter(brain);

        var first = await costs.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);
        var second = await costs.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-2", ct);

        Assert.Equal(500, first.Micros);
        Assert.Equal(500, second.Micros);
        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            costs.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-3", ct).AsTask());

        Assert.Equal(-1_000, await brain.Grains.GetGrain<ILedgerGrain>(meter.AccountId).Balance());
        Assert.Equal(2, Assert.Single(await brain.Grains.GetGrain<IUsageGrain>(meter.BrainId).Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10)).Count);
    }

    [Fact]
    public async Task NoCurrentTraceChargesTheCallersBrainUntraced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        // Person.AsCaller opens a trace as the edge does; a caller without one is only expressible as a bare stamp.
        RequestContext.Set(CallerContextStamper.RequestContextKey, new CallerContext
        {
            PrincipalId = meter.PrincipalId,
            AccountId = meter.AccountId,
            BrainId = meter.BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });

        var cost = await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "u-1", ct);

        Assert.Equal(1_250, cost.Micros);
        Assert.Equal(-1_250, await brain.Grains.GetGrain<ILedgerGrain>(meter.AccountId).Balance());
        AssertRow(await brain.Grains.GetGrain<IUsageGrain>(meter.BrainId).Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10), "untraced", 1_250, 1);
    }

    [Fact]
    public async Task TheCostLandsInTheBrainUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();

        await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);

        AssertRow(await brain.Grains.GetGrain<IUsageGrain>(meter.BrainId).Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10), meter.Key("edge"), 1_250, 1);
        AssertRow(await brain.Grains.GetGrain<IUsageGrain>(meter.BrainId).Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 10), "Model", 1_250, 1);
    }

    // The circle's owner funds a share of their balance to the brains in it; the meter is one of them.
    private sealed record Circle(ITestBrain Brain, Person Owner, Person Meter, string Key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            Meter.Dispose();
            await Brain.DisposeAsync();
        }
    }

    private static async Task<Circle> CircleBrain(long price, int percent, TimeProvider? clock, CancellationToken ct)
    {
        var builder = TestBrain.Create()
            // The deployment's tariff has no integration boundary: the price list is the module seam.
            .ConfigureServices(services => services.AddSingleton<IPriceList>(new FixedPriceList(price)));
        if (clock is not null) { builder = builder.ConfigureServices(services => services.AddSingleton(clock)); }
        var brain = await builder.StartAsync(ct);
        var owner = await brain.PersonAsync("circle-owner", ct);
        var meter = await brain.PersonAsync("meter", ct);
        var key = owner.Key("circles/c");
        await brain.Grains.GetGrain<ILedgerGrain>(owner.AccountId).Credit("top-up", 1_000, "test");
        await brain.Grains.GetGrain<ICircleShareGrain>(key).Apply(percent, [owner.BrainId, meter.BrainId]);
        return new(brain, owner, meter, key);
    }

    [Fact]
    public async Task ACircleFundedCallDrawsFromTheOwnersShareAndThrowsOverIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await CircleBrain(100, 20, null, ct);
        using var caller = circle.Meter.AsCaller();
        var meter = Meter(circle.Brain);

        var first = await meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(circle.Key), "c-1", ct);
        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(circle.Key), "c-2", ct).AsTask());

        Assert.Equal(125, first.Micros);
        Assert.Equal(875, await circle.Brain.Grains.GetGrain<ILedgerGrain>(circle.Owner.AccountId).Balance());
        Assert.Equal(0, await circle.Brain.Grains.GetGrain<ILedgerGrain>(circle.Meter.AccountId).Balance());
    }

    [Fact]
    public async Task TheShareIsWhatTheOwnerHasLeftAndStartsOverAtMidnightUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new SettableTime(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        await using var circle = await CircleBrain(100, 20, clock, ct);
        using var caller = circle.Meter.AsCaller();
        var meter = Meter(circle.Brain);

        await meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(circle.Key), "c-1", ct);
        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(circle.Key), "c-2", ct).AsTask());
        clock.Now = clock.Now.AddDays(1);
        var next = await meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(circle.Key), "c-3", ct);

        Assert.Equal(125, next.Micros);
    }

    [Fact]
    public async Task ABrainOutsideTheCircleCannotChargeItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await CircleBrain(100, 25, null, ct);
        using var mallory = await circle.Brain.PersonAsync("mallory", ct);
        using var caller = mallory.AsCaller();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Meter(circle.Brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(circle.Key), "c-1", ct).AsTask());

        Assert.Equal(1_000, await circle.Brain.Grains.GetGrain<ILedgerGrain>(circle.Owner.AccountId).Balance());
    }

    [Fact]
    public async Task ACircleNobodyConfiguredFundsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(100, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();

        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(meter.Key("circles/none")), "c-1", ct).AsTask());
    }

    [Fact]
    public async Task TheCircleCallsBudgetIsCheckedLikeAnIntegrationCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await CircleBrain(100, 25, null, ct);
        var meter = circle.Meter;
        using var trace = TraceContext.Enter(TraceContext.Begin(meter.AccountId, meter.BrainId, new NeuronId(meter.Key("edge")), budgetMicros: 100));

        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            Meter(circle.Brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(circle.Key), "c-1", ct).AsTask());

        Assert.Equal(1_000, await circle.Brain.Grains.GetGrain<ILedgerGrain>(circle.Owner.AccountId).Balance());
    }

    private static Task<ITestBrain> MeteredBrain(long? pricePerUnit, CancellationToken ct)
        => TestBrain.Create()
            // The deployment's tariff has no integration boundary: the price list is the module seam.
            .ConfigureServices(services => services.AddSingleton<IPriceList>(new FixedPriceList(pricePerUnit)))
            .StartAsync(ct);

    private static ICostMeter Meter(ITestBrain brain) => brain.Services.GetRequiredService<ICostMeter>();

    private static void AssertRow(CostRow[] rows, string key, long micros, long count)
    {
        var row = Assert.Single(rows);
        Assert.Equal(key, row.Key);
        Assert.Equal(micros, row.Micros);
        Assert.Equal(count, row.Count);
    }

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedPriceList(long? microsPerUnit) : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => microsPerUnit;
    }
}
