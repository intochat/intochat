using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit.Hosting;

[NeuronAlias("facts.dispatcher.affine")]
public interface IDispatcherAffineNeuron
{
    Task<string> Mutate(string value);

    [ReadOnlyCall]
    Task<string> Peek();

    Task Notify(string value);
}

[Neuron("facts.dispatcher.affine")]
public sealed class DispatcherAffineNeuron : Neuron, IDispatcherAffineNeuron
{
    public Task<string> Mutate(string value) => Task.FromResult(value);

    [ReadOnlyCall]
    public Task<string> Peek() => Task.FromResult("ok");

    public Task Notify(string value) => Task.CompletedTask;
}

[NeuronAlias("facts.dispatcher.reentrant")]
public interface IDispatcherReentrantNeuron
{
    Task<string> Ping();
}

[Neuron("facts.dispatcher.reentrant")]
[Reentrant]
public sealed class DispatcherReentrantNeuron : Neuron, IDispatcherReentrantNeuron
{
    public Task<string> Ping() => Task.FromResult("pong");
}

[NeuronAlias("facts.dispatcher.stateless")]
public interface IDispatcherStatelessNeuron
{
    Task<string> Compute();
}

[Neuron("facts.dispatcher.stateless")]
[StatelessNeuron]
public sealed class DispatcherStatelessNeuron : Neuron, IDispatcherStatelessNeuron
{
    public Task<string> Compute() => Task.FromResult("42");
}

public sealed class OrleansNeuronDispatcherFacts
{
    private sealed class RecordingCatalog(Func<string, string?> resolve) : INeuronHostCatalog
    {
        public int HostTypeCalls { get; private set; }

        public Task<string?> HostType(string neuronType)
        {
            HostTypeCalls++;
            return Task.FromResult(resolve(neuronType));
        }
    }

    private sealed class RecordingHost(bool interleavedResult) : INeuronHost
    {
        public int IsInterleavedCalls { get; private set; }
        public int InvokeCalls { get; private set; }
        public int InvokeInterleavedCalls { get; private set; }
        public int PostCalls { get; private set; }
        public int PostInterleavedCalls { get; private set; }

        public Task<bool> IsInterleaved(RuntimeInvocation call)
        {
            IsInterleavedCalls++;
            return Task.FromResult(interleavedResult);
        }

        public Task<JsonElement?> Invoke(RuntimeInvocation call, CancellationToken cancellationToken = default)
        {
            InvokeCalls++;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("invoke", RuntimeProtocol.Json));
        }

        public Task<JsonElement?> InvokeInterleaved(RuntimeInvocation call, CancellationToken cancellationToken = default)
        {
            InvokeInterleavedCalls++;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("invoke-interleaved", RuntimeProtocol.Json));
        }

        public Task Post(RuntimeInvocation call)
        {
            PostCalls++;
            return Task.CompletedTask;
        }

        public Task PostInterleaved(RuntimeInvocation call)
        {
            PostInterleavedCalls++;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<JsonElement> Stream(RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<JsonElement?> InspectState(CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);
    }

    private sealed class RecordingGrainFactory(RecordingCatalog catalog, RecordingHost host) : IGrainFactory
    {
        public List<GrainId> RequestedHostIds { get; } = [];

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(INeuronHostCatalog))
            {
                return (TGrainInterface)(object)catalog;
            }

            throw new NotSupportedException($"Unexpected grain request: {typeof(TGrainInterface).FullName}");
        }

        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable
        {
            if (typeof(TGrainInterface) == typeof(INeuronHost))
            {
                RequestedHostIds.Add(grainId);
                return (TGrainInterface)(object)host;
            }

            throw new NotSupportedException($"Unexpected grain request: {typeof(TGrainInterface).FullName}");
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainPrimaryKey) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainPrimaryKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    }

    [Fact]
    public async Task IndexedAffineNeuronUsesFastPathWithoutCatalogOrIsInterleavedRpcs()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new RecordingCatalog(_ => "neuron.host");
        var host = new RecordingHost(interleavedResult: false);
        var grains = new RecordingGrainFactory(catalog, host);
        var registry = new NeuronTypeRegistry([typeof(DispatcherAffineNeuron)]);
        var dispatcher = new OrleansNeuronDispatcher(grains, registry);

        var target = new NeuronId(new BrainId("alice"), "facts.dispatcher.affine", "item-1");
        var mutateCall = new RuntimeInvocation(
            Behavior: string.Empty,
            Contract: "facts.dispatcher.affine",
            Key: target.ToString(),
            Method: nameof(IDispatcherAffineNeuron.Mutate),
            Arguments: [JsonSerializer.SerializeToElement("x", RuntimeProtocol.Json)]);
        var peekCall = new RuntimeInvocation(
            Behavior: string.Empty,
            Contract: "facts.dispatcher.affine",
            Key: target.ToString(),
            Method: nameof(IDispatcherAffineNeuron.Peek),
            Arguments: []);
        var notifyCall = new RuntimeInvocation(
            Behavior: string.Empty,
            Contract: "facts.dispatcher.affine",
            Key: target.ToString(),
            Method: nameof(IDispatcherAffineNeuron.Notify),
            Arguments: [JsonSerializer.SerializeToElement("y", RuntimeProtocol.Json)]);

        var mutateResult = await dispatcher.InvokeAsync(target, mutateCall, ct);
        var peekResult = await dispatcher.InvokeAsync(target, peekCall, ct);
        await dispatcher.PostAsync(target, notifyCall);

        Assert.Equal("invoke", mutateResult?.GetString());
        Assert.Equal("invoke-interleaved", peekResult?.GetString());
        Assert.Equal(0, catalog.HostTypeCalls);
        Assert.Equal(0, host.IsInterleavedCalls);
        Assert.Equal(1, host.InvokeCalls);
        Assert.Equal(1, host.InvokeInterleavedCalls);
        Assert.Equal(1, host.PostCalls);
        Assert.Equal(0, host.PostInterleavedCalls);
        Assert.All(grains.RequestedHostIds, id => Assert.Equal("neuron.host", id.Type.ToString()));
    }

    [Fact]
    public async Task IndexedReentrantAndStatelessNeuronsResolveCorrectHostGrainTypesOnFastPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new RecordingCatalog(_ => null);
        var host = new RecordingHost(interleavedResult: false);
        var grains = new RecordingGrainFactory(catalog, host);
        var registry = new NeuronTypeRegistry([typeof(DispatcherReentrantNeuron), typeof(DispatcherStatelessNeuron)]);
        var dispatcher = new OrleansNeuronDispatcher(grains, registry);

        var reentrantTarget = new NeuronId(new BrainId("alice"), "facts.dispatcher.reentrant", "r-1");
        var reentrantCall = new RuntimeInvocation(
            Behavior: string.Empty,
            Contract: "facts.dispatcher.reentrant",
            Key: reentrantTarget.ToString(),
            Method: nameof(IDispatcherReentrantNeuron.Ping),
            Arguments: []);

        var statelessTarget = new NeuronId(new BrainId("alice"), "facts.dispatcher.stateless", "s-1");
        var statelessCall = new RuntimeInvocation(
            Behavior: string.Empty,
            Contract: "facts.dispatcher.stateless",
            Key: statelessTarget.ToString(),
            Method: nameof(IDispatcherStatelessNeuron.Compute),
            Arguments: []);

        await dispatcher.InvokeAsync(reentrantTarget, reentrantCall, ct);
        await dispatcher.InvokeAsync(statelessTarget, statelessCall, ct);

        Assert.Equal(0, catalog.HostTypeCalls);
        Assert.Equal(0, host.IsInterleavedCalls);
        Assert.Equal("neuron.host.reentrant", grains.RequestedHostIds[0].Type.ToString());
        Assert.Equal("neuron.host.stateless", grains.RequestedHostIds[1].Type.ToString());
    }

    [Fact]
    public async Task UnindexedProcessFallsBackToCatalogAndHostIsInterleaved()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new RecordingCatalog(type => type == "remote.reentrant" ? "neuron.host.reentrant" : null);
        var host = new RecordingHost(interleavedResult: true);
        var grains = new RecordingGrainFactory(catalog, host);
        var dispatcher = new OrleansNeuronDispatcher(grains, types: null);

        var target = new NeuronId(new BrainId("alice"), "remote.reentrant", "1");
        var call = new RuntimeInvocation(
            Behavior: string.Empty,
            Contract: "remote.reentrant",
            Key: target.ToString(),
            Method: "Ping",
            Arguments: []);

        var result = await dispatcher.InvokeAsync(target, call, ct);
        await dispatcher.PostAsync(target, call);

        Assert.Equal("invoke-interleaved", result?.GetString());
        Assert.Equal(2, catalog.HostTypeCalls);
        Assert.Equal(2, host.IsInterleavedCalls);
        Assert.Equal(1, host.InvokeInterleavedCalls);
        Assert.Equal(1, host.PostInterleavedCalls);
        Assert.All(grains.RequestedHostIds, id => Assert.Equal("neuron.host.reentrant", id.Type.ToString()));
    }

    [Fact]
    public async Task UnindexedTypeWithRegistryDefaultsToAffineHostAndFallsBackToHostIsInterleaved()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new RecordingCatalog(_ => "neuron.host.reentrant");
        var host = new RecordingHost(interleavedResult: false);
        var grains = new RecordingGrainFactory(catalog, host);
        var registry = new NeuronTypeRegistry([typeof(DispatcherAffineNeuron)]);
        var dispatcher = new OrleansNeuronDispatcher(grains, registry);

        var target = new NeuronId(new BrainId("alice"), "dynamic.unit.state", "u-1");
        var call = new RuntimeInvocation(
            Behavior: string.Empty,
            Contract: "dynamic.unit.state",
            Key: target.ToString(),
            Method: "Read",
            Arguments: []);

        var result = await dispatcher.InvokeAsync(target, call, ct);

        Assert.Equal("invoke", result?.GetString());
        Assert.Equal(0, catalog.HostTypeCalls);
        Assert.Equal(1, host.IsInterleavedCalls);
        Assert.Equal(1, host.InvokeCalls);
        Assert.Equal("neuron.host", grains.RequestedHostIds[0].Type.ToString());
    }

    [Fact]
    public async Task RequireHostedThrowsWhenCatalogReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new RecordingCatalog(_ => null);
        var host = new RecordingHost(interleavedResult: false);
        var grains = new RecordingGrainFactory(catalog, host);
        var dispatcher = new OrleansNeuronDispatcher(grains, types: null);

        var target = new NeuronId(new BrainId("alice"), "missing.type", "1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.ResolveAsync(target, requireHosted: true, cancellationToken: ct).AsTask());
        Assert.Equal(1, catalog.HostTypeCalls);
    }
}
