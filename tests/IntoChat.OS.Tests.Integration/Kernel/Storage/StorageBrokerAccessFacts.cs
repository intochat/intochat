using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
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
        var row = await Stat("google-drive", "app.files.drive");
        Assert.Equal("app.files.drive", row.App);
        Assert.Equal("Stat", row.Op);
        Assert.Equal("Ok", row.Outcome);
    }

    [Fact]
    public async Task BlobCallIsLogged()
    {
        var row = await Stat("azure-blob", "app.files.blob");
        Assert.Equal("app.files.blob", row.App);
        Assert.Equal("Ok", row.Outcome);
    }

    [Fact]
    public async Task FakeStorageCallIsLogged()
    {
        var appName = "app.files.fake";
        var rows = new List<(string App, string Op, string Outcome)>();
        using var listener = Listen(rows, appName);
        var fake = new FakeStorage();
        var connections = new Binder("fake", fake);
        var broker = new CapabilityBroker(new CapabilityRegistry(), connections, new ServiceCollection().BuildServiceProvider(), []).ForApp(appName);
        var storage = (await broker.Storages(Brain, TestContext.Current.CancellationToken)).Single().Capability;
        fake.Seed(StorageSeed.Canonical);
        rows.Clear();

        await storage.List(fake.Root, new ListQuery(), TestContext.Current.CancellationToken);

        var row = Assert.Single(rows);
        Assert.Equal(appName, row.App);
        Assert.Equal("List", row.Op);
        Assert.Equal("Ok", row.Outcome);
    }

    private static async Task<(string App, string Op, string Outcome)> Stat(string kind, string appName)
    {
        var rows = new List<(string App, string Op, string Outcome)>();
        using var listener = Listen(rows, appName);
        var connections = new Binder(kind, new InMemoryStorage(kind));
        var broker = new CapabilityBroker(new CapabilityRegistry(), connections, new ServiceCollection().BuildServiceProvider(), []).ForApp(appName);
        var storage = (await broker.Storages(Brain, TestContext.Current.CancellationToken)).Single().Capability;
        await storage.Stat(((InMemoryStorage)connections.Bound).Root, TestContext.Current.CancellationToken);
        return Assert.Single(rows);
    }

    private static ActivityListener Listen(List<(string App, string Op, string Outcome)> rows, string appName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DigitalBrain.Storage",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "storage.access")
                {
                    return;
                }
                var app = activity.GetTagItem("storage.app") as string ?? "";
                if (!string.Equals(app, appName, StringComparison.Ordinal))
                {
                    return;
                }
                var op = activity.GetTagItem("storage.op") as string ?? "";
                var outcome = activity.GetTagItem("storage.outcome") as string ?? "";
                lock (rows)
                {
                    rows.Add((app, op, outcome));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
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
