using System.Collections.Concurrent;
using IntoChat.OS.Authority.Compute;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration;

[Trait("host", "own")]
public sealed class AccountingRecoveryFacts
{
    [Theory]
    [InlineData(0L)]
    [InlineData(null)]
    public async Task LegacyPaidIdentityDoesNotInventAProjectionWhenTheCurrentTariffDisappears(long? currentRate)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ExternalCallFacts.Create(new(currentRate, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var person = await brain.PersonAsync("legacy-rate", ct);
        using var caller = person.AsCaller();
        var legacy = ComputeReservationFacts.Cost(new(TraceContext.Current!.TraceId, "legacy"), 125);
        await brain.Platform.Ledger(person.AccountId).Charge(legacy);
        await brain.Platform.Usage(person.BrainId).Add(legacy, TraceContext.Current!.Origin.ToString());
        await brain.Services.GetRequiredService<ICostMeter>().RecordAsync(CostKind.Model, "test", 1, "call", Funding.Integration, "legacy", ct);
        var total = Assert.Single(await brain.Platform.Usage(person.BrainId).Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 100));
        Assert.Equal(1, total.Count);
        Assert.Equal(125, total.Micros);
        Assert.Equal(-125, await brain.Platform.Ledger(person.AccountId).Balance());
        Assert.Equal(TraceDetailStatus.DetailUnavailable, (await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null)).Status);
    }

    [Fact]
    public async Task LegacyPendingChangesDoNotAlterAnExistingJournalSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FaultingAccountingStorage();
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => storage.With(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        await meter.RecordAsync(CostKind.Model, "tokens", 1, "token", Funding.OwnConnection, "first", ct);
        await meter.RecordAsync(CostKind.Model, "tokens", 1, "token", Funding.OwnConnection, "second", ct);
        var compute = brain.Platform.Compute(person.BrainId);
        var first = await compute.ReadTrace(TraceContext.Current!.TraceId, 1, null);
        Assert.NotNull(first.ContinuationToken);
        storage.Arm("ledger", -1, false);
        await Assert.ThrowsAsync<OrleansException>(() => meter.RecordAsync(CostKind.Model, "tokens", 1, "token", Funding.Integration, "pending", ct).AsTask());
        var second = await compute.ReadTrace(TraceContext.Current!.TraceId, 1, first.ContinuationToken);
        Assert.Equal(first.PendingCount, second.PendingCount);
        Assert.Equal(first.KnownListPriceMicros, second.KnownListPriceMicros);
        Assert.Equal(first.SnapshotWatermark, second.SnapshotWatermark);
        Assert.Equal(TraceDetailStatus.Complete, second.Status);
        var provisional = await compute.ReadTrace(TraceContext.Current!.TraceId, 1, null);
        Assert.Equal(TraceDetailStatus.Incomplete, provisional.Status);
        Assert.Equal(1, provisional.PendingCount);
        Assert.Null(provisional.ContinuationToken);
        storage.Disable();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyLedgerRetryAndBudgetFloorSurviveUpgrade(bool circleFunding, bool externalFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var owner = await brain.PersonAsync("legacy-owner", ct);
        using var member = await brain.PersonAsync("legacy-member", ct);
        var circleKey = owner.Key("circles/upgrade");
        var payer = circleFunding ? owner.AccountId : member.AccountId;
        await brain.Platform.Ledger(payer).Credit("grant", 1_000, "test");
        if (circleFunding) { await brain.Platform.CircleShare(circleKey).Apply(100, [member.AccountId]); }
        using var caller = member.AsCaller();
        using var trace = TraceContext.Enter(TraceContext.Current! with { BudgetMicros = 250 });
        var traceId = TraceContext.Current!.TraceId;
        var funding = circleFunding ? Funding.Circle(circleKey) : Funding.Integration;
        var legacy = ComputeReservationFacts.Cost(new(traceId, "legacy"), 125) with { Item = "tokens", Unit = "token" };
        Assert.True(await brain.Platform.TraceBudget(traceId).TrySpend(125, 250));
        Assert.True(await brain.Platform.Ledger(payer).Charge(legacy));
        await brain.Platform.Usage(member.BrainId).Add(legacy, TraceContext.Current!.Origin.ToString());
        await brain.DeactivateAsync(brain.Platform.TraceBudget(traceId), ct);
        await brain.DeactivateAsync(brain.Platform.Ledger(payer), ct);
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        await meter.RecordAsync(CostKind.Model, "tokens", 1, "token", funding, "legacy", ct);
        if (externalFirst)
        {
            var handle = await meter.BeginExternalCallAsync(ExternalCallFacts.Request("fresh") with { Funding = funding }, ct);
            await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
            await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct);
            await Assert.ThrowsAsync<BudgetExhaustedException>(() => meter.RecordAsync(CostKind.Model, "tokens", 1, "token", funding, "over", ct).AsTask());
        }
        else
        {
            await meter.RecordAsync(CostKind.Model, "tokens", 1, "token", funding, "fresh", ct);
            await Assert.ThrowsAsync<BudgetExhaustedException>(() => meter.BeginExternalCallAsync(ExternalCallFacts.Request("over") with { Funding = funding }, ct).AsTask());
        }
        Assert.Equal(750, await brain.Platform.Ledger(payer).Balance());
        var totals = await brain.Platform.Usage(member.BrainId).Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 100);
        var total = Assert.Single(totals, value => value.Key == CostKind.Model.ToString());
        Assert.Equal(externalFirst ? 1 : 2, total.Count);
    }

    [Fact]
    public async Task LegacyBudgetSpendIsPreservedWithoutReplayingAnyOldCost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt)).StartAsync(ct);
        using var person = await brain.PersonAsync("legacy-budget", ct);
        using var caller = person.AsCaller();
        using var trace = TraceContext.Enter(TraceContext.Current! with { BudgetMicros = 250 });
        var oldBudget = brain.Platform.TraceBudget(TraceContext.Current!.TraceId);
        Assert.True(await oldBudget.TrySpend(200, 250));
        await brain.DeactivateAsync(oldBudget, ct);
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => meter.BeginExternalCallAsync(ExternalCallFacts.Request("external"), ct).AsTask());
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => meter.RecordAsync(CostKind.Model, "tokens", 1, "token", Funding.Integration, "model", ct).AsTask());
        Assert.Equal(0, await brain.Platform.Ledger(person.AccountId).Balance());
    }

    [Fact]
    public async Task PendingOutcomeReplayDoesNotCreateExtraRevisionsWhileProjectionIsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FaultingAccountingStorage();
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.SuccessfulResponse))
            .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => storage.With(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var handle = await meter.BeginExternalCallAsync(ExternalCallFacts.Request("attempt"), ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        storage.Arm("usage", -1, false);
        var outcome = new ExternalCallOutcome(ExternalCallOutcomeKind.TransportFailed);
        await Assert.ThrowsAsync<OrleansException>(() => meter.CompleteExternalCallAsync(handle, outcome, ct).AsTask());
        var first = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        for (var index = 0; index < 3; index++)
        {
            await Assert.ThrowsAsync<OrleansException>(() => meter.CompleteExternalCallAsync(handle, outcome, ct).AsTask());
        }
        storage.Disable();
        var repaired = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Equal(first.SnapshotWatermark, repaired.SnapshotWatermark);
        Assert.Single(repaired.Rows);
        Assert.Equal(1, repaired.PendingCount);
        Assert.Equal(0, repaired.ChargedMicros);
    }

    [Fact]
    public async Task LostInitialLegacyReceiptAcknowledgementReservesBudgetAndCircleFundingBeforeSettlement()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FaultingAccountingStorage();
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => storage.With(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var owner = await brain.PersonAsync("circle-owner", ct);
        using var member = await brain.PersonAsync("member", ct);
        var circleKey = owner.Key("circles/recovery");
        await brain.Platform.Ledger(owner.AccountId).Credit("grant", 1_000, "test");
        await brain.Platform.CircleShare(circleKey).Apply(100, [member.AccountId]);
        using var caller = member.AsCaller();
        using var trace = TraceContext.Enter(TraceContext.Current! with { BudgetMicros = 125 });
        var compute = brain.Platform.Compute(member.BrainId);
        await compute.Reconcile();
        storage.Arm("brain-compute", 2, true);
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        await Assert.ThrowsAsync<OrleansException>(() => meter.RecordAsync(CostKind.Model, "tokens", 1, "token",
            Funding.Circle(circleKey), "model", ct).AsTask());
        Assert.True(storage.Triggered);
        await brain.DeactivateAsync(compute, ct);
        var page = await compute.ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Equal(125, page.ChargedMicros);
        Assert.Equal(875, await brain.Platform.Ledger(owner.AccountId).Balance());
        Assert.Equal(0, await brain.Platform.Ledger(member.AccountId).Balance());
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => meter.RecordAsync(CostKind.Model, "tokens", 1, "token",
            Funding.Circle(circleKey), "next", ct).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsettledLegacyCostIsPendingAndDoesNotClaimAChargeInTraceDetail(bool hasPriorRow)
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FaultingAccountingStorage();
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => storage.With(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        if (hasPriorRow)
        {
            await brain.Services.GetRequiredService<ICostMeter>()
                .RecordAsync(CostKind.Model, "tokens", 1, "token", Funding.OwnConnection, "prior", ct);
        }
        storage.Arm("ledger", -1, false);
        await Assert.ThrowsAsync<OrleansException>(() => brain.Services.GetRequiredService<ICostMeter>()
            .RecordAsync(CostKind.Model, "tokens", 1, "token", Funding.Integration, "model", ct).AsTask());
        var page = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Equal(TraceDetailStatus.Incomplete, page.Status);
        Assert.Equal(1, page.PendingCount);
        Assert.Equal(0, page.ChargedMicros);
        var row = Assert.Single(page.Rows);
        Assert.Equal(0, row.Micros);
        if (!hasPriorRow)
        {
            Assert.Null(row.ListPriceMicros);
            Assert.Equal(100, row.EstimatedListPriceMicros);
        }
        storage.Disable();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedAdmissionRepairsTheSameBudgetHold(bool afterCommit)
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FaultingAccountingStorage();
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => storage.With(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        using var trace = TraceContext.Enter(TraceContext.Current! with { BudgetMicros = 125 });
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        storage.Arm("trace-budget", 1, afterCommit);
        await Assert.ThrowsAsync<OrleansException>(() => meter.BeginExternalCallAsync(ExternalCallFacts.Request("attempt"), ct).AsTask());
        await brain.DeactivateAsync(brain.Platform.Compute(person.BrainId), ct);
        await brain.DeactivateAsync(brain.Platform.TraceBudget(person.BrainId, TraceContext.Current!.TraceId), ct);
        var handle = await meter.BeginExternalCallAsync(ExternalCallFacts.Request("attempt"), ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        var result = await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct);
        Assert.Equal(125, result.Cost!.Micros);
        Assert.Equal(-125, await brain.Platform.Ledger(person.AccountId).Balance());
        Assert.Single((await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null)).Rows);
    }

    [Fact]
    public async Task UnavailableProjectionIsVisibleUntilDurableRepairFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FaultingAccountingStorage();
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => storage.With(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var handle = await meter.BeginExternalCallAsync(ExternalCallFacts.Request("attempt"), ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        storage.Arm("usage", -1, false);
        await Assert.ThrowsAsync<OrleansException>(() => meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct).AsTask());
        var pending = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Equal(TraceDetailStatus.Incomplete, pending.Status);
        Assert.Equal(-125, await brain.Platform.Ledger(person.AccountId).Balance());
        storage.Disable();
        var repaired = await brain.Platform.Compute(person.BrainId).ReadTrace(TraceContext.Current!.TraceId, 100, null);
        Assert.Equal(TraceDetailStatus.Complete, repaired.Status);
        Assert.Equal(125, repaired.ChargedMicros);
        Assert.Equal(0, repaired.PendingCount);
        Assert.Single(repaired.Rows);
    }

    [Theory]
    [InlineData("brain-compute", 1, false)]
    [InlineData("brain-compute", 2, false)]
    [InlineData("brain-compute", 3, false)]
    [InlineData("brain-compute", 4, false)]
    [InlineData("brain-compute", 5, false)]
    [InlineData("brain-compute", 6, false)]
    [InlineData("brain-compute", 7, false)]
    [InlineData("brain-compute", 8, false)]
    [InlineData("brain-compute", 9, false)]
    [InlineData("brain-compute", 6, true)]
    [InlineData("ledger", 1, true)]
    [InlineData("usage", 1, true)]
    [InlineData("compute-trace-journal", 1, true)]
    public async Task InterruptedSettlementReplaysAccountingWithoutChargingAgain(string stateName, int write, bool afterCommit)
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new FaultingAccountingStorage();
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
                (provider, _) => storage.With(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var person = await brain.PersonAsync("meter", ct);
        using var caller = person.AsCaller();
        var traceId = TraceContext.Current!.TraceId;
        var meter = brain.Services.GetRequiredService<ICostMeter>();
        var handle = await meter.BeginExternalCallAsync(ExternalCallFacts.Request("attempt"), ct);
        await meter.MarkExternalCallDispatchStartedAsync(handle, ct);
        storage.Arm(stateName, write, afterCommit);
        var failure = await Assert.ThrowsAsync<OrleansException>(() => meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct).AsTask());
        Assert.IsType<IOException>(failure.InnerException);
        Assert.True(storage.Triggered);
        await brain.DeactivateAsync(brain.Platform.Compute(person.BrainId), ct);
        await brain.DeactivateAsync(brain.Platform.Ledger(person.AccountId), ct);
        var completion = await meter.CompleteExternalCallAsync(handle, new(ExternalCallOutcomeKind.Succeeded, 200), ct);
        Assert.Equal(ExternalAccountingStatus.Settled, completion.Status);
        Assert.Equal(-125, await brain.Platform.Ledger(person.AccountId).Balance());
        var page = await brain.Platform.Compute(person.BrainId).ReadTrace(traceId, 100, null);
        Assert.Single(page.Rows);
        Assert.Equal(125, page.ChargedMicros);
        Assert.Equal(0, page.PendingCount);
        var total = Assert.Single(await brain.Platform.Usage(person.BrainId).Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 100));
        Assert.Equal(1, total.Count);
        Assert.Equal(125, total.Micros);
    }

    internal sealed class FaultingAccountingStorage : IGrainStorage
    {
        private readonly ConcurrentDictionary<(string, GrainId), byte[]> _records = new();
        private Serializer? _serializer;
        private string? _stateName;
        private int _remaining;
        private bool _afterCommit;
        private bool _persistent;
        public bool Triggered { get; private set; }
        public FaultingAccountingStorage With(Serializer serializer) { _serializer = serializer; return this; }
        public void Arm(string stateName, int write, bool afterCommit)
        { _stateName = stateName; _remaining = write; _persistent = write < 0; _afterCommit = afterCommit; Triggered = false; }
        public void Disable() => _stateName = null;

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            if (_records.TryGetValue((stateName, grainId), out var bytes))
            {
                grainState.State = _serializer!.Deserialize<T>(bytes);
                (grainState.ETag, grainState.RecordExists) = ("stored", true);
            }
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var fail = stateName == _stateName && (_persistent || _remaining > 0 && Interlocked.Decrement(ref _remaining) == 0);
            if (fail) { Triggered = true; }
            if (fail && !_afterCommit) { throw new IOException("Injected accounting write failure."); }
            _records[(stateName, grainId)] = _serializer!.SerializeToArray(grainState.State);
            (grainState.ETag, grainState.RecordExists) = ("stored", true);
            if (fail) { throw new IOException("Injected lost accounting acknowledgement."); }
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _records.TryRemove((stateName, grainId), out _);
            (grainState.ETag, grainState.RecordExists) = (null, false);
            return Task.CompletedTask;
        }
    }
}
