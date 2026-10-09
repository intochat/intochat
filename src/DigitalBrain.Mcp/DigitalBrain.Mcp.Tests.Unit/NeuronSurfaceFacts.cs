using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Surface;

namespace DigitalBrain.Mcp.Tests.Unit;

public sealed class NeuronSurfaceFacts
{
    [Fact]
    public void APlatformOnlyContractIsNotAddressable()
    {
        var contracts = new ContractCatalog(ContractCatalog.Deployed());
        Assert.DoesNotContain(typeof(IConnectionIndex).FullName!, contracts.Neurons);
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(IConnectionIndex).FullName!));
    }

    [Fact]
    public void ToolNamesAndDescriptionsAreDefinedOnce()
    {
        Assert.Equal(["neurons_list", "neurons_search", "neurons_get", "neurons_call", "neurons_context"],
            [NeuronSurfaceTools.ListName, NeuronSurfaceTools.SearchName, NeuronSurfaceTools.GetName, NeuronSurfaceTools.CallName, NeuronSurfaceTools.ContextName]);
        Assert.All([NeuronSurfaceTools.ListDescription, NeuronSurfaceTools.SearchDescription, NeuronSurfaceTools.GetDescription,
            NeuronSurfaceTools.CallDescription, NeuronSurfaceTools.ContextDescription], description => Assert.False(string.IsNullOrWhiteSpace(description)));
    }

    [Fact]
    public async Task WatchIsNotCallable()
    {
        var surface = Surface();
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Apps.IApp).FullName!, "b/apps/x", "Watch", "[null]", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedArgumentsAreAnArgumentError()
    {
        var surface = Surface();
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Apps.IApp).FullName!, "b/apps/x", "Read", "{not json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARestrictedSurfaceRefusesAContractTheAppDidNotName()
    {
        var surface = Surface()
            .Restrict([typeof(DigitalBrain.Apps.IApp).FullName!]);
        Assert.Equal([typeof(DigitalBrain.Apps.IApp).FullName!], surface.List());
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!,
            "connection/b/postgres/x", "ReadConnection", "[]", TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => surface.Get(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!, "connection/b/postgres/x"));
    }

    [Fact]
public void RestrictingToAnUnknownContractIsRefused()
    {
        var surface = Surface();
        Assert.Throws<ArgumentException>(() => surface.Restrict(["DigitalBrain.Nope.INope"]));
        Assert.Throws<ArgumentException>(() => surface.Restrict([typeof(IConnectionIndex).FullName!]));
    }

    [Fact]
    public async Task ARestrictedSearchRanksOnlyWhatTheAppComposes()
    {
        var ct = TestContext.Current.CancellationToken;
        var contracts = new ContractCatalog(ContractCatalog.Deployed());
        var surface = new NeuronSurface(new UnreachableBrain(), new CapabilityCatalog(contracts, [new CrowdedSource()]),
            new NeuronTypeSearch(contracts, null)).Restrict([typeof(IMcpServer).FullName!]);
        CallerContextStamper.Stamp(new()
        { PrincipalId = "owner", AccountId = "owner", BrainId = "crowded", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

        var hits = await surface.Search("render image preview", ct);

        Assert.Equal("tool:crowded/mcp/photocraft/render_preview", Assert.Single(hits).Type.Id);
    }

    // Sixty entries the app does not compose match the query better than its one tool.
    private sealed class CrowdedSource : IRuntimeCapabilitySource
    {
        public Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<NeuronType>>([
                .. Enumerable.Range(0, 60).Select(index => new NeuronType($"other:{index}", "other", "Render image preview " + index, "render image preview", [], "Other.IUnrelated")),
                new NeuronType($"tool:{brainId}/mcp/photocraft/render_preview", "tools", "PhotoCraft: Render", "A flattened PNG of the document image.",
                    [], typeof(IMcpServer).FullName!, brainId + "/mcp/photocraft", "render_preview")]);
    }

    private static NeuronSurface Surface()
        => new(new UnreachableBrain(), new CapabilityCatalog(new ContractCatalog(ContractCatalog.Deployed()), []), null!);

    // Both refusals happen before a neuron is resolved, so reaching the brain is a failure.
    private sealed class UnreachableBrain : IDigitalBrain
    {
        public IKernel Self => throw new NotSupportedException();

        public T Get<T>(NeuronId id) where T : INeuron => throw new NotSupportedException();

        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
        public IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
    }
}
