using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;

namespace DigitalBrain.Testing.Tests.Unit.Hosting;

public interface IEdgeProbeNeuron
{
    Task<string> Ping();

    [Internal]
    Task<string> InternalPing();

    [Shared]
    Task<string> SharedUnmarked();

    [Public]
    Task<string> PublicUnmarked();
}

[Shareable, DefaultNeuronType("test.edge.probe")]
public interface IEdgeShareableProbe
{
    Task<string> OwnerOnly();

    [Shared]
    Task<string> SharedOp();

    [Public]
    Task<string> PublicOp();
}

[Neuron("test.edge.probe")]
public sealed class EdgeProbeNeuron : Neuron, IEdgeProbeNeuron, IEdgeShareableProbe
{
    public Task<string> Ping() => Task.FromResult(Context.Caller?.Actor.GetType().Name ?? "none");

    public Task<string> InternalPing() => Task.FromResult(Context.Caller?.Actor.GetType().Name ?? "none");

    public Task<string> SharedUnmarked() => Task.FromResult(Context.Caller?.Brain.Value ?? "none");

    public Task<string> PublicUnmarked() => Task.FromResult(Context.Caller?.Brain.Value ?? "none");

    public Task<string> OwnerOnly() => Task.FromResult(Context.Caller?.Brain.Value ?? "none");

    public Task<string> SharedOp() => Task.FromResult(Context.Caller?.Brain.Value ?? "none");

    public Task<string> PublicOp() => Task.FromResult(Context.Caller?.Brain.Value ?? "none");
}

public sealed class EdgeAuthorityFacts
{
    private static readonly BrainId Brain = new("testos-edge");
    private static readonly BrainId ForeignBrain = new("testos-foreign");

    [Fact]
    public async Task TestOS_Edge_SupportsKernelAndTypedCallers_WithoutExposingEdgeInNeuronServices()
    {
        await using var os = global::DigitalBrain.Testing.TestOS.Local().Register<EdgeProbeNeuron>();
        os.AccessOptions.UnstampedEdgeIsKernel = false;
        os.AccessOptions.AuditContractInternal = false;

        Assert.Null(os.Services.GetService(typeof(EdgeAuthority)));
        Assert.False(os.Edge.IsKernelActive);

        var id = new NeuronId(Brain, "test.edge.probe", "default");
        var proxy = os.Get<IEdgeProbeNeuron>(id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => proxy.Ping());

        using (os.Edge.EnterKernel(Brain))
        {
            Assert.True(os.Edge.IsKernelActive);
            Assert.Equal("Kernel", await proxy.InternalPing());
        }

        Assert.False(os.Edge.IsKernelActive);

        using (os.Edge.Enter(os.Edge.Guest("try-1", "try-1", Brain)))
        {
            Assert.False(os.Edge.IsKernelActive);
            Assert.Equal("App", await proxy.Ping());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => proxy.InternalPing());
        }

        using (os.Edge.Enter(os.Edge.RelayedPerson("alice", "acct-1", Brain)))
        {
            Assert.False(os.Edge.IsKernelActive);
            Assert.Equal("Person", await proxy.InternalPing());
        }

        using (os.Edge.Enter(os.Edge.From(CapabilityCallers.Of("alice", "acct-1", Brain.Value))))
        {
            Assert.False(os.Edge.IsKernelActive);
            Assert.Equal("Person", await proxy.Ping());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => proxy.InternalPing());
        }
    }

    [Fact]
    public async Task TestOS_CrossBrainCalls_RequireShareableAndSharedOrPublic()
    {
        await using var os = global::DigitalBrain.Testing.TestOS.Local().Register<EdgeProbeNeuron>();
        var id = new NeuronId(Brain, "test.edge.probe", "default");
        var unmarked = os.Get<IEdgeProbeNeuron>(id);
        var shareable = os.Get<IEdgeShareableProbe>(id);

        using (os.Edge.Enter(os.Edge.From(CapabilityCallers.Of("bob", "acct-bob", ForeignBrain.Value))))
        {
            Assert.Equal(ForeignBrain.Value, await shareable.SharedOp());
            Assert.Equal(ForeignBrain.Value, await shareable.PublicOp());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(shareable.OwnerOnly);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(unmarked.SharedUnmarked);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(unmarked.PublicUnmarked);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(unmarked.Ping);
        }
    }
}
