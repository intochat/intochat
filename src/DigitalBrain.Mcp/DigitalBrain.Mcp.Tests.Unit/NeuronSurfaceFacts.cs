using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Surface;

namespace DigitalBrain.Mcp.Tests.Unit;

public sealed class NeuronSurfaceFacts
{
    [Fact]
    public void APlatformOnlyContractIsNotAddressable()
    {
        var contracts = new NeuronContracts(NeuronContracts.Deployed());
        Assert.DoesNotContain(typeof(IConnectionIndex).FullName!, contracts.Names);
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
        var surface = new NeuronSurface(new UnreachableBrain(), new NeuronContracts(NeuronContracts.Deployed()), null!);
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Apps.IApp).FullName!, "b/apps/x", "Watch", "[null]", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedArgumentsAreAnArgumentError()
    {
        var surface = new NeuronSurface(new UnreachableBrain(), new NeuronContracts(NeuronContracts.Deployed()), null!);
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Apps.IApp).FullName!, "b/apps/x", "Read", "{not json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARestrictedSurfaceRefusesAContractTheAppDidNotName()
    {
        var surface = new NeuronSurface(new UnreachableBrain(), new NeuronContracts(NeuronContracts.Deployed()), null!)
            .Restrict([typeof(DigitalBrain.Apps.IApp).FullName!]);
        Assert.Equal([typeof(DigitalBrain.Apps.IApp).FullName!], surface.List());
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!,
            "connection/b/postgres/x", "ReadConnection", "[]", TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => surface.Get(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!, "connection/b/postgres/x"));
    }

    [Fact]
    public void RestrictingToAnUnknownContractIsRefused()
    {
        var surface = new NeuronSurface(new UnreachableBrain(), new NeuronContracts(NeuronContracts.Deployed()), null!);
        Assert.Throws<ArgumentException>(() => surface.Restrict(["DigitalBrain.Nope.INope"]));
        Assert.Throws<ArgumentException>(() => surface.Restrict([typeof(IConnectionIndex).FullName!]));
    }

    // Both refusals happen before a neuron is resolved, so reaching the brain is a failure.
    private sealed class UnreachableBrain : IDigitalBrain
    {
        public IKernel Self => throw new NotSupportedException();

        public T Get<T>(NeuronId id) where T : INeuron => throw new NotSupportedException();

        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
    }
}
