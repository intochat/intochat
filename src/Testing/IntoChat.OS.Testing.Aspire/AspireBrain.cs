using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Testing.Aspire;

// A brain in the AppHost's processes: HTTP through its edge, neurons through an Orleans client.
/// <summary>A brain in the AppHost's processes; public so Testing.Playwright can reach its session and lifetime.</summary>
public sealed class AspireBrain : ITestBrain, ITrackedBrain, IntoChat.OS.Runtime.IPlainClient
{
    private readonly AspireTestSession _session;
    private readonly TestSessionLifetime _lifetime;
    private readonly bool _ownsSession;
    private readonly IAsyncDisposable? _home;

    public AspireBrain(AspireTestSession session) : this(session, session.Lifetime, ownsSession: true) { }

    // The booting brain of AspireTarget: its temporary Home outlives the session's processes.
    internal AspireBrain(AspireTestSession session, IAsyncDisposable home) : this(session, session.Lifetime, ownsSession: true) => _home = home;

    private AspireBrain(AspireTestSession session, TestSessionLifetime lifetime, bool ownsSession)
    {
        _session = session;
        _lifetime = lifetime;
        _ownsSession = ownsSession;
    }

    /// <summary>The Aspire test session hosting this brain.</summary>
    public AspireTestSession Session => _session;

    // What this brain owns: the session for the booting brain, only its own observations for a lease.
    /// <summary>What this brain owns: the session for the booting brain, only its own observations for a lease.</summary>
    public TestSessionLifetime Lifetime => _lifetime;

    // A fact's view of a host it does not own: disposing it releases only what the fact observed.
    internal AspireBrain Lease() => new(_session, new TestSessionLifetime(_session.Options), ownsSession: false);

    public HttpClient HttpClient => _session.HttpClient;
    public TestExecutionOptions Execution => _session.Options;

    public IServiceProvider Services
        => throw new NotSupportedException("An Aspire brain's services live in the AppHost's processes; reach them through HTTP or neurons.");

    int ITrackedBrain.BufferCapacity => new SubscriptionOptions().BufferCapacity;
    void ITrackedBrain.Track(IAsyncDisposable resource) => _lifetime.Own("observation", resource);

    public T Get<T>(string id) where T : class, INeuron => _session.Brains.Grain<T>(id);
    T IIntoChatOS.Get<T>(NeuronId id) => _session.Brains.Grain<T>(id.Name);

    // 06-07b: plain neurons through the session's client, when it reaches them.
    private IntoChat.OS.Runtime.IPlainClient Plain => _session.Brains as IntoChat.OS.Runtime.IPlainClient
        ?? throw new NotSupportedException("This brain's client does not reach plain neurons.");

    object IntoChat.OS.Runtime.IPlainClient.GetPlain(Type contract, NeuronId id) => Plain.GetPlain(contract, id);

    IAsyncEnumerable<T> IntoChat.OS.Runtime.IPlainClient.OnPlain<T>(object source, Func<Task> onSubscribed, CancellationToken cancellationToken)
        => Plain.OnPlain<T>(source, onSubscribed, cancellationToken);
    public T Neuron<T>(NeuronId relative) where T : INeuron => _session.Brains.Grain<T>(relative.Within(BrainScope.CurrentId()).Name);
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

    public async ValueTask DisposeAsync()
    {
        try { await (_ownsSession ? _session.DisposeAsync() : _lifetime.DisposeAsync()).ConfigureAwait(false); }
        finally { if (_home is not null) { await _home.DisposeAsync().ConfigureAwait(false); } }
    }

    private sealed class Release(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release();
            return ValueTask.CompletedTask;
        }
    }
}
