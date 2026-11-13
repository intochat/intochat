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
        var log = CreateAccessLog();
        var fake = new FakeStorage();
        var connections = new Binder("fake", fake);
        var broker = new CapabilityBroker(new CapabilityRegistry(), connections, WithAccessLog(log), []).ForApp("app.files");
        var storage = (await broker.Storages(Brain, TestContext.Current.CancellationToken)).Single().Capability;
        fake.Seed(StorageSeed.Canonical);
        ClearAccessLog(log);

        await storage.List(fake.Root, new ListQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(ReadRows(log));
        Assert.Equal("app.files", row.App);
        Assert.Equal("fake", row.ModuleKind);
        Assert.Equal("List", row.Op);
    }

    private static async Task<(string App, string ModuleKind, string Op, string Outcome)> Stat(string kind)
    {
        var log = CreateAccessLog();
        var connections = new Binder(kind, new InMemoryStorage(kind));
        var broker = new CapabilityBroker(new CapabilityRegistry(), connections, WithAccessLog(log), []).ForApp("app.files");
        var storage = (await broker.Storages(Brain, TestContext.Current.CancellationToken)).Single().Capability;
        await storage.Stat(((InMemoryStorage)connections.Bound).Root, TestContext.Current.CancellationToken);
        return Assert.Single(ReadRows(log));
    }

    private static readonly Type AccessLogType = typeof(CapabilityBroker).Assembly
        .GetType("DigitalBrain.Runtime.Orleans.Capabilities.Storage.StorageAccessLog", throwOnError: true)!;

    private static object CreateAccessLog() => Activator.CreateInstance(AccessLogType)!;

    private static IServiceProvider WithAccessLog(object log)
        => new ServiceCollection().AddSingleton(AccessLogType, log).BuildServiceProvider();

    private static void ClearAccessLog(object log)
        => AccessLogType.GetMethod("Clear", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.Invoke(log, null);

    private static List<(string App, string ModuleKind, string Op, string Outcome)> ReadRows(object log)
    {
        var rows = (System.Collections.IEnumerable)AccessLogType
            .GetProperty("Rows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .GetValue(log)!;
        var result = new List<(string, string, string, string)>();
        foreach (var row in rows)
        {
            var t = row.GetType();
            result.Add((
                (string)t.GetProperty("App")!.GetValue(row)!,
                (string)t.GetProperty("ModuleKind")!.GetValue(row)!,
                (string)t.GetProperty("Op")!.GetValue(row)!,
                (string)t.GetProperty("Outcome")!.GetValue(row)!));
        }
        return result;
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
