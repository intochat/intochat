using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Kernel.Composition.Capabilities;

namespace IntoChat.OS.Tests.Unit.Kernel;

// A bound connection's storage never runs a provider call with no caller, and never as an implicit kernel.
public sealed class ConnectionStorageCallerFacts
{
    private const string Key = "alice/drive/c1";

    [Fact]
    public async Task ACallWithNoCallerIsRefused()
    {
        await using var os = TestOS.Local();
        var storage = new ConnectionStorage(new InMemoryStorage(), Key, edge: os.Edge);

        Assert.Null(os.Callers.Current);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.Roots(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABoundConnectionWithoutAnEdgeFails()
    {
        await using var os = TestOS.Local();
        var storage = new ConnectionStorage(new InMemoryStorage(), Key);

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId("alice"))))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Roots(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task APersonCallsTheProviderAsThemselves()
    {
        await using var os = TestOS.Local();
        var storage = new ConnectionStorage(new InMemoryStorage(), Key, edge: os.Edge);

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId("alice"))))
        {
            Assert.NotNull(await storage.Roots(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task AnAppCallerIsPreservedAndNotElevatedToKernel()
    {
        await using var os = TestOS.Local();
        Caller? observed = null;
        var backing = new InMemoryStorage();
        var spy = new CallerCapturingStorage(backing, () => observed = os.Callers.Current);
        var storage = new ConnectionStorage(spy, Key, edge: os.Edge);
        var expected = os.Edge.App("alice/apps/files", new Actor.Person("alice", "alice"), new BrainId("alice"));

        using (os.Edge.Enter(expected))
        {
            _ = await storage.Roots(TestContext.Current.CancellationToken);
        }

        Assert.NotNull(observed);
        var app = Assert.IsType<Actor.App>(observed.Actor);
        Assert.Equal("alice/apps/files", app.Key);
        Assert.Equal(new BrainId("alice"), observed.Brain);
        Assert.False(observed.Actor is Actor.Kernel);
    }

    [Fact]
    public async Task BrokerBoundAppReadsSucceedAndUnapprovedAppWritesCannotBypassConfirmation()
    {
        await using var os = TestOS.Local();
        var brain = new BrainId("alice");
        var neuronId = new NeuronId(brain, "local-cloud", "main");
        var connectionKey = "alice/local-cloud/main";
        var backing = new InMemoryStorage();
        var gated = new GatedStorage(backing, neuronId, () => os.Callers.Current, confirmations: null);
        var storage = new ConnectionStorage(gated, connectionKey, edge: os.Edge);
        var appCaller = os.Edge.App("alice/apps/notes", new Actor.Person("alice", "alice"), brain);

        using (os.Edge.Enter(appCaller))
        {
            var roots = await storage.Roots(TestContext.Current.CancellationToken);
            var root = Assert.Single(roots);

            // Unapproved Actor.App write cannot bypass StorageWriteGate (even with WorkspaceDirect policy)
            // because ConnectionStorage no longer lifts Actor.App to Actor.Kernel.
            await Assert.ThrowsAsync<DigitalBrain.Capabilities.Storage.Denied>(() =>
                storage.Write(DigitalBrain.Capabilities.Storage.WritePlan.CreateFolder(root.Id, "unapproved"), TestContext.Current.CancellationToken));
        }
    }

    private sealed class CallerCapturingStorage(DigitalBrain.Capabilities.Storage.IStorage inner, Action capture) : DigitalBrain.Capabilities.Storage.IStorage
    {
        public Task<DigitalBrain.Capabilities.Storage.StorageDescriptor> Describe(CancellationToken ct = default) { capture(); return inner.Describe(ct); }
        public Task<IReadOnlyList<DigitalBrain.Capabilities.Storage.Item>> Roots(CancellationToken ct = default) { capture(); return inner.Roots(ct); }
        public Task<DigitalBrain.Capabilities.Storage.Page<DigitalBrain.Capabilities.Storage.Item>> List(DigitalBrain.Capabilities.Storage.ItemId folder, DigitalBrain.Capabilities.Storage.ListQuery query, CancellationToken ct = default) { capture(); return inner.List(folder, query, ct); }
        public Task<DigitalBrain.Capabilities.Storage.Item> Stat(DigitalBrain.Capabilities.Storage.ItemId id, CancellationToken ct = default) { capture(); return inner.Stat(id, ct); }
        public Task<DigitalBrain.Capabilities.Storage.Content> Read(DigitalBrain.Capabilities.Storage.ItemId id, DigitalBrain.Capabilities.Storage.ByteRange? range, CancellationToken ct = default) { capture(); return inner.Read(id, range, ct); }
        public Task<DigitalBrain.Operations.OperationId> Write(DigitalBrain.Capabilities.Storage.WritePlan plan, CancellationToken ct = default) { capture(); return inner.Write(plan, ct); }
        public Task<DigitalBrain.Capabilities.Storage.Page<DigitalBrain.Capabilities.Storage.Item>> Search(DigitalBrain.Capabilities.Storage.SearchQuery query, CancellationToken ct = default) { capture(); return inner.Search(query, ct); }
        public Task<IReadOnlyList<DigitalBrain.Capabilities.Storage.Item>> Ancestors(DigitalBrain.Capabilities.Storage.ItemId id, CancellationToken ct = default) { capture(); return inner.Ancestors(id, ct); }
        public Task<DigitalBrain.Capabilities.Storage.StorageReadStream> OpenRead(DigitalBrain.Capabilities.Storage.ItemId item, DigitalBrain.Capabilities.Storage.ByteRange? range, CancellationToken ct = default) { capture(); return inner.OpenRead(item, range, ct); }
        public Task<DigitalBrain.Capabilities.Storage.StorageWriteStream> OpenWrite(DigitalBrain.Capabilities.Storage.WritePlan plan, CancellationToken ct = default) { capture(); return inner.OpenWrite(plan, ct); }
    }

    private sealed class GatedStorage(
        DigitalBrain.Capabilities.Storage.IStorage inner,
        NeuronId neuronId,
        Func<Caller?> caller,
        DigitalBrain.Confirmation.IConfirmations? confirmations) : DigitalBrain.Capabilities.Storage.IStorage
    {
        public Task<DigitalBrain.Capabilities.Storage.StorageDescriptor> Describe(CancellationToken ct = default) => inner.Describe(ct);
        public Task<IReadOnlyList<DigitalBrain.Capabilities.Storage.Item>> Roots(CancellationToken ct = default) => inner.Roots(ct);
        public Task<DigitalBrain.Capabilities.Storage.Page<DigitalBrain.Capabilities.Storage.Item>> List(DigitalBrain.Capabilities.Storage.ItemId folder, DigitalBrain.Capabilities.Storage.ListQuery query, CancellationToken ct = default) => inner.List(folder, query, ct);
        public Task<DigitalBrain.Capabilities.Storage.Item> Stat(DigitalBrain.Capabilities.Storage.ItemId id, CancellationToken ct = default) => inner.Stat(id, ct);
        public Task<DigitalBrain.Capabilities.Storage.Content> Read(DigitalBrain.Capabilities.Storage.ItemId id, DigitalBrain.Capabilities.Storage.ByteRange? range, CancellationToken ct = default) => inner.Read(id, range, ct);
        public async Task<DigitalBrain.Operations.OperationId> Write(DigitalBrain.Capabilities.Storage.WritePlan plan, CancellationToken ct = default)
        {
            await DigitalBrain.Capabilities.Storage.Gating.StorageWriteGate.AuthorizeAsync(
                neuronId, inner, plan, caller(), authorizer: null, confirmations, DigitalBrain.Capabilities.Storage.Gating.StorageWritePolicy.WorkspaceDirect, ct);
            return await inner.Write(plan, ct);
        }
        public Task<DigitalBrain.Capabilities.Storage.Page<DigitalBrain.Capabilities.Storage.Item>> Search(DigitalBrain.Capabilities.Storage.SearchQuery query, CancellationToken ct = default) => inner.Search(query, ct);
        public Task<IReadOnlyList<DigitalBrain.Capabilities.Storage.Item>> Ancestors(DigitalBrain.Capabilities.Storage.ItemId id, CancellationToken ct = default) => inner.Ancestors(id, ct);
        public Task<DigitalBrain.Capabilities.Storage.StorageReadStream> OpenRead(DigitalBrain.Capabilities.Storage.ItemId item, DigitalBrain.Capabilities.Storage.ByteRange? range, CancellationToken ct = default) => inner.OpenRead(item, range, ct);
        public Task<DigitalBrain.Capabilities.Storage.StorageWriteStream> OpenWrite(DigitalBrain.Capabilities.Storage.WritePlan plan, CancellationToken ct = default) => inner.OpenWrite(plan, ct);
    }
}
