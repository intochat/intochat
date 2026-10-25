using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Authority.Compute;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration;

[Trait("host", "own")]
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
        Assert.Equal(-1_250, await brain.Platform.Ledger(meter.AccountId).Balance());
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
        Assert.Equal(0, await brain.Platform.Ledger(meter.AccountId).Balance());
    }

    [Fact]
    public async Task ACallOverBudgetThrowsAndChargesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(400, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var trace = TraceContext.Enter(TraceContext.Begin(meter.AccountId, meter.BrainId, new NeuronId(meter.Key("edge")), budgetMicros: 1_000));
        var costs = Meter(brain);
        var usage = await ActiveUsage(brain, meter.BrainId);

        var first = await costs.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);
        var second = await costs.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-2", ct);

        Assert.Equal(500, first.Micros);
        Assert.Equal(500, second.Micros);
        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            costs.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-3", ct).AsTask());

        Assert.Equal(-1_000, await brain.Platform.Ledger(meter.AccountId).Balance());
        Assert.Equal(2, Assert.Single(await usage.Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10)).Count);
    }

    [Fact]
    public async Task NoCurrentTraceChargesTheCallersBrainUntraced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        var usage = await ActiveUsage(brain, meter.BrainId);
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
        Assert.Equal(-1_250, await brain.Platform.Ledger(meter.AccountId).Balance());
        AssertRow(await usage.Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10), "untraced", 1_250, 1);
    }

    [Fact]
    public async Task TheCostLandsInTheBrainUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();
        var usage = await ActiveUsage(brain, meter.BrainId);

        await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-1", ct);

        AssertRow(await usage.Group(UsageGrouping.Origin, DateTimeOffset.UtcNow.AddDays(-1), 10), meter.Key("edge"), 1_250, 1);
        AssertRow(await usage.Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 10), "Model", 1_250, 1);
    }

    [Fact]
    public async Task ACostKnowsTheSynapseAppAndNeuronItWasSpentOn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();
        var synapse = meter.Key("apps/x/synapses/a.cs");
        var usage = await ActiveUsage(brain, meter.BrainId);

        var cost = await Meter(brain).RecordAsync(CostKind.Script, "cpu-ms", 1m, "ms", Funding.Integration, "s-1", ct,
            new CostSubject(Synapse: synapse, App: meter.Key("apps/x"), Neuron: meter.Key("timer")));

        Assert.Equal((synapse, meter.Key("apps/x"), meter.Key("timer")), (cost.Synapse, cost.App, cost.Neuron));
        AssertRow(await usage.Group(UsageGrouping.Synapse, DateTimeOffset.UtcNow.AddDays(-1), 10), synapse, 1_250, 1);
        AssertRow(await usage.Group(UsageGrouping.App, DateTimeOffset.UtcNow.AddDays(-1), 10), meter.Key("apps/x"), 1_250, 1);
    }

    // Accounting acknowledgement waits for the durable usage projection. A held write must not be reported as complete.
    [Fact(Timeout = 60_000)]
    public async Task ACostWaitsForItsDurableUsageAcknowledgement()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new HeldUsageWrites();
        await using var brain = await TestBrain.Create()
            .ConfigureServices(services => services
                .AddSingleton<IPriceList>(new FixedPriceList(1_000))
                .AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage, (provider, _) => storage.Over(provider.GetRequiredService<Serializer>())))
            .StartAsync(ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();
        var usage = await ActiveUsage(brain, meter.BrainId);
        storage.HoldNextUsageWrite();
        var recording = Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, "c-0", ct).AsTask();
        try
        {
            await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.False(recording.IsCompleted);
        }
        finally { storage.Release(); }
        await recording;
        await brain.DeactivateAsync(usage, ct);
        AssertRow(await usage.Group(UsageGrouping.Kind, DateTimeOffset.UtcNow.AddDays(-1), 10), "Model", 1_250, 1);
        Assert.Equal("c-0", Assert.Single(await usage.Recent(10)).CostId);
    }

    [Fact]
    public async Task TheRecentEndpointServesTheLatestCostsNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await MeteredBrain(1_000, ct);
        using var meter = await brain.PersonAsync("meter", ct);
        using var caller = meter.AsCaller();
        await ActiveUsage(brain, meter.BrainId);

        for (var i = 0; i < 3; i++) { await Meter(brain).RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Integration, $"c-{i}", ct); }

        var recent = await meter.Http.GetFromJsonAsync<JsonElement>($"/brains/{meter.BrainId}/compute/recent?take=2", ct);
        Assert.Equal(["c-2", "c-1"], recent.EnumerateArray().Select(cost => cost.GetProperty("costId").GetString()));
        Assert.All(recent.EnumerateArray(), cost => Assert.Equal(1_250, cost.GetProperty("micros").GetInt64()));
    }

    // The circle's creator funds a share of their balance to the member accounts; the meter is one of them.
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
        await brain.Platform.Ledger(owner.AccountId).Credit("top-up", 1_000, "test");
        await brain.Platform.CircleShare(key).Apply(percent, [owner.AccountId, meter.AccountId]);
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
        Assert.Equal(875, await circle.Brain.Platform.Ledger(circle.Owner.AccountId).Balance());
        Assert.Equal(0, await circle.Brain.Platform.Ledger(circle.Meter.AccountId).Balance());
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

        Assert.Equal(1_000, await circle.Brain.Platform.Ledger(circle.Owner.AccountId).Balance());
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

        Assert.Equal(1_000, await circle.Brain.Platform.Ledger(circle.Owner.AccountId).Balance());
    }

    private static Task<ITestBrain> MeteredBrain(long? pricePerUnit, CancellationToken ct)
        => TestBrain.Create()
            // The deployment's tariff has no integration boundary: the price list is the module seam.
            .ConfigureServices(services => services.AddSingleton<IPriceList>(new FixedPriceList(pricePerUnit)))
            .StartAsync(ct);

    private static ICostMeter Meter(ITestBrain brain) => brain.Services.GetRequiredService<ICostMeter>();

    // Costs reach usage one-way; a read sent after them is answered after them once the grain is active.
    private static async Task<IUsageGrain> ActiveUsage(ITestBrain brain, string brainId)
    {
        var usage = brain.Platform.Usage(brainId);
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

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedPriceList(long? microsPerUnit) : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => microsPerUnit;
    }

    // Platform grain storage that counts the usage grain's writes once asked to and holds the first of them out.
    // A write stores the state as it was when the write started, as a blob upload does.
    private sealed class HeldUsageWrites : IGrainStorage
    {
        private const string UsageState = "usage";
        private readonly ConcurrentDictionary<(string, GrainId), byte[]> _records = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Serializer? _serializer;
        private int _usageWrites = -1;

        public int UsageWrites => Volatile.Read(ref _usageWrites);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldUsageWrites Over(Serializer serializer)
        {
            _serializer = serializer;
            return this;
        }

        public void HoldNextUsageWrite() => Volatile.Write(ref _usageWrites, 0);

        public void Release() => _release.TrySetResult();

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            if (_records.TryGetValue((stateName, grainId), out var stored))
            {
                grainState.State = _serializer!.Deserialize<T>(stored);
                (grainState.ETag, grainState.RecordExists) = ("stored", true);
            }
            return Task.CompletedTask;
        }

        public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var bytes = _serializer!.SerializeToArray(grainState.State);
            if (stateName == UsageState && Volatile.Read(ref _usageWrites) >= 0 && Interlocked.Increment(ref _usageWrites) == 1) { Started.TrySetResult(); await _release.Task; }
            _records[(stateName, grainId)] = bytes;
            (grainState.ETag, grainState.RecordExists) = ("stored", true);
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _records.TryRemove((stateName, grainId), out _);
            (grainState.ETag, grainState.RecordExists) = (null, false);
            return Task.CompletedTask;
        }
    }
}
