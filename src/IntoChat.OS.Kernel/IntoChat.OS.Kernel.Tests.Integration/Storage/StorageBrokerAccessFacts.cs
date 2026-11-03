using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities.Storage;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Integration.Storage;

[Trait("host", "own")]
public sealed class StorageBrokerAccessFacts
{
    private static readonly BrainId Brain = new("b1");

    [Fact]
    public async Task DriveCallIsLogged()
    {
        var row = await Stat("google-drive");
        Assert.Equal("google-drive", row.ModuleKind);
        Assert.Equal("Stat", row.Op);
        Assert.Equal("Ok", row.Outcome);
    }

    [Fact]
    public async Task BlobCallIsLogged()
    {
        var row = await Stat("azure-blob");
        Assert.Equal("azure-blob", row.ModuleKind);
        Assert.Equal("Ok", row.Outcome);
    }

    [Fact]
    public async Task FakeStorageCallIsLogged()
    {
        var log = new StorageAccessLog();
        var fake = new FakeStorage();
        var connections = new Binder("fake", fake);
        var broker = new CapabilityBroker(new CapabilityRegistry(), connections, new ServiceCollection().AddSingleton(log).BuildServiceProvider(), []).ForApp("app.files");
        var storage = (await broker.Storages(Brain, TestContext.Current.CancellationToken)).Single().Capability;
        fake.Seed(StorageSeed.Canonical);
        log.Clear();

        await storage.List(fake.Root, new ListQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(log.Rows);
        Assert.Equal("app.files", row.App);
        Assert.Equal("fake", row.ModuleKind);
        Assert.Equal("List", row.Op);
    }

    private static async Task<StorageAccessRow> Stat(string kind)
    {
        var log = new StorageAccessLog();
        var connections = new Binder(kind, new InMemoryStorage(kind));
        var broker = new CapabilityBroker(new CapabilityRegistry(), connections, new ServiceCollection().AddSingleton(log).BuildServiceProvider(), []).ForApp("app.files");
        var storage = (await broker.Storages(Brain, TestContext.Current.CancellationToken)).Single().Capability;
        await storage.Stat(((InMemoryStorage)connections.Bound).Root, TestContext.Current.CancellationToken);
        return Assert.Single(log.Rows);
    }

    private sealed class Binder : ICapabilityConnections
    {
        private readonly string _key;
        private readonly IStorage _storage;

        internal Binder(string key, IStorage storage)
        {
            _key = key;
            _storage = storage;
            Bound = storage;
        }

        internal IStorage Bound { get; }

        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BrainConnection>>([new BrainConnection(_key, _key, true, [Capability.Storage.V1], null)]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct) where TCapability : class
            => Task.FromResult((TCapability)(object)_storage);

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct) => Task.CompletedTask;
    }
}
