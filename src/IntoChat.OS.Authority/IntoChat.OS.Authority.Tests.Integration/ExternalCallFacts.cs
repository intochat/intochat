using IntoChat.OS.Authority.Compute;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Sdk.Compute;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Sdk.Platform.Identity;

namespace IntoChat.OS.Authority.Tests.Integration;

[Trait("host", "own")]
public sealed class ExternalCallFacts
{
    [Theory]
    [InlineData(ExternalChargePolicy.EveryDispatchedAttempt, ExternalCallOutcomeKind.HttpFailed, 125)]
    [InlineData(ExternalChargePolicy.SuccessfulResponse, ExternalCallOutcomeKind.HttpFailed, 0)]
    [InlineData(ExternalChargePolicy.SuccessfulResponse, ExternalCallOutcomeKind.Succeeded, 125)]
    public async Task OutcomePricingSettlesOnceAfterTheAmbientTraceEnds(ExternalChargePolicy policy, ExternalCallOutcomeKind outcome, long expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var prices = new ExternalPrices(100, policy);
        await using var brain = await Create(prices).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        ExternalCallHandle handle;
        string traceId;
        using (person.AsCaller())
        {
            traceId = TraceContext.Current!.TraceId;
            handle = await meter.BeginExternalCallAsync(Request("attempt"), ct);
            Assert.Equal(handle, await meter.BeginExternalCallAsync(Request("attempt"), ct));
            await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        }
        prices.Rate = 9_999;
        var result = await meter.CompleteExternalCallAsync(handle, new(outcome, outcome == ExternalCallOutcomeKind.Succeeded ? 200 : 503), ct);
        Assert.Equal(ExternalAccountingStatus.Settled, result.Status);
        Assert.Equal(expected, result.Cost!.Micros);
        Assert.Equal(result, await meter.CompleteExternalCallAsync(handle, new(outcome, outcome == ExternalCallOutcomeKind.Succeeded ? 200 : 503), ct));
        Assert.Equal(-expected, await brain.Platform.Ledger(person.AccountId).Balance());
        var page = await brain.Platform.Compute(person.BrainId).ReadTrace(traceId, 100, null);
        Assert.Single(page.Rows);
        Assert.Equal(expected, page.ChargedMicros);
        Assert.Equal(0, page.PendingCount);
    }

    [Fact]
    public async Task PendingAdmissionsReserveTheFeeInclusiveBudgetBeforeDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(100, ExternalChargePolicy.SuccessfulResponse)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        using var trace = TraceContext.Enter(TraceContext.Current! with { BudgetMicros = 125 });
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var handle = await meter.BeginExternalCallAsync(Request("first"), ct);
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => meter.BeginExternalCallAsync(Request("second"), ct).AsTask());
        await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.CancelledBeforeDispatch), ct);
        var next = await meter.BeginExternalCallAsync(Request("third"), ct);
        Assert.NotEqual(handle, next);
        Assert.Equal(0, await brain.Platform.Ledger(person.AccountId).Balance());
    }

    [Fact]
    public async Task UnknownPriceIsNotFreeAndPersonalUsageRemainsUnpriced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(null, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.BeginExternalCallAsync(Request("deployment"), ct).AsTask());
        var handle = await meter.BeginExternalCallAsync(Request("personal") with { Funding = Funding.OwnConnection }, ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        var result = await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct);
        Assert.Equal(PricingStatus.Unpriced, result.Cost!.PricingStatus);
        Assert.Null(result.Cost.ListPriceMicros);
        Assert.Equal(0, result.Cost.Micros);
    }

    [Fact]
    public async Task InterruptedDispatchRetainsPendingExposureAndCannotBecomeNoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(100, ExternalChargePolicy.SuccessfulResponse)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var handle = await meter.BeginExternalCallAsync(Request("uncertain"), ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        var result = await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.TransportFailed), ct);
        Assert.Equal(ExternalAccountingStatus.Pending, result.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.CancelledBeforeDispatch), ct).AsTask());
        await brain.DeactivateAsync(brain.Platform.Compute(person.BrainId), ct);
        var page = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Equal(1, page.PendingCount);
        Assert.Equal(0, page.ChargedMicros);
        Assert.Equal(ExternalCallState.Uncertain, Assert.Single(page.Rows).AttemptState);
    }

    [Fact]
    public async Task OneHundredUnresolvedAttemptsBlockTheNextBeforeDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(0, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        for (var index = 0; index < 100; index++)
        { await meter.BeginExternalCallAsync(Request(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.BeginExternalCallAsync(Request("overflow"), ct).AsTask());
        await brain.DeactivateAsync(brain.Platform.Compute(person.BrainId), ct);
        var page = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 500, null);
        Assert.Equal(100, page.PendingCount);
        Assert.Equal(100, page.Rows.Length);
        Assert.All(page.Rows, cost => Assert.Equal(PricingStatus.Known, cost.PricingStatus));
    }

    [Fact]
    public async Task BoundedUnpricedPolicyLimitsAttemptsWithoutInventingMoney()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(null, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.Configure<ComputeOptions>(value => value.AllowedUnpricedOperations = [new("test.read", 1)]))
            .StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var handle = await meter.BeginExternalCallAsync(Request("first"), ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        var completion = await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct);
        Assert.Null(completion.Cost!.ListPriceMicros);
        Assert.Equal(0, completion.Cost.Micros);
        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.BeginExternalCallAsync(Request("second"), ct).AsTask());
    }

    [Fact]
    public async Task InvalidQuantitiesUnitsAndOverflowCannotAdmitAnAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(long.MaxValue, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        await Assert.ThrowsAsync<ArgumentException>(() => meter.BeginExternalCallAsync(Request("zero") with { Quantity = 0 }, ct).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => meter.BeginExternalCallAsync(Request("negative") with { Quantity = -1 }, ct).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => meter.BeginExternalCallAsync(Request("unit") with { Unit = "token" }, ct).AsTask());
        await Assert.ThrowsAsync<OverflowException>(() => meter.BeginExternalCallAsync(Request("overflow"), ct).AsTask());
        var page = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task PersonalZeroChargeReplayStillCountsUsageOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var first = await meter.RecordAsync(CostKind.Model, "test", 1, "token", Funding.OwnConnection, "same", ct);
        Assert.Equal(first, await meter.RecordAsync(CostKind.Model, "test", 1, "token", Funding.OwnConnection, "same", ct));
        var total = Assert.Single(await brain.Platform.Usage(person.BrainId).Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 100));
        Assert.Equal(1, total.Count);
        Assert.Equal(0, total.Micros);
        Assert.Equal(100, total.KnownListPriceMicros);
    }

    [Fact]
    public async Task RecordedCostReplayKeepsItsPinnedPriceAfterATariffChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var prices = new ExternalPrices(100, ExternalChargePolicy.EveryDispatchedAttempt);
        await using var brain = await Create(prices).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var first = await meter.RecordAsync(CostKind.Model, "test", 1, "token", Funding.Integration, "same", ct);
        prices.Rate = 200;
        await brain.DeactivateAsync(brain.Platform.Compute(person.BrainId), ct);
        Assert.Equal(first, await meter.RecordAsync(CostKind.Model, "test", 1, "token", Funding.Integration, "same", ct));
        Assert.Equal(-125, await brain.Platform.Ledger(person.AccountId).Balance());
        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.RecordAsync(CostKind.Model, "test", 2, "token", Funding.Integration, "same", ct).AsTask());
        Assert.Equal(1, Assert.Single(await brain.Platform.Usage(person.BrainId).Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 100)).Count);
    }

    [Fact]
    public async Task UncertainObservationMayReplayOrBecomeDefiniteButCannotBeRewritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(100, ExternalChargePolicy.SuccessfulResponse)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var handle = await meter.BeginExternalCallAsync(Request("uncertain"), ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        var observation = new ExternalCallOutcome(ExternalCallOutcomeKind.TransportFailed, ProviderRequestId: "first");
        await meter.CompleteExternalCallAsync(handle, observation, ct);
        var first = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        await meter.CompleteExternalCallAsync(handle, observation, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.CompleteExternalCallAsync(handle,
            observation with { ProviderRequestId = "different" }, ct).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.CompleteExternalCallAsync(handle,
            new(ExternalCallOutcomeKind.CancelledAfterDispatch), ct).AsTask());
        Assert.Equal(first.SnapshotWatermark, (await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null)).SnapshotWatermark);
        await brain.DeactivateAsync(brain.Platform.Compute(person.BrainId), ct);
        var settled = await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct);
        Assert.Equal(ExternalAccountingStatus.Settled, settled.Status);
        Assert.Equal(125, settled.Cost!.Micros);
        Assert.Equal(-125, await brain.Platform.Ledger(person.AccountId).Balance());
    }

    [Fact]
    public async Task AnExplicitTraceCannotEstablishTheCallerOrChooseAnotherPayer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        using var caller = person.AsCaller();
        var stamp = CallerContextStamper.Require();
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            await Assert.ThrowsAsync<UntrustedCallerException>(() => meter.BeginExternalCallAsync(Request("no-caller"), ct).AsTask());
        }
        finally { RequestContext.Set(CallerContextStamper.RequestContextKey, stamp); }
        using (TraceContext.Enter(TraceContext.Current! with { PayerAccountId = "someone-else" }))
        {
            await Assert.ThrowsAsync<UntrustedCallerException>(() => meter.BeginExternalCallAsync(Request("wrong-payer"), ct).AsTask());
        }
        using (TraceContext.Enter(TraceContext.Current! with { BrainId = "someone-else" }))
        {
            await Assert.ThrowsAsync<UntrustedCallerException>(() => meter.BeginExternalCallAsync(Request("wrong-brain"), ct).AsTask());
        }
        var page = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task TwoBrainsSharingATraceAndPayerKeepIndependentBudgetAndAttemptIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var owner = await brain.PersonAsync("shared-owner", ct);
        using var payer = await brain.PersonAsync("shared-payer", ct);
        await brain.Platform.Directory().ShareBrainAsync(owner.AccountId, owner.BrainId, payer.PrincipalId, "Payer", MemberRole.Member, ct);
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        foreach (var brainId in new[] { owner.BrainId, payer.BrainId })
        {
            using var caller = payer.AsCallerIn(brainId);
            using var trace = TraceContext.Enter(TraceContext.Current! with { TraceId = "shared-trace", BudgetMicros = 125 });
            var handle = await meter.BeginExternalCallAsync(Request("same-attempt"), ct);
            await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
            await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct);
            Assert.Single((await brain.Platform.Compute(brainId).ReadTrace("shared-trace", 100, null)).Rows);
        }
        Assert.Equal(-250, await brain.Platform.Ledger(payer.AccountId).Balance());
    }

    internal static TestBrain Create(ExternalPrices prices) => TestBrain.Create().ConfigureServices(services =>
    {
        services.AddSingleton(prices);
        services.AddSingleton<IPriceList>(prices);
        services.AddSingleton(new ModulePriceListRegistration("test", typeof(ExternalPrices), [new("test.read", "test-api", "call")]));
        services.AddSingleton<ModulePriceListRegistry>();
    });

    internal static ExternalCallRequest Request(string id) => new()
    {
        Operation = "test.read",
        Quantity = 1,
        Unit = "call",
        Funding = Funding.Integration,
        InvocationId = "invocation",
        CostId = id,
    };

    internal sealed class ExternalPrices(long? rate, ExternalChargePolicy policy) : IPriceList
    {
        public long? Rate { get; set; } = rate;
        public long? MicrosPerUnit(CostKind kind, string item) => Rate;
        public PriceQuote Quote(CostKind kind, string item, string unit) => new()
        { Unit = unit, MicrosPerUnit = Rate, ChargePolicy = policy, TariffVersion = "test-v1" };
    }
}
