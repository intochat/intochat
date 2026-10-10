using DigitalBrain.Framework;

namespace DigitalBrain.Testing;

public interface ITestBrain : IDigitalBrain, IBrainSubscriptions, IAsyncDisposable
{
    T Get<T>(string id) where T : class, INeuron;

    // The brain's HTTP edge, unauthenticated.
    HttpClient HttpClient { get; }

    // The host's services: in-process targets only; others throw NotSupportedException.
    IServiceProvider Services { get; }

    TestExecutionOptions Execution { get; }

    Task DeactivateAsync(IAddressable neuron, CancellationToken cancellationToken = default);

    // In-process: the host is rebuilt over the same memory stores, on the same HTTP address.
    // Neuron references and services taken before the restart belong to the stopped host: take them again after it.
    Task RestartAsync(CancellationToken cancellationToken = default);

    // Deleted in Task 20: the module tier's name for Services during the sweep.
    IServiceProvider SiloServices => Services;

    // Registers an account through the brain's HTTP edge; the brain owns its cookie client.
    Task<Person> PersonAsync(string name, CancellationToken cancellationToken = default);

    // The Open posture's synthetic owner: owner/owner, no cookie, the brain's plain client.
    Person Owner { get; }
}
