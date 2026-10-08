using DigitalBrain.Compute;
using DigitalBrain.Compute.Allowances;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Modules.Compute.Tests;

public sealed class AllowancePersistenceFacts
{
    [Fact]
    public async Task UnchangedAuthorizationsDoNotWriteTheLedger()
    {
        var ct = TestContext.Current.CancellationToken;
        CountingStorage? storage = null;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>()
            .ConfigureSilo(silo =>
            {
                silo.AddMemoryGrainStorage("observed-allowances");
                silo.Services.AddKeyedSingleton<IGrainStorage>("Default", (services, _) =>
                    storage = new CountingStorage(services.GetRequiredKeyedService<IGrainStorage>("observed-allowances")));
            }).StartAsync(ct);
        var ledger = brain.Get<IAllowanceLedger>("account");
        var read = Request(0m);
        Assert.True((await ledger.AuthorizeAsync(read, ct)).Allowed);
        Assert.True((await ledger.AuthorizeAsync(read, ct)).Allowed);
        Assert.Equal(0, storage!.Writes);

        var paid = Request(5m);
        Assert.Equal(AllowanceDenial.MissingAllowance, (await ledger.AuthorizeAsync(paid, ct)).Denial);
        Assert.Equal(1, storage.Writes);
        Assert.Equal(AllowanceDenial.MissingAllowance, (await ledger.AuthorizeAsync(paid, ct)).Denial);
        Assert.Equal(1, storage.Writes);
        await brain.DeactivateAsync(ledger, ct);
        Assert.Single(await ledger.ListPendingAsync(ct));
    }

    [Fact]
    public async Task AFailedApprovalWriteIsRetriedBeforeReturningTheDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        CountingStorage? storage = null;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>()
            .ConfigureSilo(silo =>
            {
                silo.AddMemoryGrainStorage("observed-allowances");
                silo.Services.AddKeyedSingleton<IGrainStorage>("Default", (services, _) =>
                    storage = new CountingStorage(services.GetRequiredKeyedService<IGrainStorage>("observed-allowances")));
            }).StartAsync(ct);
        var ledger = brain.Get<IAllowanceLedger>("account");
        await ledger.ReadReservedAsync(cancellationToken: ct);
        storage!.FailNextWrite = true;
        var error = await Assert.ThrowsAsync<OrleansException>(() => ledger.AuthorizeAsync(Request(5m), ct));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(AllowanceDenial.MissingAllowance, (await ledger.AuthorizeAsync(Request(5m), ct)).Denial);
        await brain.DeactivateAsync(ledger, ct);
        Assert.Single(await ledger.ListPendingAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationsAndHardStopsStillPersistBeforeAuthorizationReturns(bool failFirstWrite)
    {
        var ct = TestContext.Current.CancellationToken;
        CountingStorage? storage = null;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>()
            .ConfigureSilo(silo =>
            {
                silo.AddMemoryGrainStorage("observed-allowances");
                silo.Services.AddKeyedSingleton<IGrainStorage>("Default", (services, _) =>
                    storage = new CountingStorage(services.GetRequiredKeyedService<IGrainStorage>("observed-allowances")));
            }).StartAsync(ct);
        var ledger = brain.Get<IAllowanceLedger>("account");
        await ledger.GrantAsync(new Allowance
        {
            AllowanceId = "budget",
            AccountId = "account",
            BrainId = "brain",
            AppId = "app",
            Level = ApprovalLevel.StandingBudget,
            Scope = AllowanceScope.Always,
            LimitCompute = 10m,
            PriceBookVersion = new PriceBook().Version,
        }, ct);
        await ledger.SetLimitsAsync(new LimitPolicy { AccountLimitCompute = 10m }, ct);
        DateTimeOffset? originalHardStop = null;
        if (failFirstWrite)
        {
            storage!.FailNextWrite = true;
            var error = await Assert.ThrowsAsync<OrleansException>(() => ledger.AuthorizeAsync(Request(10m), ct));
            Assert.IsType<IOException>(error.InnerException);
            originalHardStop = (await ledger.ReadLimitsAsync(ct)).HardStoppedAt;
            Assert.NotNull(originalHardStop);
        }
        Assert.True((await ledger.AuthorizeAsync(Request(10m), ct)).Allowed);
        Assert.True((await ledger.AuthorizeAsync(Request(0m) with { Operation = "Read" }, ct)).Allowed);
        await brain.DeactivateAsync(ledger, ct);
        Assert.Equal(10m, await ledger.ReadReservedAsync(cancellationToken: ct));
        var limits = await ledger.ReadLimitsAsync(ct);
        Assert.Equal(LimitAlert.At100, limits.HighestAlert);
        Assert.NotNull(limits.HardStoppedAt);
        if (failFirstWrite) { Assert.Equal(originalHardStop, limits.HardStoppedAt); }
        Assert.Equal(limits.HardStoppedAt + TimeSpan.FromDays(30), limits.ReadExportGraceUntil);
    }

    [Fact]
    public async Task AuthorizationFlushesAFailedSettlementBeforeReturningAnUnchangedDecision()
    {
        var ct = TestContext.Current.CancellationToken;
        CountingStorage? storage = null;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>()
            .ConfigureSilo(silo =>
            {
                silo.AddMemoryGrainStorage("observed-allowances");
                silo.Services.AddKeyedSingleton<IGrainStorage>("Default", (services, _) =>
                    storage = new CountingStorage(services.GetRequiredKeyedService<IGrainStorage>("observed-allowances")));
            }).StartAsync(ct);
        var ledger = brain.Get<IAllowanceLedger>("account");
        await ledger.GrantAsync(new Allowance
        {
            AllowanceId = "budget",
            AccountId = "account",
            BrainId = "brain",
            AppId = "app",
            Level = ApprovalLevel.StandingBudget,
            Scope = AllowanceScope.Always,
            LimitCompute = 100m,
            PriceBookVersion = new PriceBook().Version,
        }, ct);
        await ledger.SetLimitsAsync(new LimitPolicy { AccountLimitCompute = 10m }, ct);
        var authorized = await ledger.AuthorizeAsync(Request(5m), ct);
        storage!.FailNextWrite = true;
        var error = await Assert.ThrowsAsync<OrleansException>(() => ledger.SettleAsync(authorized.ReservationId!, 6m, FailureClass.None, ct));
        Assert.IsType<IOException>(error.InnerException);
        Assert.True((await ledger.AuthorizeAsync(Request(0m) with { Operation = "Read" }, ct)).Allowed);
        await brain.DeactivateAsync(ledger, ct);
        Assert.Equal(6m, await ledger.ReadSettledAsync(cancellationToken: ct));
    }

    private static AllowanceRequest Request(decimal compute) =>
        new("account", "brain", "app", "intent", AllowanceCallerKind.App,
            "test/target", "Execute", [], compute, compute > 0);

    private sealed class CountingStorage(IGrainStorage inner) : IGrainStorage
    {
        public int Writes { get; private set; }
        public bool FailNextWrite { get; set; }
        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> state)
            => inner.ReadStateAsync(stateName, grainId, state);
        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> state)
            => inner.ClearStateAsync(stateName, grainId, state);
        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> state)
        {
            if (typeof(T) == typeof(AllowanceLedgerState))
            {
                Writes++;
                if (FailNextWrite) { FailNextWrite = false; throw new IOException("Injected ledger write failure."); }
            }
            return inner.WriteStateAsync(stateName, grainId, state);
        }
    }
}
