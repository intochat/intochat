using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Testing.Conformance.Storage;

namespace DigitalBrain.Testing.Tests.Unit.Conformance;

public sealed class StorageFakeFacts
{
    [Fact]
    public async Task WithStorage_binds_fakes_as_storage_at_1_in_binding_order()
    {
        await using var os = TestOS.Local()
            .WithStorage("drive", new FakeStorage("Work Drive", Quota.Known(1, 2)))
            .WithStorage("c", label: "C:");

        var broker = (ICapabilityBroker)os.Services.GetService(typeof(ICapabilityBroker))!;
        var all = await broker.ResolveAll<IStorage>(new BrainId("ada"), TestContext.Current.CancellationToken);
        var labels = new List<string>();
        foreach (var connection in all)
        {
            labels.Add((await connection.Capability.Describe(TestContext.Current.CancellationToken)).Label);
        }

        Assert.Equal(["drive", "c"], all.Select(connection => connection.ConnectionKey));
        Assert.Equal(["Work Drive", "C:"], labels);
    }

    [Fact]
    public async Task FakeStorage_records_calls_and_seeds_files()
    {
        var fake = new FakeStorage("Mem");
        var file = fake.AddFile(fake.Root, "a.txt", "hi"u8.ToArray());

        Assert.Equal("hi", await fake.ReadText(file, TestContext.Current.CancellationToken));
        Assert.Contains(nameof(IStorage.Read), fake.Calls);
    }

    [Fact]
    public async Task InMemoryOperations_record_started_progress_and_done()
    {
        var storage = new InMemoryStorage();
        var id = await storage.Write(WritePlan.CreateFolder(storage.Root, "x"), TestContext.Current.CancellationToken);
        var result = await storage.Operations.WaitFor(id, TestContext.Current.CancellationToken);

        Assert.IsType<DigitalBrain.Operations.OperationResult.Succeeded>(result);
        Assert.Equal(["started", "progress", "done"], storage.Operations.Events.Where(e => e.Id == id).Select(e => e.Stage));
    }
}
