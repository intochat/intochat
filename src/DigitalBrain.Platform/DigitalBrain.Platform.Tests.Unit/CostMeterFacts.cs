using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Platform.Compute;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Tests.Unit;

public sealed class CostMeterFacts
{
    private const string Account = "acct-meter";
    private const string BrainId = "brain-meter";

    [Fact]
    public async Task AnIntegrationCallIsChargedListPlusTheFee()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        StampCaller();

        var cost = await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);

        Assert.Equal(1_250, cost.Micros);
        Assert.Equal(-1_250, await brain.Grains.GetGrain<ILedgerGrain>(Account).Balance());
    }

    [Fact]
    public async Task AnOwnConnectionCallIsMeteredAtZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        StampCaller();

        var cost = await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.OwnConnection, "c-1", ct);

        Assert.Equal(0, cost.Micros);
        Assert.Equal(0, await brain.Grains.GetGrain<ILedgerGrain>(Account).Balance());
    }

    [Fact]
    public async Task ACallOverBudgetThrowsAndChargesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(400, ct);
        using var trace = TraceContext.Enter(TraceContext.Begin(Account, BrainId, new NeuronId($"{BrainId}/edge"), budgetMicros: 1_000));
        var meter = Meter(brain);

        var first = await meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);
        var second = await meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-2", ct);

        Assert.Equal(500, first.Micros);
        Assert.Equal(500, second.Micros);
        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-3", ct).AsTask());

        Assert.Equal(-1_000, await brain.Grains.GetGrain<ILedgerGrain>(Account).Balance());
        Assert.Equal(2, Assert.Single(await brain.Grains.GetGrain<IUsageGrain>(BrainId).Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10)).Count);
    }

    [Fact]
    public async Task NoCurrentTraceChargesTheCallersBrainUntraced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        RequestContext.Set(CallerContextStamper.RequestContextKey, new CallerContext
        {
            PrincipalId = Account,
            AccountId = Account,
            BrainId = BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });

        var cost = await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "u-1", ct);

        Assert.Equal(1_250, cost.Micros);
        Assert.Equal(-1_250, await brain.Grains.GetGrain<ILedgerGrain>(Account).Balance());
        AssertRow(await brain.Grains.GetGrain<IUsageGrain>(BrainId).Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10), "untraced", 1_250, 1);
    }

    [Fact]
    public async Task TheCostLandsInTheBrainUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        StampCaller();

        await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);

        AssertRow(await brain.Grains.GetGrain<IUsageGrain>(BrainId).Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10), "brain-meter/edge", 1_250, 1);
        AssertRow(await brain.Grains.GetGrain<IUsageGrain>(BrainId).Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 10), "Model", 1_250, 1);
    }

    [Fact]
    public async Task ACircleFundedCallDrawsFromTheOwnersShareAndThrowsOverIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(100, ct);
        await brain.Grains.GetGrain<ILedgerGrain>("acct-owner").Credit("top-up", 1_000, "test");
        await brain.SiloServices.GetRequiredService<ICircleShares>().Configure("owner/circles/c", "acct-owner", 20);
        StampCaller();
        var meter = Meter(brain);

        var first = await meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle("owner/circles/c"), "c-1", ct);
        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle("owner/circles/c"), "c-2", ct).AsTask());

        Assert.Equal(125, first.Micros);
        Assert.Equal(875, await brain.Grains.GetGrain<ILedgerGrain>("acct-owner").Balance());
        Assert.Equal(0, await brain.Grains.GetGrain<ILedgerGrain>(Account).Balance());
    }

    [Fact]
    public async Task ACircleWithNoShareFundsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(100, ct);
        await brain.Grains.GetGrain<ILedgerGrain>("acct-owner").Credit("top-up", 1_000, "test");
        StampCaller();

        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle("owner/circles/none"), "c-1", ct).AsTask());
    }

    private static Task<ModuleBrain> MeteredBrain(long? pricePerUnit, CancellationToken ct)
        => ModuleTest.Create()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IPriceList>(new FixedPriceList(pricePerUnit)))
            .StartAsync(ct);

    private static ICostMeter Meter(ModuleBrain brain) => brain.SiloServices.GetRequiredService<ICostMeter>();

    private static void StampCaller() => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = Account,
        AccountId = Account,
        BrainId = BrainId,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    private static void AssertRow(CostRow[] rows, string key, long micros, long count)
    {
        var row = Assert.Single(rows);
        Assert.Equal(key, row.Key);
        Assert.Equal(micros, row.Micros);
        Assert.Equal(count, row.Count);
    }

    private sealed class FixedPriceList(long? microsPerUnit) : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => microsPerUnit;
    }
}