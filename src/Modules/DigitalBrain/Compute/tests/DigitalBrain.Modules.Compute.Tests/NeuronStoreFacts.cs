using DigitalBrain.Compute;
using DigitalBrain.Compute.Metering;
using DigitalBrain.Compute.Storage;
using DigitalBrain.Compute.Usage;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Compute.Tests;

public sealed class NeuronStoreFacts
{
    [Fact]
    public async Task UsageContractPreservesRowsAcrossReactivationAndRejectsForeignCursors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var usage = brain.Get<IComputeUsage>("account");
        await usage.Append("brain", "one", "first", DateTimeOffset.UnixEpoch, ct);
        await usage.Append("brain", "two", "second", DateTimeOffset.UnixEpoch, ct);
        await brain.DeactivateAsync(usage, ct);
        var page = await usage.Read("brain", 1, null, ct);
        Assert.Single(page.Items);
        Assert.NotNull(page.NextCursor);
        var next = await usage.Read("brain", 1, page.NextCursor, ct);
        Assert.Single(next.Items);
        Assert.NotEqual(page.Items[0].Id, next.Items[0].Id);
        Assert.Empty((await usage.Read("other", 20, null, ct)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => usage.Read("other", 1, page.NextCursor, ct));
    }

    [Fact]
    public async Task TheUsageLogEvictsOldestRowsPastTheCapAndStillPages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var store = brain.SiloServices.GetRequiredService<IUsageStore>();
        var grain = brain.Get<IComputeRecords>(ComputeRecordsNeuron.UsageKeyPrefix + UsagePaging.Scope("account", "brain-1"));
        var total = ComputeRecordsNeuron.Cap + 10;
        for (var start = 0; start < total; start += 1000)
        {
            await grain.Put([.. Enumerable.Range(start, Math.Min(1000, total - start)).Select(UsageRecord)], true);
        }
        var page = await store.ReadAsync("account", "brain-1", 1, null, ct);
        Assert.Equal($"row-{total - 1}", page.Items[0].Id);
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var next = await store.ReadAsync("account", "brain-1", 100, cursor, ct);
            all.AddRange(next.Items.Select(row => row.Id));
            cursor = next.NextCursor;
        } while (cursor is not null);
        Assert.Equal(ComputeRecordsNeuron.Cap, all.Count);
        Assert.All(Enumerable.Range(0, 10), i => Assert.DoesNotContain($"row-{i}", all));
        Assert.Contains("row-10", all);
    }

    [Fact]
    public async Task ARowOlderThanEveryKeptUsageRowIsNotCountedWhenItIsEvictedInTheSamePut()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var grain = brain.Get<IComputeRecords>(ComputeRecordsNeuron.UsageKeyPrefix + "full");
        for (var start = 0; start < ComputeRecordsNeuron.Cap; start += 1000)
        {
            await grain.Put([.. Enumerable.Range(start + 1, 1000).Select(UsageRecord)], true);
        }
        Assert.Equal(0, await grain.Put([UsageRecord(0)], true));
        Assert.DoesNotContain(await grain.Read(), record => record.Id == "row-0");
    }

    [Fact]
    public async Task MeterShardsNeverEvictRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var grain = brain.Get<IComputeRecords>("meters/2026-09");
        for (var start = 0; start < ComputeRecordsNeuron.Cap + 10; start += 1000)
        {
            await grain.Put([.. Enumerable.Range(start, Math.Min(1000, ComputeRecordsNeuron.Cap + 10 - start)).Select(UsageRecord)], false);
        }
        Assert.Equal(ComputeRecordsNeuron.Cap + 10, (await grain.Read()).Length);
    }

    private static ComputeStoredRecord UsageRecord(int i)
    {
        var id = $"row-{i}";
        return new(id, "{}", (DateTime.UnixEpoch.Ticks + i).ToString("D19") + "-" + UsagePaging.Hash(id), 0);
    }

    [Fact]
    public async Task NeuronRecordsSurviveDeactivationAndDeduplicateBatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var records = brain.Get<IComputeRecords>("test-records");
        var row = new ComputeStoredRecord("one", "original", "001", 10);
        Assert.Equal(1, await records.Put([row, row], false));
        await brain.DeactivateAsync(records, ct);
        Assert.Equal(0, await records.Put([row], false));
        Assert.Equal("original", Assert.Single(await records.Read()).Payload);
        await records.Put([row with { Payload = "stale", Revision = 1 }], true);
        Assert.Equal("original", Assert.Single(await records.Read()).Payload);
        await records.Put([row with { Payload = "updated", SortKey = "999", Revision = 11 }], true);
        var updated = Assert.Single(await records.Read());
        Assert.Equal("updated", updated.Payload);
        Assert.Equal("001", updated.SortKey);
    }
    [Fact]
    public async Task ProductionStoresUseGrainPersistenceWithoutDatabaseOrFilesystemFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        Assert.DoesNotContain("InMemory", brain.SiloServices.GetRequiredService<IMeterStore>().GetType().Name);
        Assert.DoesNotContain("File", brain.SiloServices.GetRequiredService<IUsageStore>().GetType().Name);
        var usage = brain.SiloServices.GetRequiredService<IUsageStore>();
        await usage.AppendAsync("account", "brain", "first", "original", ct);
        var firstTime = (await usage.ReadAsync("account", "brain", 1, null, ct)).Items[0].OccurredAt;
        await usage.AppendAsync("account", "brain", "second", "second", ct);
        await usage.AppendAsync("account", "brain", "first", "newer", ct);
        await usage.AppendAsync("account", "brain", "first", "stale", ct, DateTimeOffset.UnixEpoch);
        var first = await usage.ReadAsync("account", "brain", 1, null, ct);
        var second = await usage.ReadAsync("account", "brain", 1, first.NextCursor, ct);
        Assert.Equal("second", Assert.Single(first.Items).Id);
        Assert.Equal("newer", Assert.Single(second.Items).Payload);
        Assert.Equal(firstTime, second.Items[0].OccurredAt);
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => usage.ReadAsync("other", "brain", 1, first.NextCursor, ct).AsTask());
    }

    [Fact]
    public async Task ARecordOverTheSizeLimitIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ComputeModule>().StartAsync(ct);
        var grain = brain.Get<IComputeRecords>("limits");
        var oversized = UsageRecord(1) with { Payload = new string('x', 1024 * 1024 + 1) };
        await Assert.ThrowsAsync<ArgumentException>(() => grain.Put([oversized], true));
    }
}
