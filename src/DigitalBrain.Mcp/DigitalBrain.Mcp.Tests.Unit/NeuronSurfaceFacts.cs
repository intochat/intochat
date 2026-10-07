using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;

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

    // Both refusals happen before a neuron is resolved, so reaching the brain is a failure.
    private sealed class UnreachableBrain : IDigitalBrain
    {
        public IKernel Self => throw new NotSupportedException();

        public T Get<T>(NeuronId id) where T : INeuron => throw new NotSupportedException();

        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
    }
}
