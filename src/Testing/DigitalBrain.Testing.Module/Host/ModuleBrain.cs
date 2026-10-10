using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.TestingHost;

namespace DigitalBrain.Testing.Module;

public sealed class ModuleBrain : IDigitalBrain, IBrainSubscriptions, IAsyncDisposable, ITrackedBrain
{
    private readonly InProcessTestCluster cluster;
    private readonly IDigitalBrains _brains;
    private readonly IBrainSubscriptions _subscriptions;
    private readonly TestSessionLifetime _lifetime;
    private readonly TestExecutionOptions _execution;
    internal ModuleBrain(InProcessTestCluster cluster, TestExecutionOptions execution, TestSessionLifetime lifetime)
    {
        this.cluster = cluster;
        _execution = execution;
        _brains = cluster.Client.ServiceProvider.GetRequiredService<IDigitalBrains>();
        _subscriptions = cluster.Client.ServiceProvider.GetRequiredService<IBrainSubscriptions>();
        _lifetime = lifetime;
        _lifetime.Own("client", (IAsyncDisposable)_brains);
    }
    int ITrackedBrain.BufferCapacity => cluster.Client.ServiceProvider.GetRequiredService<IOptions<SubscriptionOptions>>().Value.BufferCapacity;
    TestExecutionOptions ITrackedBrain.Execution => _execution;

    public IGrainFactory Grains => cluster.Client;

    private HttpClient? _http;

    // The composed modules' HTTP endpoints, when the builder asked for WithHttpEdge().
    public HttpClient HttpClient => _http ?? throw new InvalidOperationException(
        "This brain has no HTTP edge. Start it with ModuleTest.Create()...WithHttpEdge().");

    internal void AttachHttpEdge(global::Microsoft.AspNetCore.Builder.WebApplication edge)
    {
        _http = new HttpClient { BaseAddress = new Uri(edge.Urls.Single()) };
        _lifetime.Own("http-client", new DisposableClient(_http));
    }

    private sealed class DisposableClient(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // The active silo's service provider. Lets a test reach module-registered singletons that are
    // not grains (for example the broker gateway or a certification service).
    public IServiceProvider SiloServices => cluster.GetActiveSilos().First().ServiceProvider;

    internal IDigitalBrains Brains => _brains;

    void ITrackedBrain.Track(IAsyncDisposable resource) => _lifetime.Own("observation", resource);

    // Raw grain access for facts; programs resolve by NeuronId through IDigitalBrain.
    public T Get<T>(string id) where T : class, INeuron => _brains.Grain<T>(id);

    T IDigitalBrain.Get<T>(NeuronId id) => _brains.Grain<T>(id.Name);

    // The brain of the caller the fact stamped.
    public IBrain Self => _brains.Open(DigitalBrain.Framework.BrainScope.CurrentId()).Self;

    public IKernelView Kernel => _brains.Open(DigitalBrain.Framework.BrainScope.CurrentId()).Kernel;

    public Task<ISignalSubscription<T>> SubscribeAsync<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        => _subscriptions.SubscribeAsync<T>(source, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        => _brains.On<T>(source, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => _brains.On<T>(source, onSubscribed, cancellationToken);

    public Task DeactivateAsync(IAddressable neuron, CancellationToken cancellationToken = default)
        => cluster.DeactivateAsync(neuron.GetGrainId()).WaitAsync(cancellationToken);

    public async Task RestartSiloAsync(CancellationToken cancellationToken = default)
    {
        await cluster.RestartSiloAsync(cluster.GetActiveSilos().Single()).WaitAsync(cancellationToken).ConfigureAwait(false);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();

}
