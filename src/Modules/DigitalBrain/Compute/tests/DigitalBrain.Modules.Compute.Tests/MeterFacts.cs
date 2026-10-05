using DigitalBrain.Framework.Metering;
using DigitalBrain.Compute;
using DigitalBrain.Compute.Metering;
using DigitalBrain.Compute.Reconciliation;
using DigitalBrain.Compute.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Xunit;

namespace DigitalBrain.Modules.Compute.Tests;

public sealed class MeterFacts
{
    [Fact]
    public async Task SameKeyTwiceRecordsOneEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryMeterStore();
        var sink = new DurableMeterSink(store);
        var meterEvent = Event();

        await sink.RecordAsync(meterEvent, ct);
        await sink.RecordAsync(meterEvent with { Quantity = 999 }, ct);

        var stored = await store.ReadAsync(EpochPeriod, ct);
        var single = Assert.Single(stored);
        Assert.Equal(10m, single.Quantity);
    }

    [Fact]
    public async Task BatchAppendWritesOneEventPerKeyAndIgnoresDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryMeterStore();
        var sink = new DurableMeterSink(store);
        var input = Event();
        var output = input with { MeterId = "llm.gpt.output_tokens", Step = "output_tokens" };

        var inserted = await sink.RecordBatchAsync([input, output, input], ct);

        Assert.Equal(2, inserted);
        Assert.Equal(2, (await store.ReadAsync(EpochPeriod, ct)).Count);
    }

    [Fact]
    public async Task ConcurrentDuplicateAppendsRecordOneEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var store = new NeuronMeterStore(brain.SiloServices.GetRequiredService<IGrainFactory>());

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => store.AppendAsync(Event(), ct).AsTask()));

        Assert.Single(results, inserted => inserted);
        Assert.Single(await store.ReadAsync(EpochPeriod, ct));
    }

    [Fact]
    public async Task StorageSamplerKeepsTheDailyPeakAndOneEventPerDay()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new FakeProbe(1L * 1024 * 1024 * 1024);
        var store = new InMemoryMeterStore();
        var sampler = new StorageSampler(probe, store);
        var day1 = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);

        var first = await sampler.SampleAsync("workspace-a", day1, ct);
        probe.Bytes = 512L * 1024 * 1024;
        var lower = await sampler.SampleAsync("workspace-a", day1.AddHours(6), ct);
        probe.Bytes = 3L * 1024 * 1024 * 1024;
        var second = await sampler.SampleAsync("workspace-a", day1.AddDays(1), ct);

        Assert.NotNull(first);
        Assert.Null(lower);
        Assert.NotNull(second);
        Assert.Equal(2m, sampler.AverageDailyPeakGigabytes);
        Assert.Equal(2, (await store.ReadAsync("2026-09", ct)).Count);
        Assert.All(await store.ReadAsync("2026-09", ct), stored => Assert.Equal(StorageSampler.MeterId, stored.MeterId));
        Assert.Equal(MeterSource.StorageSampler, second.Source);
    }

    [Fact]
    public void ReconciliationFlagsOnlyDeltasBeyondOnePercent()
    {
        Assert.True(MeterReconciliation.Compare(100.5m, 100m).WithinTolerance);
        Assert.False(MeterReconciliation.Compare(102m, 100m).WithinTolerance);
        Assert.True(MeterReconciliation.Compare(0m, 0m).WithinTolerance);
        Assert.Equal(0.5m, MeterReconciliation.Compare(100.5m, 100m).DeltaPercent);
    }

    private const string EpochPeriod = "1970-01";

    [Fact]
    public async Task ABatchSpanningTwoMonthsLandsInBothPeriods()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var store = brain.SiloServices.GetRequiredService<IMeterStore>();
        var september = Meter("intent-1", new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero));
        var october = Meter("intent-2", new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, await store.AppendBatchAsync([september, october], ct));
        Assert.Single(await store.ReadAsync("2026-09", ct));
        Assert.Single(await store.ReadAsync("2026-10", ct));
    }

    [Fact]
    public async Task ADuplicateMeterEventAfterReactivationIsNotRecordedTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var store = brain.SiloServices.GetRequiredService<IMeterStore>();
        var meter = Meter("intent-1", new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.True(await store.AppendAsync(meter, ct));
        await brain.DeactivateAsync(brain.Get<IComputeRecords>("meters/2026-09"), ct);
        Assert.False(await store.AppendAsync(meter, ct));
        Assert.Single(await store.ReadAsync("2026-09", ct));
    }

    private static MeterEvent Meter(string intent, DateTimeOffset at) => new()
    {
        IntentId = intent,
        MeterId = "llm.m.input_tokens",
        Step = "input_tokens",
        BrainId = "brain-1",
        Quantity = 1m,
        Unit = "tokens",
        Source = MeterSource.ChatClient,
        OccurredAt = at,
    };

    private static MeterEvent Event() => new()
    {
        IntentId = "intent-1",
        MeterId = "llm.gpt.input_tokens",
        Step = "input_tokens",
        BrainId = "workspace-a",
        Quantity = 10,
        Unit = "tokens",
        Source = MeterSource.ChatClient,
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class FakeProbe(long bytes) : IStorageUsageProbe
    {
        public long Bytes { get; set; } = bytes;

        public ValueTask<long> ReadBytesAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Bytes);
    }
}
