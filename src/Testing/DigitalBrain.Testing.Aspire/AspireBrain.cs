using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;

namespace DigitalBrain.Testing.Aspire;

// A brain in the AppHost's processes: HTTP through its edge, neurons through an Orleans client.
internal sealed class AspireBrain : ITestBrain, ITrackedBrain
{
    private readonly AspireTestSession _session;
    private readonly TestSessionLifetime _lifetime;
    private readonly bool _ownsSession;

    public AspireBrain(AspireTestSession session) : this(session, session.Lifetime, ownsSession: true) { }

    private AspireBrain(AspireTestSession session, TestSessionLifetime lifetime, bool ownsSession)
    {
        _session = session;
        _lifetime = lifetime;
        _ownsSession = ownsSession;
    }

    internal AspireTestSession Session => _session;

    // What this brain owns: the session for the booting brain, only its own observations for a lease.
    internal TestSessionLifetime Lifetime => _lifetime;

    // A fact's view of a host it does not own: disposing it releases only what the fact observed.
    internal AspireBrain Lease() => new(_session, new TestSessionLifetime(_session.Options), ownsSession: false);

    public HttpClient HttpClient => _session.HttpClient;
    public TestExecutionOptions Execution => _session.Options;

    public IServiceProvider Services
        => throw new NotSupportedException("An Aspire brain's services live in the AppHost's processes; reach them through HTTP or neurons.");

    int ITrackedBrain.BufferCapacity => new SubscriptionOptions().BufferCapacity;
    void ITrackedBrain.Track(IAsyncDisposable resource) => _lifetime.Own("observation", resource);

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
        _lifetime.Own("person", new Release(person.Dispose));
        return person;
    }

    // The AppHost's posture is its own; a Secured AppHost answers this owner's calls with 401.
    public Person Owner => Person.OpenOwner(HttpClient);

    public ValueTask DisposeAsync() => _ownsSession ? _session.DisposeAsync() : _lifetime.DisposeAsync();

    private sealed class Release(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release();
            return ValueTask.CompletedTask;
        }
    }
}
