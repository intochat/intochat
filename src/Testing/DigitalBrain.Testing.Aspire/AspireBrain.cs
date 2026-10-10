using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;

namespace DigitalBrain.Testing.Aspire;

// A brain in the AppHost's processes: HTTP through its edge, neurons through an Orleans client.
internal sealed class AspireBrain : ITestBrain, ITrackedBrain
{
    private readonly AspireTestSession _session;

    public AspireBrain(AspireTestSession session) => _session = session;

    public HttpClient HttpClient => _session.HttpClient;
    public TestExecutionOptions Execution => _session.Options;

    public IServiceProvider Services
        => throw new NotSupportedException("An Aspire brain's services live in the AppHost's processes; reach them through HTTP or neurons.");

    int ITrackedBrain.BufferCapacity => new SubscriptionOptions().BufferCapacity;
    void ITrackedBrain.Track(IAsyncDisposable resource) => _session.Lifetime.Own("observation", resource);

    public T Get<T>(string id) where T : class, INeuron => _session.Brains.Grain<T>(id);
    T IDigitalBrain.Get<T>(NeuronId id) => _session.Brains.Grain<T>(id.Name);
    public IBrain Self => _session.Brains.Open(BrainScope.CurrentId()).Self;
    public IKernelView Kernel => _session.Brains.Open(BrainScope.CurrentId()).Kernel;

    public Task<ISignalSubscription<T>> SubscribeAsync<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
        => _session.Subscriptions.SubscribeAsync<T>(source, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => _session.Brains.On<T>(neuron, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => _session.Brains.On<T>(neuron, onSubscribed, cancellationToken);

    public Task DeactivateAsync(IAddressable neuron, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("IManagementGrain.ForceActivationCollection(TimeSpan.Zero) scoped to the grain's type is not available.");

    // An Aspire restart is IDeployment.Restart: a product act, not a harness one.
    public Task RestartAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("An Aspire brain restarts through IDeployment.Restart, a product act.");

    public async Task<Person> PersonAsync(string name, CancellationToken cancellationToken = default)
    {
        var person = await Person.RegisterAsync(HttpClient, name, cancellationToken).ConfigureAwait(false);
        _session.Lifetime.Own("person", new Release(person.Dispose));
        return person;
    }

    // The AppHost's posture is its own; a Secured AppHost answers this owner's calls with 401.
    public Person Owner => Person.OpenOwner(HttpClient);

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private sealed class Release(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release();
            return ValueTask.CompletedTask;
        }
    }
}
